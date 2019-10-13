﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PrettyPrinter;

namespace DaeForth {
	public class MacroLocals {
		readonly List<Dictionary<string, Ir>> Scopes = new List<Dictionary<string, Ir>> {
			new Dictionary<string, Ir>()
		};
		Dictionary<string, Ir> Values = new Dictionary<string, Ir>();

		public Ir this[string name] {
			get => Values[name];
			set {
				Values[name] = value;
				foreach(var scope in Scopes) {
					if(!scope.ContainsKey(name)) continue;
					scope[name] = value;
					return;
				}
				Scopes.Last()[name] = value;
			}
		}
		
		public void PushScope() => Scopes.Add(new Dictionary<string, Ir>());

		public void PopScope() {
			Scopes.RemoveAt(Scopes.Count - 1);
			Values = Scopes.SelectMany(x => x).ToDictionary(x => x.Key, x => x.Value);
		}

		public bool TryGetValue(string name, out Ir value) => Values.TryGetValue(name, out value);
	}

	public class CompilerException : Exception {
		public CompilerException(string message) : base(message) {}
	}
	
	public class Compiler {
		public Tokenizer Tokenizer;
		readonly List<Func<Compiler, Token, bool>> StringHandlers = new List<Func<Compiler, Token, bool>>();
		readonly List<Func<Compiler, string, bool>> WordHandlers = new List<Func<Compiler, string, bool>>();

		readonly List<(string Prefix, Func<Compiler, string, Token, bool> Handler)> PrefixHandlers =
			new List<(string Prefix, Func<Compiler, string, Token, bool> Handler)>();

		public Stack<Ir> Stack = new Stack<Ir>();
		public readonly Stack<Stack<Ir>> StackStack = new Stack<Stack<Ir>>();

		public MacroLocals MacroLocals = new MacroLocals();
		
		public readonly Dictionary<string, Ir> Macros = new Dictionary<string, Ir>();
		
		public Dictionary<string, Type> Locals = new Dictionary<string, Type>();

		Token CurrentToken;
		
		public void Add(DaeforthModule module) {
			StringHandlers.AddRange(module.StringHandlers);
			WordHandlers.AddRange(module.WordHandlers);
			PrefixHandlers.AddRange(module.PrefixHandlers);
		}

		public void Compile(string filename, string source) {
			Tokenizer = new Tokenizer(filename, source);
			Tokenizer.Prefixes.AddRange(PrefixHandlers.Select(x => x.Prefix));

			foreach(var _token in Tokenizer) {
				var token = CurrentToken = _token;
				if(token.Prefixes.Count != 0) {
					var pftoken = token.PopPrefix();
					var pfx = token.Prefixes.First();
					var ph = PrefixHandlers.FirstOrDefault(x => x.Prefix == pfx);
					if(ph.Handler != null && ph.Handler(this, pfx, pftoken))
						continue;
					token = token.BakePrefixes(token.Prefixes);
				}

				try {
					switch(token.Type) {
						case TokenType.Word: {
							var wordHandled = false;

							if(Macros.TryGetValue(token.Value, out var macroBody)) {
								InjectToken(macroBody);
								InjectToken("call");
								wordHandled = true;
							}
							
							if(!wordHandled && MacroLocals.TryGetValue(token.Value, out var value)) {
								Push(value);
								wordHandled = true;
							}

							if(!wordHandled)
								foreach(var wh in WordHandlers) {
									if(!wh(this, token.Value)) continue;
									wordHandled = true;
									break;
								}

							if(!wordHandled)
								throw new CompilerException($"Unhandled word: {token}");
							break;
						}
						case TokenType.Value:
							Push(((ValueToken) token).Value);
							break;
						case TokenType.String: {
							var stringHandled = false;
							foreach(var sh in StringHandlers) {
								if(!sh(this, token)) continue;
								stringHandled = true;
								break;
							}

							if(!stringHandled)
								throw new CompilerException($"Unhandled string: {token}");
							break;
						}
					}
				} catch(CompilerException ce) {
					Console.Error.WriteLine(ce);
					Console.Error.WriteLine($"Exception in token {token}");
					DumpStack();
					Environment.Exit(1);
				}
			}
			
			DumpStack();
		}

		public void Warn(string message) =>
			Console.Error.WriteLine($"Warning near {CurrentToken}: {message}");

		public void DumpStack() {
			Console.WriteLine("~Stack~");
			foreach(var elem in Stack)
				elem.Print();
		}

		public void PushStack() {
			StackStack.Push(Stack);
			Stack = new Stack<Ir>();
		}

		public Stack<Ir> PopStack() {
			var cur = Stack;
			Stack = StackStack.Pop();
			return cur;
		}

		public void PushValue<T>(T value) => Stack.Push(value.Box());
		public void Push(params Ir[] value) {
			foreach(var val in value)
				Stack.Push(val);
		}
		public T Pop<T>() where T : Ir {
			if(Stack.Count == 0) throw new CompilerException("Stack underflow");
			var val = Stack.Pop();
			if(!(val is T tval)) throw new CompilerException($"Expected {typeof(T).Name} on stack, got {val.GetType().Name}");
			return tval;
		}
		public Ir Pop() => Pop<Ir>();
		public T TryPop<T>() where T : Ir {
			if(Stack.Count != 0 && Stack.Peek() is T) return (T) Stack.Pop();
			return null;
		}
		
		public (T1, T2) Pop<T1, T2>() where T1 : Ir where T2 : Ir {
			var _2 = Pop<T2>();
			var _1 = Pop<T1>();
			return (_1, _2);
		}
		public (T1, T2, T3) Pop<T1, T2, T3>() where T1 : Ir where T2 : Ir where T3 : Ir {
			var _3 = Pop<T3>();
			var _2 = Pop<T2>();
			var _1 = Pop<T1>();
			return (_1, _2, _3);
		}
		public (T1, T2, T3, T4) Pop<T1, T2, T3, T4>() where T1 : Ir where T2 : Ir where T3 : Ir where T4 : Ir {
			var _4 = Pop<T4>();
			var _3 = Pop<T3>();
			var _2 = Pop<T2>();
			var _1 = Pop<T1>();
			return (_1, _2, _3, _4);
		}

		public void InjectToken(Token token) => Tokenizer.Injected.Enqueue(token);
		public void InjectToken(Ir value) => InjectToken(new ValueToken(value));
		public void InjectToken(string token) => InjectToken(new Token(Location.Generated, Location.Generated, TokenType.Word, null, token));

		public Token ConsumeToken() {
			var enumerator = Tokenizer.GetEnumerator();
			enumerator.MoveNext();
			return enumerator.Current;
		}

		public void Output(Stream ostream) {
			
		}
	}
}