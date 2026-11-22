using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Dapper;
using Sde.Core.Records;

namespace Sde.Data.Infrastructure;

/// <summary>
/// Column metadata for a record type (its [Table], [Key], [AutoIncrement] attributes), plus generic,
/// parameterized CRUD. Replaces FEMA's SQLFactory, which concatenated values into Jet SQL strings.
/// </summary>
internal static class TableMap<T>
	where T : class, new()
{
	public static readonly string Table = typeof(T).GetCustomAttribute<TableAttribute>()?.Name
		?? throw new InvalidOperationException(typeof(T).Name + " has no [Table]");

	public static readonly PropertyInfo[] Columns = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<NotMappedAttribute>() == null)
		.ToArray();

	public static readonly PropertyInfo[] Keys = Columns.Where(p => p.GetCustomAttribute<KeyAttribute>() != null).ToArray();

	public static readonly PropertyInfo AutoIncrement = Columns.FirstOrDefault(p => p.GetCustomAttribute<AutoIncrementAttribute>() != null);

	private static readonly string SelectSql = $"SELECT {string.Join(", ", Columns.Select(c => Q(c.Name)))} FROM {Q(Table)}";

	private static readonly string KeyWhere = string.Join(" AND ", Keys.Select(k => $"{Q(k.Name)} = @{k.Name}"));

	// Built once per record type: an import writes hundreds of thousands of rows with the same statements.
	private static readonly string InsertSql = InsertFor(Columns);

	private static readonly PropertyInfo[] ColumnsWithoutAutoIncrement = Columns.Where(p => p != AutoIncrement).ToArray();

	private static readonly string InsertWithoutAutoIncrementSql = InsertFor(ColumnsWithoutAutoIncrement);

	private static readonly string InsertReturningSql = AutoIncrement == null
		? null
		: InsertWithoutAutoIncrementSql + " RETURNING " + Q(AutoIncrement.Name);

	private static readonly PropertyInfo[] NonKeyColumns = Columns.Where(p => !Keys.Contains(p)).ToArray();

	private static readonly string UpdateSql =
		$"UPDATE {Q(Table)} SET {string.Join(", ", NonKeyColumns.Select(p => $"{Q(p.Name)} = @{p.Name}"))} WHERE {KeyWhere}";

	// Single-statement upsert (not INSERT OR REPLACE, which would fire cascading deletes).
	private static readonly string UpsertSql = Keys.Length == 0
		? null
		: NonKeyColumns.Length == 0
			? $"{InsertSql} ON CONFLICT ({string.Join(", ", Keys.Select(k => Q(k.Name)))}) DO NOTHING"
			: $"{InsertSql} ON CONFLICT ({string.Join(", ", Keys.Select(k => Q(k.Name)))}) DO UPDATE SET {string.Join(", ", NonKeyColumns.Select(p => $"{Q(p.Name)} = excluded.{Q(p.Name)}"))}";

	private static readonly HashSet<PropertyInfo> JetScaleStringColumns = Columns
		.Where(p => p.PropertyType == typeof(string) && p.GetCustomAttribute<JetScaleAttribute>() != null)
		.ToHashSet();

	private static readonly bool HasJetScaleStrings = JetScaleStringColumns.Count > 0;

	private static readonly Func<T, bool> IsAutoIncrementUnset = AutoIncrement == null ? null : BuildIsUnset(AutoIncrement);

	private static readonly Action<T, long> SetAutoIncrement = AutoIncrement == null ? null : BuildSetLong(AutoIncrement);

	private static readonly Func<T, bool> HasAllKeys = BuildHasAllKeys();

	public static readonly Func<T, T> Clone = BuildClone();

	private static string InsertFor(PropertyInfo[] columns) =>
		$"INSERT INTO {Q(Table)} ({string.Join(", ", columns.Select(p => Q(p.Name)))}) VALUES ({string.Join(", ", columns.Select(p => "@" + p.Name))})";

	public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

	public static IEnumerable<T> Select(IDbConnection c, string where = null, object args = null, IDbTransaction tx = null, string orderBy = null)
	{
		string sql = SelectSql + (where == null ? "" : " WHERE " + where) + (orderBy == null ? "" : " ORDER BY " + orderBy);
		return c.Query<T>(sql, args, tx).Select(JetScaling.Apply);
	}

	public static T Get(IDbConnection c, object key, IDbTransaction tx = null) =>
		Select(c, KeyWhere, key, tx).FirstOrDefault();

	/// <summary>Inserts the record (rounding Currency/Numeric values to their Jet scale first). AutoNumber keys are filled in.</summary>
	public static void Insert(IDbConnection c, T record, IDbTransaction tx = null)
	{
		JetScaling.Apply(record);
		bool assignId = IsAutoIncrementUnset != null && IsAutoIncrementUnset(record);
		if (assignId)
		{
			object args = HasJetScaleStrings ? Parameters(record, ColumnsWithoutAutoIncrement) : record;
			long id = c.ExecuteScalar<long>(InsertReturningSql, args, tx);
			SetAutoIncrement(record, id);
		}
		else
		{
			object args = HasJetScaleStrings ? Parameters(record, Columns) : record;
			c.Execute(InsertSql, args, tx);
		}
	}

	/// <summary>Updates every column of the row with the record's key. Returns the number of rows changed.</summary>
	public static int Update(IDbConnection c, T record, IDbTransaction tx = null)
	{
		JetScaling.Apply(record);
		object args = HasJetScaleStrings ? Parameters(record, Columns) : record;
		return c.Execute(UpdateSql, args, tx);
	}

	/// <summary>Update if the key exists, otherwise insert. (Not INSERT OR REPLACE, which would fire cascading deletes.)</summary>
	public static void Upsert(IDbConnection c, T record, IDbTransaction tx = null)
	{
		if (HasAllKeys(record) && UpsertSql != null)
		{
			JetScaling.Apply(record);
			object args = HasJetScaleStrings ? Parameters(record, Columns) : record;
			c.Execute(UpsertSql, args, tx);
		}
		else
		{
			Insert(c, record, tx);
		}
	}

	public static int Delete(IDbConnection c, string where, object args, IDbTransaction tx = null) =>
		c.Execute($"DELETE FROM {Q(Table)} WHERE {where}", args, tx);

	private static DynamicParameters Parameters(T record, PropertyInfo[] props)
	{
		DynamicParameters p = new DynamicParameters();
		for (int i = 0; i < props.Length; i++)
		{
			PropertyInfo prop = props[i];
			object value = prop.GetValue(record);
			// [JetScale] string columns (ElementPercentage.ItemCost/DamageValues) hold numbers: store them as numbers.
			if (value is string s && JetScaleStringColumns.Contains(prop))
			{
				value = JetScaling.TryParse(s, out decimal d) ? d : null;
			}
			p.Add(prop.Name, value);
		}
		return p;
	}

	private static Func<T, bool> BuildIsUnset(PropertyInfo prop)
	{
		ParameterExpression target = Expression.Parameter(typeof(T), "record");
		MemberExpression read = Expression.Property(target, prop);
		Expression check = IsUnsetExpression(read, prop.PropertyType);
		return Expression.Lambda<Func<T, bool>>(check, target).Compile();
	}

	private static Func<T, bool> BuildHasAllKeys()
	{
		if (Keys.Length == 0)
		{
			return static _ => false;
		}
		ParameterExpression target = Expression.Parameter(typeof(T), "record");
		Expression combined = null;
		foreach (PropertyInfo key in Keys)
		{
			Expression set = Expression.Not(IsUnsetExpression(Expression.Property(target, key), key.PropertyType));
			combined = combined == null ? set : Expression.AndAlso(combined, set);
		}
		return Expression.Lambda<Func<T, bool>>(combined, target).Compile();
	}

	private static Expression IsUnsetExpression(Expression value, Type type)
	{
		if (type == typeof(int))
		{
			return Expression.Equal(value, Expression.Constant(0));
		}
		if (type == typeof(long))
		{
			return Expression.Equal(value, Expression.Constant(0L));
		}
		if (type == typeof(Guid))
		{
			return Expression.Equal(value, Expression.Constant(Guid.Empty));
		}
		if (Nullable.GetUnderlyingType(type) is Type underlying)
		{
			Expression noVal = Expression.Not(Expression.Property(value, "HasValue"));
			Expression inner = IsUnsetExpression(Expression.Property(value, "Value"), underlying);
			return Expression.OrElse(noVal, inner);
		}
		if (!type.IsValueType)
		{
			return Expression.Equal(value, Expression.Constant(null, type));
		}
		return Expression.Constant(false);
	}

	private static Action<T, long> BuildSetLong(PropertyInfo prop)
	{
		ParameterExpression target = Expression.Parameter(typeof(T), "record");
		ParameterExpression id = Expression.Parameter(typeof(long), "id");
		Type underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
		Expression converted = Expression.Convert(id, underlying);
		if (prop.PropertyType != underlying)
		{
			converted = Expression.Convert(converted, prop.PropertyType);
		}
		return Expression.Lambda<Action<T, long>>(Expression.Assign(Expression.Property(target, prop), converted), target, id).Compile();
	}

	private static Func<T, T> BuildClone()
	{
		ParameterExpression source = Expression.Parameter(typeof(T), "source");
		MemberBinding[] bindings = new MemberBinding[Columns.Length];
		for (int i = 0; i < Columns.Length; i++)
		{
			bindings[i] = Expression.Bind(Columns[i], Expression.Property(source, Columns[i]));
		}
		MemberInitExpression init = Expression.MemberInit(Expression.New(typeof(T)), bindings);
		return Expression.Lambda<Func<T, T>>(init, source).Compile();
	}
}
