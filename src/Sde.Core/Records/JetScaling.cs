using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Sde.Core.Records;

/// <summary>
/// Reproduces how Jet stored Currency/Numeric(p,s) values: rounded to the column's scale on write, handed back
/// by OleDb with exactly that scale (Currency 1234.5 → 1234.5000). Applied by the data layer after reads and
/// before writes, and by the SDE exchange writer, so numbers look exactly as they did in FEMA's SDE.
/// </summary>
public static class JetScaling
{
	private static readonly ConcurrentDictionary<Type, (PropertyInfo Prop, int Scale)[]> _cache = new();
	private static readonly ConcurrentDictionary<Type, Action<object>> _appliers = new();
	private static readonly MethodInfo WithScaleMethod = typeof(JetScaling).GetMethod(nameof(WithScale), BindingFlags.Public | BindingFlags.Static);
	private static readonly MethodInfo ScaleStringMethod = typeof(JetScaling).GetMethod(nameof(ScaleString), BindingFlags.NonPublic | BindingFlags.Static);

	/// <summary>Rounds (half-to-even, like Jet's CCur) and sets the scale of every [JetScale] property on <paramref name="record"/>.</summary>
	public static T Apply<T>(T record)
		where T : class
	{
		if (record == null)
		{
			return null;
		}
		if (typeof(T) == record.GetType())
		{
			TypedApplier<T>.Apply(record);
		}
		else
		{
			_appliers.GetOrAdd(record.GetType(), BuildUntypedApplier)(record);
		}
		return record;
	}

	public static void ApplyAll<T>(IEnumerable<T> records)
		where T : class
	{
		foreach (T r in records)
		{
			Apply(r);
		}
	}

	/// <summary>Rounds to <paramref name="scale"/> decimals and pads with trailing zeros to exactly that scale.</summary>
	public static decimal WithScale(decimal value, int scale)
	{
		decimal rounded = Math.Round(value, scale, MidpointRounding.ToEven);
		// Adding a zero that has the target scale forces the result's scale up to it (1234.5 + 0.0000 = 1234.5000).
		return rounded + new decimal(0, 0, 0, false, (byte)scale);
	}

	public static bool TryParse(string s, out decimal value)
	{
		if (string.IsNullOrEmpty(s))
		{
			value = 0m;
			return false;
		}
		ReadOnlySpan<char> span = s.Contains('$') ? s.Replace("$", "").AsSpan().Trim() : s.AsSpan().Trim();
		return decimal.TryParse(span, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
	}

	public static (PropertyInfo Prop, int Scale)[] ScaledProperties(Type type) =>
		_cache.GetOrAdd(type, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => (p, p.GetCustomAttribute<JetScaleAttribute>()))
			.Where(x => x.Item2 != null)
			.Select(x => (x.p, x.Item2.Scale))
			.ToArray());

	private static string ScaleString(string s, int scale) =>
		s != null && TryParse(s, out decimal parsed) ? WithScale(parsed, scale).ToString(CultureInfo.InvariantCulture) : s;

	private static Action<T> BuildTypedApplier<T>()
		where T : class
	{
		(PropertyInfo Prop, int Scale)[] props = ScaledProperties(typeof(T));
		if (props.Length == 0)
		{
			return static _ => { };
		}
		ParameterExpression target = Expression.Parameter(typeof(T), "record");
		List<Expression> body = BuildAssignments(target, props);
		return Expression.Lambda<Action<T>>(Expression.Block(body), target).Compile();
	}

	private static Action<object> BuildUntypedApplier(Type type)
	{
		(PropertyInfo Prop, int Scale)[] props = ScaledProperties(type);
		if (props.Length == 0)
		{
			return static _ => { };
		}
		ParameterExpression obj = Expression.Parameter(typeof(object), "record");
		ParameterExpression typed = Expression.Variable(type, "typed");
		List<Expression> body = new List<Expression>(props.Length + 1)
		{
			Expression.Assign(typed, Expression.Convert(obj, type)),
		};
		body.AddRange(BuildAssignments(typed, props));
		return Expression.Lambda<Action<object>>(Expression.Block(new[] { typed }, body), obj).Compile();
	}

	private static List<Expression> BuildAssignments(ParameterExpression target, (PropertyInfo Prop, int Scale)[] props)
	{
		List<Expression> body = new List<Expression>(props.Length);
		foreach ((PropertyInfo prop, int scale) in props)
		{
			MemberExpression member = Expression.Property(target, prop);
			ConstantExpression scaleConst = Expression.Constant(scale);
			if (prop.PropertyType == typeof(decimal))
			{
				body.Add(Expression.Assign(member, Expression.Call(WithScaleMethod, member, scaleConst)));
			}
			else if (prop.PropertyType == typeof(decimal?))
			{
				ParameterExpression v = Expression.Variable(typeof(decimal?), "v");
				Expression check = Expression.IfThen(
					Expression.Property(v, nameof(Nullable<decimal>.HasValue)),
					Expression.Assign(member, Expression.Convert(
						Expression.Call(WithScaleMethod, Expression.Property(v, nameof(Nullable<decimal>.Value)), scaleConst),
						typeof(decimal?))));
				body.Add(Expression.Block(new[] { v }, Expression.Assign(v, member), check));
			}
			else if (prop.PropertyType == typeof(string))
			{
				body.Add(Expression.Assign(member, Expression.Call(ScaleStringMethod, member, scaleConst)));
			}
		}
		return body;
	}

	private static class TypedApplier<T>
		where T : class
	{
		public static readonly Action<T> Apply = BuildTypedApplier<T>();
	}
}
