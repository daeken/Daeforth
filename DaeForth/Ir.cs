using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PrettyPrinter;

namespace DaeForth {
	public enum BinaryOp {
		Add, 
		Subtract, 
		Multiply, 
		Divide, 
		Modulus, 
		Equal, 
		NotEqual, 
		LessThanOrEqual, 
		LessThan, 
		GreaterThan, 
		GreaterThanOrEqual, 
		LogicalOr, 
		LogicalAnd
	}

	public enum UnaryOp {
		BitwiseNegate, 
		LogicalNegate, 
		Minus // TODO: I hate this name
	}
	
	public abstract class Ir {
		public Type Type;
		public virtual bool IsConstant => false;
		public virtual bool IsCheap => IsConstant;
		
		public static implicit operator Ir(List<Ir> e) => new List(e.ToList());
		
		public virtual Ir CastTo(Type type) => Type == type ? this : new Cast(type, this);

		public class Assignment : Ir {
			public Ir Lhs;
			public Ir Value;
		}

		public class List : Ir, IList<Ir> {
			readonly IList<Ir> Elements = new List<Ir>();

			public List() {}
			public List(IEnumerable<Ir> values) => Elements = values.ToList();

			public IEnumerator<Ir> GetEnumerator() => Elements.GetEnumerator();
			IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

			public void Add(Ir item) => Elements.Add(item);
			public void Clear() => Elements.Clear();
			public bool Contains(Ir item) => Elements.Contains(item);
			public void CopyTo(Ir[] array, int arrayIndex) => Elements.CopyTo(array, arrayIndex);
			public bool Remove(Ir item) => Elements.Remove(item);

			public int Count => Elements.Count;
			public bool IsReadOnly => false;
			public int IndexOf(Ir item) => Elements.IndexOf(item);

			public void Insert(int index, Ir item) => Elements.Insert(index, item);

			public void RemoveAt(int index) => Elements.RemoveAt(index);

			public Ir this[int index] {
				get => Elements[index];
				set => Elements[index] = value;
			}

			public override bool IsConstant => Elements.All(x => x.IsConstant);
		}

		public class BinaryOperation : Ir {
			public Ir Left, Right;
			public BinaryOp Op;
		}

		public class UnaryOperation : Ir {
			public Ir Value;
			public UnaryOp Op;
		}

		public class Map : Ir {
			public Ir Functor; // x -> y
			public Ir Target;
		}

		public class Reduce : Ir {
			public Ir Functor; // x x -> y
			public Ir Target;
		}

		public class Block : Ir {
			public List Body;
		}

		public class If : Ir {
			public Ir Cond, A, B;
		}

		public class For : Ir {
			public Ir Iterator, Count, Body;
		}

		public class Return : Ir {
			public Ir Value; // Optional
		}

		public class Break : Ir { }

		public class Continue : Ir { }

		public class Call : Ir {
			public Ir Functor;
			public List Arguments;
		}

		public class CallWord : Ir {
			public (string Name, Type Return, Type[] Arguments) Word;
			public List Arguments;
		}

		public class Identifier : Ir {
			public string Name;

			public Identifier(string name) => Name = name;
			
			public override bool IsConstant => false;
			public override bool IsCheap => true;

			public override string ToString() => $"Identifier({Name.ToPrettyString()})";
		}

		public class Cast : Ir {
			public Ir Value;

			public Cast(Type type, Ir value) {
				Type = type;
				Value = value;
			}
		}

		public interface IConstValue {
			object Value { get; }
		}

		public class ConstValue<T> : Ir, IConstValue {
			public readonly T Value;
			object IConstValue.Value => Value;

			public ConstValue(T value) {
				Value = value;
				Type = typeof(T);
			}

			public static implicit operator T(ConstValue<T> cv) => cv.Value;
			public static implicit operator ConstValue<T>(T v) => new ConstValue<T>(v);
			
			public override bool IsConstant => true;

			public override Ir CastTo(Type type) {
				if(Type == type) return this;

				if(this is ConstValue<float> fcv && type == typeof(int))
					return ((int) (float) fcv).Box();
				if(this is ConstValue<int> icv && type == typeof(float))
					return ((float) (int) icv).Box();
				throw new NotImplementedException($"Unknown cast from {typeof(T).Name} to {type.Name}");
			}

			public override string ToString() => $"ConstValue<{typeof(T).Name}>({Value.ToPrettyString()})";
		}

		public class Swizzle : Ir {
			public readonly Ir Value;
			public readonly int[] Fields;

			public Swizzle(Ir value, params int[] fields) {
				Value = value;
				Fields = fields;
			}

			public Swizzle(Ir value, IEnumerable<int> fields) {
				Value = value;
				Fields = fields.ToArray();
			}
		}

		public class MemberAccess : Ir {
			public readonly Ir Value;
			public readonly string Member;
			public override bool IsConstant => Value.IsConstant;
			public override bool IsCheap => Value.IsCheap;

			public MemberAccess(Ir value, string member) {
				Value = value;
				Member = member;
			}
		}

		public class Ternary : Ir {
			public Ir Cond, A, B;
		}
	}
}