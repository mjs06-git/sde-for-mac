using System.Globalization;
using System.Text.Json.Serialization;
using Sde.Gis.Features;

namespace Sde.Gis.Analysis;

/// <summary>How a field's value is compared with the condition's value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttributeOperator
{
	/// <summary>=</summary>
	Equals,

	/// <summary>≠</summary>
	NotEquals,

	/// <summary>&lt;</summary>
	LessThan,

	/// <summary>≤</summary>
	LessOrEqual,

	/// <summary>&gt;</summary>
	GreaterThan,

	/// <summary>≥</summary>
	GreaterOrEqual,

	/// <summary>The text contains the value (ignoring case).</summary>
	Contains,

	/// <summary>The text starts with the value (ignoring case).</summary>
	StartsWith,

	/// <summary>The field has no value (or only spaces).</summary>
	IsEmpty,

	/// <summary>The field has a value.</summary>
	IsNotEmpty,
}

/// <summary>Whether all conditions must hold, or any one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttributeCombine
{
	And,
	Or,
}

/// <summary>One condition: field, operator, value (the value as typed; it is read as the field's type).</summary>
public sealed class AttributeCondition
{
	public AttributeCondition()
	{
	}

	public AttributeCondition(string field, AttributeOperator op, string value = null)
	{
		Field = field;
		Operator = op;
		Value = value;
	}

	public string Field { get; set; }

	public AttributeOperator Operator { get; set; }

	public string Value { get; set; }

	/// <summary>The symbol or words shown for an operator: =, ≠, &lt;, ≤, &gt;, ≥, contains, starts with, is empty, is not empty.</summary>
	public static string Label(AttributeOperator op) => op switch
	{
		AttributeOperator.Equals => "=",
		AttributeOperator.NotEquals => "≠",
		AttributeOperator.LessThan => "<",
		AttributeOperator.LessOrEqual => "≤",
		AttributeOperator.GreaterThan => ">",
		AttributeOperator.GreaterOrEqual => "≥",
		AttributeOperator.Contains => "contains",
		AttributeOperator.StartsWith => "starts with",
		AttributeOperator.IsEmpty => "is empty",
		_ => "is not empty",
	};

	public override string ToString() => Operator is AttributeOperator.IsEmpty or AttributeOperator.IsNotEmpty
		? $"{Field} {Label(Operator)}"
		: $"{Field} {Label(Operator)} {Value}";
}

/// <summary>
/// Select by attribute: conditions joined by AND or OR. Numbers and dates compare as numbers and dates; text compares
/// without regard to case. A condition on a field the layer doesn't have is an error, so a typo never selects nothing
/// silently. JSON-serialisable, so a query can be saved or sent by an AI assistant.
/// </summary>
public sealed class AttributeQuery
{
	public List<AttributeCondition> Conditions { get; set; } = new List<AttributeCondition>();

	public AttributeCombine Combine { get; set; } = AttributeCombine.And;

	/// <summary>The features of the layer that match.</summary>
	public List<Feature> Select(FeatureLayer layer)
	{
		ArgumentNullException.ThrowIfNull(layer);
		Validate(layer.Fields);
		if (Conditions.Count == 0)
		{
			return new List<Feature>(layer.Features);
		}
		CompiledCondition[] compiled = Compile(layer.Fields);
		List<Feature> matched = new List<Feature>();
		bool requireAll = Combine == AttributeCombine.And;
		for (int i = 0; i < layer.Features.Count; i++)
		{
			Feature f = layer.Features[i];
			if (MatchesCompiled(f, compiled, requireAll))
			{
				matched.Add(f);
			}
		}
		return matched;
	}

	/// <summary>Throws <see cref="GisException"/> when a condition names a field that isn't there or a value that can't be read as the field's type.</summary>
	public void Validate(FieldList fields)
	{
		foreach (AttributeCondition c in Conditions)
		{
			LayerField field = fields[c.Field];
			if (field == null)
			{
				throw new GisException($"This layer has no field called {c.Field}.", c.ToString());
			}
			bool needsValue = c.Operator is not (AttributeOperator.IsEmpty or AttributeOperator.IsNotEmpty or AttributeOperator.Contains or AttributeOperator.StartsWith);
			if (needsValue && field.Type != FieldType.Text && !FieldValues.TryConvert(field.Type, c.Value, out object parsed) )
			{
				throw new GisException($"{field.DisplayName} holds {Describe(field.Type)}, so \"{c.Value}\" can't be compared with it.", c.ToString());
			}
			if (needsValue && field.Type != FieldType.Text && string.IsNullOrWhiteSpace(c.Value))
			{
				throw new GisException($"Enter a value to compare {field.DisplayName} with.", c.ToString());
			}
		}
	}

	/// <summary>True when the feature meets the conditions (an empty query matches everything).</summary>
	public bool Matches(Feature feature)
	{
		int count = Conditions.Count;
		if (count == 0)
		{
			return true;
		}
		if (Combine == AttributeCombine.And)
		{
			for (int i = 0; i < count; i++)
			{
				if (!Test(feature, Conditions[i]))
				{
					return false;
				}
			}
			return true;
		}
		for (int i = 0; i < count; i++)
		{
			if (Test(feature, Conditions[i]))
			{
				return true;
			}
		}
		return false;
	}

	private CompiledCondition[] Compile(FieldList fields)
	{
		CompiledCondition[] result = new CompiledCondition[Conditions.Count];
		for (int i = 0; i < Conditions.Count; i++)
		{
			AttributeCondition c = Conditions[i];
			int index = fields.IndexOf(c.Field);
			FieldType type = index >= 0 ? fields[index].Type : FieldType.Text;
			bool ordering = c.Operator is not (AttributeOperator.Equals or AttributeOperator.NotEquals);
			double? num = null;
			bool? flag = null;
			DateTime? dt = null;
			string raw = c.Value ?? "";
			string trimmed = raw.Trim();
			double? textNum = null;
			switch (type)
			{
				case FieldType.Integer:
				case FieldType.Real:
					if (FieldValues.TryConvert(FieldType.Real, c.Value, out object n) && n != null)
					{
						num = (double)n;
					}
					break;
				case FieldType.Boolean:
					if (FieldValues.TryConvert(FieldType.Boolean, c.Value, out object b) && b != null)
					{
						flag = (bool)b;
					}
					break;
				case FieldType.Date:
				case FieldType.DateTime:
					if (FieldValues.TryConvert(type, c.Value, out object d) && d != null)
					{
						dt = (DateTime)d;
					}
					break;
				default:
					if (ordering && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
					{
						textNum = y;
					}
					break;
			}
			result[i] = new CompiledCondition(index, type, c.Operator, ordering, raw, trimmed, num, flag, dt, textNum);
		}
		return result;
	}

	private static bool MatchesCompiled(Feature feature, CompiledCondition[] conditions, bool requireAll)
	{
		if (requireAll)
		{
			for (int i = 0; i < conditions.Length; i++)
			{
				if (!TestCompiled(feature, conditions[i]))
				{
					return false;
				}
			}
			return true;
		}
		for (int i = 0; i < conditions.Length; i++)
		{
			if (TestCompiled(feature, conditions[i]))
			{
				return true;
			}
		}
		return false;
	}

	private static bool TestCompiled(Feature feature, in CompiledCondition c)
	{
		if (c.FieldIndex < 0)
		{
			return false;
		}
		object value = feature[c.FieldIndex];
		bool empty = value == null || (value is string s && string.IsNullOrWhiteSpace(s));
		switch (c.Operator)
		{
			case AttributeOperator.IsEmpty:
				return empty;
			case AttributeOperator.IsNotEmpty:
				return !empty;
			case AttributeOperator.Contains:
				return !empty && FieldValues.Format(value).Contains(c.RawValue, StringComparison.OrdinalIgnoreCase);
			case AttributeOperator.StartsWith:
				return !empty && FieldValues.Format(value).StartsWith(c.RawValue, StringComparison.OrdinalIgnoreCase);
		}
		if (empty)
		{
			return c.Operator == AttributeOperator.NotEquals && c.RawValue.Length > 0;
		}
		int? order = CompareCompiled(c, value);
		if (order == null)
		{
			return c.Operator == AttributeOperator.NotEquals;
		}
		return c.Operator switch
		{
			AttributeOperator.Equals => order == 0,
			AttributeOperator.NotEquals => order != 0,
			AttributeOperator.LessThan => order < 0,
			AttributeOperator.LessOrEqual => order <= 0,
			AttributeOperator.GreaterThan => order > 0,
			_ => order >= 0,
		};
	}

	private static int? CompareCompiled(in CompiledCondition c, object value)
	{
		switch (c.Type)
		{
			case FieldType.Integer:
			case FieldType.Real:
				if (c.NumberValue is not double rightNum)
				{
					return null;
				}
				double leftNum = value switch
				{
					double d => d,
					long l => l,
					int i => i,
					float f => f,
					decimal m => (double)m,
					_ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
				};
				return leftNum.CompareTo(rightNum);
			case FieldType.Boolean:
				return c.BoolValue is bool rightBool ? ((bool)value).CompareTo(rightBool) : null;
			case FieldType.Date:
			case FieldType.DateTime:
				if (c.DateValue is not DateTime rightDate)
				{
					return null;
				}
				DateTime leftDate = (DateTime)value;
				return c.Type == FieldType.Date || rightDate.TimeOfDay == TimeSpan.Zero ? leftDate.Date.CompareTo(rightDate.Date) : leftDate.CompareTo(rightDate);
			default:
				string text = value is string sv ? sv.Trim() : FieldValues.Format(value).Trim();
				if (c.Ordering && c.TextNumberValue is double y && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
				{
					return x.CompareTo(y);
				}
				return string.Compare(text, c.TrimmedValue, StringComparison.OrdinalIgnoreCase);
		}
	}

	private readonly record struct CompiledCondition(
		int FieldIndex, FieldType Type, AttributeOperator Operator, bool Ordering,
		string RawValue, string TrimmedValue, double? NumberValue, bool? BoolValue, DateTime? DateValue, double? TextNumberValue);

	public override string ToString() => string.Join(Combine == AttributeCombine.And ? " AND " : " OR ", Conditions);

	private static bool Test(Feature feature, AttributeCondition c)
	{
		int index = feature.Fields.IndexOf(c.Field);
		if (index < 0)
		{
			return false;
		}
		LayerField field = feature.Fields[index];
		object value = feature[index];
		bool empty = value == null || (value is string s && string.IsNullOrWhiteSpace(s));
		switch (c.Operator)
		{
			case AttributeOperator.IsEmpty:
				return empty;
			case AttributeOperator.IsNotEmpty:
				return !empty;
			case AttributeOperator.Contains:
				return !empty && FieldValues.Format(value).Contains(c.Value ?? "", StringComparison.OrdinalIgnoreCase);
			case AttributeOperator.StartsWith:
				return !empty && FieldValues.Format(value).StartsWith(c.Value ?? "", StringComparison.OrdinalIgnoreCase);
		}
		if (empty)
		{
			// An empty value is neither equal to nor greater or less than anything, but it is "not equal" to a value.
			return c.Operator == AttributeOperator.NotEquals && !string.IsNullOrEmpty(c.Value);
		}
		bool ordering = c.Operator is not (AttributeOperator.Equals or AttributeOperator.NotEquals);
		int? order = Compare(field.Type, value, c.Value, ordering);
		if (order == null)
		{
			return c.Operator == AttributeOperator.NotEquals;
		}
		return c.Operator switch
		{
			AttributeOperator.Equals => order == 0,
			AttributeOperator.NotEquals => order != 0,
			AttributeOperator.LessThan => order < 0,
			AttributeOperator.LessOrEqual => order <= 0,
			AttributeOperator.GreaterThan => order > 0,
			_ => order >= 0,
		};
	}

	/// <summary>The order of a stored value against the typed value, or null when they can't be compared.</summary>
	private static int? Compare(FieldType type, object value, string typed, bool ordering)
	{
		switch (type)
		{
			case FieldType.Integer:
			case FieldType.Real:
				if (!FieldValues.TryConvert(FieldType.Real, typed, out object n) || n == null)
				{
					return null;
				}
				return Convert.ToDouble(value, CultureInfo.InvariantCulture).CompareTo((double)n);
			case FieldType.Boolean:
				if (!FieldValues.TryConvert(FieldType.Boolean, typed, out object b) || b == null)
				{
					return null;
				}
				return ((bool)value).CompareTo((bool)b);
			case FieldType.Date:
			case FieldType.DateTime:
				if (!FieldValues.TryConvert(type, typed, out object d) || d == null)
				{
					return null;
				}
				DateTime left = (DateTime)value;
				DateTime right = (DateTime)d;
				return type == FieldType.Date || right.TimeOfDay == TimeSpan.Zero ? left.Date.CompareTo(right.Date) : left.CompareTo(right);
			default:
				string text = FieldValues.Format(value).Trim();
				string other = (typed ?? "").Trim();
				// For less and greater, text that holds numbers (such as "12" and "9") compares as numbers. Equality stays
				// textual, so "000121" isn't equal to "121".
				if (ordering && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
					&& double.TryParse(other, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
				{
					return x.CompareTo(y);
				}
				return string.Compare(text, other, StringComparison.OrdinalIgnoreCase);
		}
	}

	private static string Describe(FieldType type) => type switch
	{
		FieldType.Integer => "whole numbers",
		FieldType.Real => "numbers",
		FieldType.Boolean => "yes or no",
		FieldType.Date => "dates",
		FieldType.DateTime => "dates and times",
		_ => "text",
	};
}
