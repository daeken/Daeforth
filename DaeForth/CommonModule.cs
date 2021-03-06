﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using MoreLinq;
using PrettyPrinter;
using static System.Console;

namespace DaeForth {
	public class CommonModule : DaeforthModule {
		static readonly CommonModule _Module = new CommonModule();
		public override DaeforthModule Module => _Module;

		static readonly Regex IntRegex = new Regex(@"^#(-?[0-9][0-9_]*|0x[0-9a-fA-F][0-9a-fA-F_]*|0b[01][01_]+)$");
		static readonly Regex FloatRegex = new Regex(@"^-?([0-9][0-9_]*\.[0-9_]*|\.[0-9][0-9_]*|[0-9][0-9_]*)$");

		public CommonModule() {
			Ir ParseBlock(Compiler compiler, bool compile = true) {
				var btok = new Ir.List();
				var depth = 0;
				foreach(var elem in compiler.Tokenizer) {
					if(elem.Type == TokenType.Word && elem.Prefixes != null) {
						if(elem.Value == "{")
							depth++;
						else if(elem.Value == "}" && depth-- == 0)
							break;
					}
					btok.Add(elem.Box());
				}
				return compile ? (Ir) EnsureCompiled(btok) : btok;
			}

			var macroIndex = 0;

			Ir.Block EnsureCompiled(Ir pblock) {
				if(pblock is Ir.Block ib) return ib;
				if(!(pblock is Ir.List ilist)) throw new NotSupportedException();
				
				var index = ++macroIndex;
				var list = ilist.Select(x =>
					x is Ir.ConstValue<Token> cvt ? cvt.Value :
					x is Ir.ConstValue<ValueToken> cvvt ? cvvt.Value : throw new NotImplementedException()).ToList();
				var args = new List<string>();
				var rets = new List<string>();
				if(list.Count > 2 && list[0].Type == TokenType.Word && list[0].RawValue == "(|") {
					var inArgs = true;
					var i = 1;
					for(; i < list.Count - 1; ++i) {
						var tok = list[i];
						if(tok.RawValue == "|)") break;
						switch(tok.RawValue) {
							case "->":
								if(inArgs) inArgs = false;
								else throw new CompilerException("-> must only appear once in signatures");
								break;
							case { } x when inArgs && x.Contains(':'):
								var parts = x.Split(':');
								if(parts.Length != 2)
									throw new CompilerException(
										": must appear only once in each argument declaration");
								args.Add(parts[0] == "" ? null : parts[0]);
								break;
							case { } x when inArgs:
								args.Add(x);
								break;
							case { } x when !inArgs:
								rets.Add(x);
								break;
						}
					}

					if(i == list.Count || list[i].RawValue != "|)")
						throw new CompilerException("Missing |) at end of signature");
					if(args.Any(x => x != null) && args.Any(x => x == null))
						throw new CompilerException(
							"All arguments must be either named or unnamed, not a mix of both");
					list = list.Skip(i + 1).ToList();
				}

				if(args.Count == 0 && list.Any(x => x.Type == TokenType.Word && x.Value == "_"))
					args.Add("_");

				var storing = args.Where(x => x != null && x[0] == '$').Select(x => x.Substring(1)).ToList();
				args = args.Select(x => x?[0] == '$' ? x.Substring(1) : x).ToList();
				var rename = args.Where(x => x != null).Select(x => (x, $"__macro_{index}_{x}")).ToDictionary();

				if(rename.Count != 0)
					list = list.Select(x =>
						x.Type == TokenType.Word && x.Prefixes != null && rename.ContainsKey(x.Value)
							? new Token(x.StartLocation, x.EndLocation, x.Type, x.Prefixes, rename[x.Value],
								string.Join("", x.Prefixes) + rename[x.Value])
							: x).ToList();

				list = args.Where(x => x != null).Select(x => {
					var pfx = storing.Contains(x) ? "=>$" : "=>";
					return new Token(Location.Generated,
						Location.Generated, TokenType.Word, new List<string> { pfx }, rename[x], $"{pfx}{rename[x]}");
				}).Reverse().Concat(list).ToList();
				list = new[] { Token.Generate("~~push-macro-scope") }.Concat(list)
					.Concat(new[] { Token.Generate("~~pop-macro-scope") }).ToList();
				return new Ir.Block { Body = new Ir.List(list.Select(x => x.Box())) };
			}
			
			AddPrefixHandler("&", (compiler, token) => compiler.InjectToken(token.Box()));
			
			AddPrefixHandler("/", (compiler, token) => {
				if(token.Value == "{")
					compiler.Push(ParseBlock(compiler));
				else
					compiler.InjectToken(token.Box());
				compiler.InjectToken("map");
			});
			
			AddPrefixHandler("//", (compiler, token) => {
				if(token.Value == "{")
					compiler.Push(ParseBlock(compiler));
				else
					compiler.InjectToken(token.Box());
				compiler.InjectToken("map");
				compiler.InjectToken("drop");
			});
			
			AddPrefixHandler("\\", (compiler, token) => {
				if(token.Value == "{")
					compiler.Push(ParseBlock(compiler));
				else
					compiler.InjectToken(token.Box());
				compiler.InjectToken("reduce");
			});
			
			AddPrefixHandler("~", (compiler, token) => {
				if(token.RawValue.StartsWith("~")) return false;
				var cond = compiler.TryPop<Ir.ConstValue<bool>>();
				if(cond == null) throw new CompilerException("Conditional compilation requires constant expression");
				var block =
					token.Value == "{"
						? ParseBlock(compiler)
						: token.Box();
				if(!cond.Value) return true;
				compiler.InjectToken(block);
				compiler.InjectToken("call");
				return true;
			});
			
			AddPrefixHandler("=>", (compiler, token) => {
				if(token.Value == "[")
					return false;
				compiler.MacroLocals[token.Value] = compiler.Pop();
				return true;
			});
			AddPrefixHandler("=>$",
				(compiler, token) => compiler.MacroLocals[token.Value] = compiler.EnsureCheap(compiler.Pop()));
			AddWordHandler("=[", compiler => {
				var v = compiler.Pop();
				var list = EnsureList(compiler, v);
				if(list == null) { compiler.Push(v); return false; }

				var names = new List<string>();
				while(true) {
					var token = compiler.ConsumeToken();
					if(token.RawValue == "]")
						break;
					names.Add(token.RawValue);
				}
				
				if(names.Count != list.Count)
					throw new Exception($"Mismatch in list assignment: expected {names.Count} elements but got {list.Count}");
				
				list.Zip(names).ForEach(x => compiler.AssignVariable(x.Second, x.First));

				return true;
			});
			AddWordHandler("=>[", compiler => {
				var v = compiler.Pop();
				var list = EnsureList(compiler, v);
				if(list == null) { compiler.Push(v); return false; }

				var names = new List<string>();
				while(true) {
					var token = compiler.ConsumeToken();
					if(token.RawValue == "]")
						break;
					names.Add(token.RawValue);
				}
				
				if(names.Count != list.Count)
					throw new Exception($"Mismatch in list assignment: expected {names.Count} elements but got {list.Count}");
				
				list.Zip(names).ForEach(x => compiler.MacroLocals[x.Second] = x.First);

				return true;
			});
			AddPrefixHandler("=", (compiler, token) => {
				if(token.Value == "=" || token.Value == "[") return false; // == or =[

				var name = token.Value;
				var value = compiler.Pop();
				compiler.AssignVariable(name, value);
				return true;
			});
			
			AddPrefixHandler("!", (compiler, token) => {
				if(token.Value == "=") return false; // !=
				compiler.InjectToken("dup");
				compiler.InjectToken(token);
				return true;
			});
			
			AddPrefixHandler("*", (compiler, token) => {
				compiler.InjectToken(token);
				compiler.InjectToken("call");
			});

			AddPrefixHandler("@",
				(compiler, token) => compiler.InjectToken(compiler.TypeFromString(token.Value).Box()));
			
			AddWordHandler("uniform", compiler => {
				var type = compiler.TryPop<Ir.ConstValue<Type>>();
				if(type == null) throw new CompilerException("Uniform must be applied to a type");
				compiler.Push(typeof(UniformType<>).MakeGenericType(type).Box());
			});

			AddWordHandler("varying", compiler => {
				var type = compiler.TryPop<Ir.ConstValue<Type>>();
				if(type == null) throw new CompilerException("Varying must be applied to a type");
				compiler.Push(typeof(VaryingType<>).MakeGenericType(type).Box());
			});

			AddWordHandler("output-variable", compiler => {
				var type = compiler.TryPop<Ir.ConstValue<Type>>();
				if(type == null) throw new CompilerException("Output must be applied to a type");
				compiler.Push(typeof(OutputType<>).MakeGenericType(type).Box());
			});

			AddWordHandler("input-variable", compiler => {
				var type = compiler.TryPop<Ir.ConstValue<Type>>();
				if(type == null) throw new CompilerException("Input must be applied to a type");
				compiler.Push(typeof(InputType<>).MakeGenericType(type).Box());
			});

			AddWordHandler("global", compiler => {
				var type = compiler.TryPop<Ir.ConstValue<Type>>();
				if(type == null) throw new CompilerException("Global must be applied to a type");
				compiler.Push(typeof(GlobalType<>).MakeGenericType(type).Box());
			});

			AddWordHandler("((", compiler => {
				var depth = 0;
				foreach(var elem in compiler.Tokenizer) {
					if(elem.Type == TokenType.String)
						continue;
					if(elem.Value == "((")
						depth++;
					else if(elem.Value == "))" && depth-- == 0)
						break;
				}
			});
			
			AddWordHandler("{", compiler => compiler.Push(ParseBlock(compiler, compile: false)));
			
			AddWordHandler("~~push-macro-scope", compiler => compiler.MacroLocals.PushScope());
			AddWordHandler("~~pop-macro-scope", compiler => compiler.MacroLocals.PopScope());
			
			// Caller for bare or compiled blocks on the stack
			AddWordHandler("call", compiler => {
				var btok = (Ir) compiler.TryPop<Ir.List>() ?? compiler.TryPop<Ir.Block>();
				if(btok == null) return false;
				var block = EnsureCompiled(btok).Body;
				foreach(var val in block) {
					if(val is Ir.ConstValue<Token> tok)
						compiler.InjectToken(tok.Value);
					else if(val is Ir.ConstValue<ValueToken> vtok)
						compiler.InjectToken(vtok.Value);
					else
						Debug.Fail("Non-token in block");
				}
				return true;
			});
			
			// Caller for a token on the stack
			AddWordHandler("call", compiler => {
				var tok = compiler.TryPop<Ir.ConstValue<Token>>();
				if(tok == null) return false;
				compiler.InjectToken("~~push-macro-scope");
				compiler.InjectToken(tok.Value);
				compiler.InjectToken("~~pop-macro-scope");
				return true;
			});
			
			// Caller for a token on the stack
			AddWordHandler("call-bare", compiler => {
				var tok = compiler.TryPop<Ir.ConstValue<Token>>();
				if(tok == null) return false;
				compiler.InjectToken(tok.Value);
				return true;
			});
			
			AddWordHandler("call-collect", async compiler => {
				var block = compiler.Pop();
				compiler.CurrentWord.StmtStack.Push(new List<Ir>());
				compiler.InjectToken(block);
				compiler.InjectToken("call");
				await compiler.RunToHere();
				compiler.Push(new Ir.List(compiler.CurrentWord.StmtStack.Pop()));
			});

			AddWordHandler("call-collect-with", async compiler => {
				var value = compiler.Pop();
				var block = compiler.Pop();
				compiler.CurrentWord.StmtStack.Push(new List<Ir>());
				compiler.InjectToken(value);
				compiler.InjectToken(block);
				compiler.InjectToken("call");
				await compiler.RunToHere();
				compiler.Push(new Ir.List(compiler.CurrentWord.StmtStack.Pop()));
			});

			AddWordHandler("cif", compiler => {
				var cond = compiler.TryPop<Ir.ConstValue<bool>>();
				if(cond == null) throw new CompilerException("Conditional compilation requires constant expression");
				var else_ = compiler.Pop();
				var if_ = compiler.Pop();
				compiler.InjectToken(cond.Value ? if_ : else_);
				compiler.InjectToken("call");
			});
			
			AddWordHandler("select", compiler => {
				var cond = compiler.Pop();
				var b = compiler.Pop();
				var a = compiler.Pop();
				if(cond is Ir.ConstValue<bool> cvb)
					compiler.Push(cvb.Value ? a : b);
				else {
					a = Compiler.CanonicalizeValue(a);
					b = Compiler.CanonicalizeValue(b);
					if(a.Type != b.Type)
						throw new CompilerException("Both options to runtime select must have the same type");
					compiler.Push(new Ir.Ternary { Cond = cond, A = a, B = b, Type = a.Type });
				}
			});
			
			AddWordHandler("~~run-to-here", compiler =>
				compiler.TaskStack.Pop().SetResult(null));
			
			AddWordHandler("if", async compiler => {
				var cond = compiler.Pop();
				var else_ = compiler.Pop();
				var if_ = compiler.Pop();
				compiler.InjectToken(if_);
				compiler.InjectToken("call-collect");
				await compiler.RunToHere();
				var ifBlock = compiler.Pop();
				compiler.InjectToken(else_);
				compiler.InjectToken("call-collect");
				await compiler.RunToHere();
				var elseBlock = compiler.Pop();
				compiler.AddStmt(new Ir.If { Cond = cond, A = ifBlock, B = elseBlock });
			});
			
			AddWordHandler("def-word", compiler => {
				var name = compiler.TryPop<Ir.ConstValue<Token>>()?.Value?.Value ??
				           compiler.TryPop<Ir.ConstValue<string>>()?.Value;
				if(name == null) throw new CompilerException("Word name must be quoted identifier or string");
				if(compiler.Words.ContainsKey(name)) compiler.Warn($"Redefining word '{name}'");
				compiler.Words[name] = EnsureCompiled(compiler.Pop());
			});
			
			AddWordHandler(":", compiler => {
				var name = compiler.ConsumeToken().RawValue;
				var elems = new Ir.List();
				foreach(var elem in compiler.Tokenizer) {
					if(elem.Type == TokenType.Word && elem.RawValue == "((") {
						var depth = 0;
						foreach(var selem in compiler.Tokenizer) {
							if(selem.Type == TokenType.String)
								continue;
							if(selem.RawValue == "((")
								depth++;
							else if(selem.RawValue == "))" && depth-- == 0)
								break;
						}
					} else if(elem.Type == TokenType.Word && elem.RawValue == ";")
						break;
					else
						elems.Add(elem.Box());
				}
				
				if(compiler.Words.ContainsKey(name)) compiler.Warn($"Redefining word '{name}'");
				compiler.Words[name] = EnsureCompiled(elems);
			});
			
			AddWordHandler("~~call-word", async compiler => {
				var name = compiler.Pop<Ir.ConstValue<string>>().Value;
				var block = compiler.Pop();
				compiler.WordStack.Push(compiler.CurrentWord);
				compiler.CurrentWord = new WordContext { SurrogateStack = new SurrogateStack(compiler.Stack) };
				compiler.PushStack();
				compiler.InjectToken(block);
				compiler.InjectToken("call");
				await compiler.RunToHere();
				
				var word = compiler.CurrentWord;
				compiler.CurrentWord = compiler.WordStack.Pop();
				var ns = compiler.PopStack();
				if(ns.Count > 1) throw new CompilerException($"Words can only return 0 or 1 values -- {ns.Count} on the stack");
				Type ret = null;
				if(ns.Count == 1) {
					var retVal = Compiler.CanonicalizeValue(ns.Pop());
					word.Body.Add(new Ir.Return { Type = ret = retVal.Type, Value = retVal });
				}

				var key = (name, ret, word.SurrogateStack.Arguments.ToArray());
				compiler.CompiledWords[key] = word;

				var actualArgs = word.SurrogateStack.Arguments.Select(x => Compiler.CanonicalizeValue(compiler.Pop()))
					.Reverse();
				var invoke = new Ir.CallWord {
					Type = ret, Word = key, Arguments = new Ir.List(actualArgs)
				};
				
				if(ret == null)
					compiler.AddStmt(invoke);
				else
					compiler.Push(invoke);
			});
			
			AddWordHandler("def-macro", compiler => {
				var name = compiler.TryPop<Ir.ConstValue<Token>>()?.Value?.Value ??
				           compiler.TryPop<Ir.ConstValue<string>>()?.Value;
				if(name == null) throw new CompilerException("Macro name must be quoted identifier or string");
				if(compiler.Macros.ContainsKey(name)) compiler.Warn($"Redefining macro '{name}'");
				compiler.Macros[name] = EnsureCompiled(compiler.Pop());
			});
			
			AddWordHandler("def-prefix", compiler => {
				var name = compiler.TryPop<Ir.ConstValue<Token>>()?.Value?.Value ??
				           compiler.TryPop<Ir.ConstValue<string>>()?.Value;
				if(name == null) throw new CompilerException("Macro name must be quoted identifier or string");
				if(compiler.Prefixes.ContainsKey(name)) compiler.Warn($"Redefining prefix '{name}'");
				compiler.Prefixes[name] = EnsureCompiled(compiler.Pop());
			});
			
			AddWordHandler(":m", compiler => {
				var name = compiler.ConsumeToken().RawValue;
				var elems = new Ir.List();
				foreach(var elem in compiler.Tokenizer) {
					if(elem.Type == TokenType.Word && elem.RawValue == "((") {
						var depth = 0;
						foreach(var selem in compiler.Tokenizer) {
							if(selem.Type == TokenType.String)
								continue;
							if(selem.RawValue == "((")
								depth++;
							else if(selem.RawValue == "))" && depth-- == 0)
								break;
						}
					} else if(elem.Type == TokenType.Word && elem.RawValue == ";")
						break;
					else
						elems.Add(elem.Box());
				}
				
				if(compiler.Macros.ContainsKey(name)) compiler.Warn($"Redefining macro '{name}'");
				compiler.Macros[name] = EnsureCompiled(elems);
			});
			
			AddWordHandler(":p", compiler => {
				var name = compiler.ConsumeToken().RawValue;
				var elems = new Ir.List();
				foreach(var elem in compiler.Tokenizer) {
					if(elem.Type == TokenType.Word && elem.RawValue == "((") {
						var depth = 0;
						foreach(var selem in compiler.Tokenizer) {
							if(selem.Type == TokenType.String)
								continue;
							if(selem.RawValue == "((")
								depth++;
							else if(selem.RawValue == "))" && depth-- == 0)
								break;
						}
					} else if(elem.Type == TokenType.Word && elem.RawValue == ";")
						break;
					else
						elems.Add(elem.Box());
				}
				
				if(compiler.Prefixes.ContainsKey(name)) compiler.Warn($"Redefining prefix '{name}'");
				compiler.Prefixes[name] = EnsureCompiled(elems);
			});
			
			AddWordHandler("swap", compiler => {
				var (a, b) = compiler.Pop<Ir, Ir>();
				compiler.Push(b, a);
			});
			
			AddWordHandler("dup", compiler => {
				var v = compiler.EnsureCheap(compiler.Pop());
				compiler.Push(v, v);
			});
			
			AddWordHandler("drop", compiler => compiler.Pop());
			
			AddWordHandler("[", compiler => compiler.PushStack());
			AddWordHandler("]", compiler => compiler.Push(compiler.PopStack().ToList()));

			Ir.List EnsureList(Compiler compiler, Ir value) {
				if(value is Ir.List list) return list;
				
				Ir.List Swizzle(params string[] members) {
					value = compiler.EnsureCheap(value);
					return new Ir.List(members.Select(x => new Ir.MemberAccess(value, x) { Type = typeof(float) })) { Type = value.Type };
				}

				var type = value.Type;
				if(type == typeof(Vec2)) return Swizzle("x", "y");
				if(type == typeof(Vec3)) return Swizzle("x", "y", "z");
				if(type == typeof(Vec4)) return Swizzle("x", "y", "z", "w");
				return null;
			}
			
			AddWordHandler("reverse", compiler => {
				var v = compiler.Pop();
				var list = EnsureList(compiler, v);
				if(list == null) { compiler.Push(v); return false; }
				compiler.Push(new Ir.List(list.Reverse()));
				return true;
			});
			
			AddWordHandler("map", compiler => {
				var block = compiler.Pop();
				var v = compiler.Pop();
				var list = EnsureList(compiler, v);
				if(list == null) { compiler.Push(block, v); return false; }

				if(list.Count == 0) {
					compiler.Push(list);
					return true;
				}
				
				compiler.InjectToken("[");
				foreach(var elem in list) {
					compiler.InjectToken(elem);
					compiler.InjectToken(block);
					compiler.InjectToken("call");
				}
				compiler.InjectToken("]");
				
				return true;
			});
			
			AddWordHandler("reduce", compiler => {
				var block = compiler.Pop();
				var v = compiler.Pop();
				var list = EnsureList(compiler, v);
				if(list == null) { compiler.Push(block, v); return false; }

				if(list.Count == 0) throw new CompilerException("Reduce on empty list");
				if(list.Count == 1) {
					compiler.Push(list[0]);
					return true;
				}
				
				compiler.InjectToken(list[0]);
				for(var i = 1; i < list.Count; ++i) {
					compiler.InjectToken(list[i]);
					compiler.InjectToken(block);
					compiler.InjectToken("call");
				}
				
				return true;
			});
			
			AddWordHandler("flatten", compiler => {
				var list = compiler.TryPop<Ir.List>();
				if(list == null) return false;
				
				compiler.InjectToken("[");
				foreach(var elem in list)
					if(elem is Ir.List sublist)
						foreach(var selem in sublist)
							compiler.InjectToken(selem);
					else
						compiler.InjectToken(elem);
				compiler.InjectToken("]");
				
				return true;
			});
			
			AddWordHandler("flatten-deep", compiler => {
				var list = compiler.TryPop<Ir.List>();
				if(list == null) return false;

				void Flatten(Ir.List slist) {
					foreach(var elem in slist)
						if(elem is Ir.List sublist)
							Flatten(sublist);
						else
							compiler.InjectToken(elem);
				}
				
				compiler.InjectToken("[");
				Flatten(list);
				compiler.InjectToken("]");

				return true;
			});
			
			AddWordHandler("mtimes", compiler => {
				var count = compiler.TryPop<Ir.ConstValue<int>>();
				if(count == null) throw new CompilerException("Count must be a constant integer");
				if(count.Value < 0) throw new CompilerException("Count must be positive");
				var block = compiler.Pop();
				for(var i = 0; i < count; ++i) {
					compiler.InjectToken(i.Box());
					compiler.InjectToken(block);
					compiler.InjectToken("call");
				}
			});
			
			AddWordHandler("times", async compiler => {
				var count = compiler.Pop();
				var block = compiler.Pop();
				var iter = new Ir.Identifier(compiler.TempName) { Type = typeof(int) };
				compiler.InjectToken(block);
				compiler.InjectToken(iter);
				compiler.InjectToken("call-collect-with");
				await compiler.RunToHere();
				var body = compiler.Pop();
				compiler.AddStmt(new Ir.For { Iterator = iter, Count = count, Body = body });
			});
			
			AddWordHandler("break", compiler => compiler.AddStmt(new Ir.Break()));
			AddWordHandler("continue", compiler => compiler.AddStmt(new Ir.Continue()));
			
			AddWordHandler((compiler, token) => {
				if(IntRegex.IsMatch(token)) {
					token = token.Substring(1).Replace("_", "");
					if(token.StartsWith("0x"))
						compiler.PushValue(Convert.ToInt32(token.Substring(2), 16));
					else if(token.StartsWith("0b"))
						compiler.PushValue(Convert.ToInt32(token.Substring(2), 2));
					else
						compiler.PushValue(int.Parse(token));
				} else if(FloatRegex.IsMatch(token))
					compiler.PushValue(float.Parse(token.Replace("_", "")));
				else
					return false;
				return true;
			});
			
			AddWordHandler("false", compiler => compiler.PushValue(false));
			AddWordHandler("true", compiler => compiler.PushValue(true));
			
			AddWordHandler("print", compiler => compiler.ErrorWriter.WriteLine(compiler.Pop().ToPrettyString()));
			AddWordHandler("assert", compiler => {
				var cond = compiler.TryPop<Ir.ConstValue<bool>>();
				if(cond == null) throw new CompilerException("Assertion on non-const or non-bool value");
				if(!cond.Value) throw new CompilerException("Assertion failed");
			});
			
			AddWordHandler("concat", compiler => {
				var tokenB = compiler.TryPop<Ir.ConstValue<Token>>();
				if(tokenB == null) return false;
				var tokenA = compiler.TryPop<Ir.ConstValue<Token>>();
				if(tokenA == null) {
					compiler.Push(tokenB);
					return false;
				}
				
				compiler.PushValue(Token.Generate(tokenA.Value.RawValue + tokenB.Value.RawValue));

				return true;
			});
			
			AddWordHandler("bind-to-scope", compiler => {
				var btok = (Ir) compiler.TryPop<Ir.List>() ?? compiler.TryPop<Ir.Block>();
				if(btok == null) return false;
				var block = EnsureCompiled(btok).Body;
				var toInject = new List<Ir>();
				var injected = new HashSet<string>();
				foreach(var elem in block)
					if(elem is Ir.ConstValue<Token> token &&
					   !injected.Contains(token.Value.Value) &&
					   compiler.MacroLocals.TryGetValue(token.Value.Value, out var value)) {
						injected.Add(token.Value.Value);
						toInject.Add(new ValueToken(value).Box());
						toInject.Add(Token.Generate("=>" + token.Value.Value).Box());
					}
				compiler.Push(new Ir.List(block.Take(1).Concat((IEnumerable<Ir>) toInject).Concat(block.Skip(1))));
				return true;
			});
			
			AddWordHandler("~~nop", _ => {});
			AddPrefixHandler(",", (_, __) => false);
			
			AddPrefixHandler("`", (compiler, pftoken) => {
				if(pftoken.RawValue != "{")
					throw new NotSupportedException("Quasiquote blocks must begin with `{");
				compiler.Push(new Ir.List(((Ir.List) ParseBlock(compiler, compile: false)).Select(elem => {
					if(!(elem is Ir.ConstValue<Token> token) || token.Value.RawValue == null ||
					   !token.Value.RawValue.Contains(",")) return elem;
					if(token.Value.RawValue.Contains(",,"))
						return Token.Generate(token.Value.RawValue.Replace(",,", ",")).Box();
					var pieces = token.Value.RawValue.Split(',');
					if(pieces.Length != 2 || pieces[1].Length == 0) return elem;
					var value = compiler.MacroLocals[pieces[1]];
					if(value is Ir.ConstValue<Token> cvt) {
						return Token.Generate(pieces[0] + cvt.Value.RawValue).Box();
					}
					if(pieces[0].Length == 0)
						return new ValueToken(value).Box();
					throw new NotSupportedException(
						"Attempted to use non-token value in pseudoquote token with prefixes");
				})));
			});
			
			AddWordHandler("is-block?", compiler => {
				var value = compiler.Pop();
				try {
					compiler.PushValue(EnsureCompiled(value) != null);
				} catch(Exception) {
					compiler.PushValue(false);
				}
			});
			
			AddWordHandler("zip", compiler => {
				var v = compiler.Pop();
				var listB = EnsureList(compiler, v);
				if(listB == null) { compiler.Push(v); return false; }
				v = compiler.Pop();
				var listA = EnsureList(compiler, v);
				if(listA == null) { compiler.Push(listB, v); return false; }
				
				if(listA.Count != listB.Count)
					throw new NotSupportedException($"Zip requires two sequences of equal length; got {listA.Count} and {listB.Count}");
				
				compiler.Push(new Ir.List(listA.Zip(listB).Select(x => new Ir.List(new[] { x.First, x.Second }))));
				return true;
			});
		}
	}
}