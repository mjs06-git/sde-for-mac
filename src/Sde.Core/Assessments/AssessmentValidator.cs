using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sde.Core.Records;

namespace Sde.Core.Assessments;

public enum IssueSeverity
{
	/// <summary>Red pin: needed for a valid Substantial Damage determination.</summary>
	Required = 1,
	/// <summary>Yellow pin.</summary>
	Recommended = 2,
	/// <summary>Green pin (owner/mailing details).</summary>
	Suggested = 3,
}

/// <summary>One failed validation rule.</summary>
public sealed record ValidationIssue(int RuleId, IssueSeverity Severity, string Tab, string FieldName, string ControlName, string Model, string Message);

/// <summary>
/// Port of BLL DataValidationViewService.RunValidationCheck (decomp/BLL/BLL.Service/DataValidationViewService.cs),
/// driven by FEMA's ValidationRule table. Each rule names a field ("ControlName") on the Property or the Assessment
/// and a check (ValidationTypeID). Like the original, issues are advisory: they never block saving.
/// </summary>
public sealed class AssessmentValidator
{
	private static readonly ConditionalWeakTable<ReferenceData, CompiledRule[]> CompiledCache = new();
	private readonly ReferenceData _ref;
	private readonly CompiledRule[] _rules;

	public AssessmentValidator(ReferenceData referenceData)
	{
		_ref = referenceData;
		_rules = CompiledCache.GetValue(referenceData, CompileRules);
	}

	/// <param name="checkLatLong">The "Lat/Long Validation" preference; lat/long-in-state checks only run when on.</param>
	/// <param name="today">For "not in the future" checks (DateTime.Today when null).</param>
	public List<ValidationIssue> Validate(AssessmentFile file, bool checkLatLong, DateTime? today = null)
	{
		DateTime todayDate = (today ?? DateTime.Today).Date;
		PropertyRecord p = file.Property;
		AssessmentRecord a = file.Assessment;
		List<ValidationIssue> issues = new();
		foreach (CompiledRule rule in _rules)
		{
			object obj = rule.IsProperty ? rule.GetValue(p) : rule.GetValue(a);
			bool flag = false;
			switch (rule.ValidationTypeID)
			{
			case 1:
				flag = IsBlank(obj);
				break;
			case 2:
				flag = NotGreaterThanZero(obj);
				break;
			case 3:
				if (obj != null && a.DateofInspection.HasValue)
				{
					flag = Convert.ToDateTime(obj).Date > a.DateofInspection.Value.Date;
				}
				break;
			case 4:
				if (obj != null)
				{
					flag = Convert.ToDateTime(obj) > todayDate;
				}
				break;
			case 5:
				if (p.StateID > 0 && checkLatLong)
				{
					double lat = Convert.ToDouble(obj);
					if (lat > 0.0)
					{
						flag = LatitudeOutsideState(lat, p.StateID.Value);
					}
				}
				break;
			case 6:
				if (p.StateID > 0 && checkLatLong)
				{
					double lon = Convert.ToDouble(obj);
					if (lon < 0.0)
					{
						flag = LongitudeOutsideState(lon, p.StateID.Value);
					}
				}
				break;
			case 7:
				flag = obj != null && Convert.ToDouble(obj) <= rule.Threshold;
				break;
			case 8:
				if (a.CauseofDamageID is LookupIds.CauseFlood or LookupIds.CauseFloodAndWind)
				{
					flag = obj == null || !a.DurationOfFloodUnitsID.HasValue;
				}
				break;
			case 9:
			case 10:
				if (a.CauseofDamageID is LookupIds.CauseFlood or LookupIds.CauseFloodAndWind)
				{
					flag = obj == null || (a.DepthOfFloodAboveFFE.HasValue && Convert.ToDecimal(obj) < a.DepthOfFloodAboveFFE.Value);
				}
				break;
			case 11:
				if (a.CauseofDamageID == LookupIds.CauseOther)
				{
					flag = IsBlank(obj);
				}
				break;
			case 12:
				if (p.StructureTypeID == 2)
				{
					flag = NotGreaterThanZero(obj);
				}
				break;
			case 13:
				if (p.StructureTypeID == 1)
				{
					flag = NotGreaterThanZero(obj);
				}
				break;
			case 14:
				flag = obj != null && a.DateDamageOccurred.HasValue && Convert.ToDateTime(obj) < a.DateDamageOccurred.Value;
				break;
			case 15:
				if (!a.PhysicalDamage)
				{
					flag = obj == null || Convert.ToDecimal(obj) == 0m;
				}
				break;
			}
			if (flag)
			{
				issues.Add(rule.Issue);
			}
		}
		return issues;
	}

	private static CompiledRule[] CompileRules(ReferenceData referenceData)
	{
		List<CompiledRule> list = new List<CompiledRule>(referenceData.ValidationRules.Count);
		foreach (ValidationRuleRecord rule in referenceData.ValidationRules.OrderBy(r => r.ValidationID))
		{
			bool isProperty = string.Equals(rule.ModelName, "property", StringComparison.OrdinalIgnoreCase);
			Type targetType = isProperty ? typeof(PropertyRecord) : typeof(AssessmentRecord);
			PropertyInfo prop = targetType.GetProperty(rule.ControlName);
			if (prop == null)
			{
				continue;
			}
			ParameterExpression param = Expression.Parameter(typeof(object), "target");
			Expression read = Expression.Property(Expression.Convert(param, targetType), prop);
			Func<object, object> getter = Expression.Lambda<Func<object, object>>(Expression.Convert(read, typeof(object)), param).Compile();
			double threshold = rule.ValidationTypeID == 7 ? Convert.ToDouble(rule.ValidationType, CultureInfo.InvariantCulture) : 0.0;
			ValidationIssue issue = new ValidationIssue(rule.ValidationID, Severity(rule.PinColor), rule.Tab, rule.FieldName, rule.ControlName, isProperty ? "Property" : "Assessment", rule.PinMessage);
			list.Add(new CompiledRule(isProperty, rule.ValidationTypeID, threshold, getter, issue));
		}
		return list.ToArray();
	}

	private sealed record CompiledRule(bool IsProperty, int ValidationTypeID, double Threshold, Func<object, object> GetValue, ValidationIssue Issue);

	private static bool IsBlank(object value) => value == null || (value is string s ? s.Length == 0 : string.IsNullOrEmpty(value.ToString()));

	// Original: Convert.ToInt32(value) > 0 (so 0.4 counts as not > 0, as it did in SDE).
	private static bool NotGreaterThanZero(object value) => IsBlank(value) || Convert.ToInt32(value) <= 0;

	/// <summary>PinColor is e.g. "1, Red, #7FFF0000".</summary>
	private static IssueSeverity Severity(string pinColor) =>
		int.TryParse(pinColor?.Split(',')[0].Trim(), out int n) && Enum.IsDefined(typeof(IssueSeverity), n) ? (IssueSeverity)n : IssueSeverity.Recommended;

	/// <summary>checkLatitude: inside [LatMin, LatMax]; American Samoa (54, southern hemisphere) has min/max swapped in the table.</summary>
	public bool LatitudeOutsideState(double latitude, int stateId)
	{
		LatLongValidationRecord box = _ref.LatLongBox(stateId);
		if (box == null)
		{
			return false;
		}
		if (stateId != 54)
		{
			return !(box.LatMin <= latitude && box.LatMax >= latitude);
		}
		return !(box.LatMax <= latitude && box.LatMin >= latitude);
	}

	/// <summary>checkLongitude: compares magnitudes (US longitudes are negative); Alaska (2) crosses the 180° line.</summary>
	public bool LongitudeOutsideState(double longitude, int stateId)
	{
		LatLongValidationRecord box = _ref.LatLongBox(stateId);
		if (box == null)
		{
			return false;
		}
		double max = box.LongMax;
		double min = box.LongMin;
		if (stateId != 2)
		{
			if (Math.Sign(max) == -1 && Math.Sign(min) == -1 && Math.Sign(longitude) == 1)
			{
				return true;
			}
			return !(Math.Abs(min) <= Math.Abs(longitude) && Math.Abs(max) >= Math.Abs(longitude));
		}
		switch (Math.Sign(longitude))
		{
		case 1:
			return longitude < 180.0 && longitude > max;
		case 0:
			return true;
		default:
			return Math.Abs(min) < Math.Abs(longitude) && 180.0 > Math.Abs(longitude);
		}
	}
}
