using Sde.Core.Records;

namespace Sde.Core;

/// <summary>Lookup type IDs from FEMA's LookupType table (decomp/database/csv/LookupType.csv).</summary>
public static class LookupTypes
{
	public const int YesNo = 1;                 // also Conveyance
	public const int DamageElement = 2;
	public const int CauseOfDamage = 3;
	public const int ExteriorFinish = 4;
	public const int FirmZone = 5;
	public const int RegulatoryFloodway = 6;
	public const int Foundation = 7;
	public const int Hvac = 8;
	public const int StructureQuality = 9;
	public const int ResidenceType = 11;
	public const int RoofCovering = 12;
	public const int SprinklerSystem = 13;
	public const int Stories = 14;
	public const int StructureUse = 15;
	public const int Superstructure = 16;
	public const int StreetSuffix = 20;
	public const int DurationUnits = 21;
	public const int UndeterminedDamageType = 22;
	public const int CostAdjustment = 23;
	public const int DepreciationRating = 24;
	public const int ValueOfBuildingBasis = 25;
	public const int CostOfRepairsBasis = 26;
	public const int SearchAndReplaceField = 28;  // the fields FEMA's Search and Replace offers (LookupValue1 = table, LookupValue2 = column)
}

/// <summary>Well-known lookup IDs used in code by the original application.</summary>
public static class LookupIds
{
	public const int ManufacturedHouse = 94;
	public const int DepreciationOther = 389;
	public const int ValueComputedAcv = 390;
	public const int ValueMarketAppraisal = 391;
	public const int ValueAdjustedTax = 392;
	public const int RepairComputedDamages = 393;
	public const int RepairContractorEstimate = 394;
	public const int RepairCommunityEstimate = 395;
	public const int CauseFlood = 20;
	public const int CauseFloodAndWind = 21;
	public const int CauseOther = 24;
}

/// <summary>
/// Read-only reference data from FEMA's database (lookups, states, counties, depreciation tables, validation rules…).
/// Loaded once by the data layer.
/// </summary>
public sealed class ReferenceData
{
	private readonly Dictionary<int, LookupRecord> _byId;
	private readonly Dictionary<int, StateRecord> _statesById;
	private readonly Dictionary<int, CountyRecord> _countiesById;
	// Reference data never changes once loaded, so each type's sorted list is made once (import previews run off the UI thread).
	private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<LookupRecord>> _ofType = new();
	private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<CountyRecord>> _countiesIn = new();

	public ReferenceData(
		IReadOnlyList<LookupRecord> lookups,
		IReadOnlyList<StateRecord> states,
		IReadOnlyList<CountyRecord> counties,
		IReadOnlyList<StreetSuffixRecord> streetSuffixes,
		IReadOnlyList<DepreciationValueRecord> depreciation,
		IReadOnlyList<CostAdjustmentResidenceTypeRecord> costAdjustmentTypes,
		IReadOnlyList<ValidationRuleRecord> validationRules,
		IReadOnlyList<LatLongValidationRecord> latLongBoxes,
		IReadOnlyList<CustomFieldRecord> customFields)
	{
		Lookups = lookups;
		States = states;
		Counties = counties;
		StreetSuffixes = streetSuffixes;
		Depreciation = depreciation;
		CostAdjustmentTypes = costAdjustmentTypes;
		ValidationRules = validationRules;
		LatLongBoxes = latLongBoxes;
		CustomFields = customFields;
		_byId = lookups.GroupBy(l => l.LookupID).ToDictionary(g => g.Key, g => g.First());
		_statesById = states.GroupBy(s => s.StateID).ToDictionary(g => g.Key, g => g.First());
		_countiesById = counties.GroupBy(c => c.CountyID).ToDictionary(g => g.Key, g => g.First());
	}

	public IReadOnlyList<LookupRecord> Lookups { get; }
	public IReadOnlyList<StateRecord> States { get; }
	public IReadOnlyList<CountyRecord> Counties { get; }
	public IReadOnlyList<StreetSuffixRecord> StreetSuffixes { get; }
	public IReadOnlyList<DepreciationValueRecord> Depreciation { get; }
	public IReadOnlyList<CostAdjustmentResidenceTypeRecord> CostAdjustmentTypes { get; }
	public IReadOnlyList<ValidationRuleRecord> ValidationRules { get; }
	public IReadOnlyList<LatLongValidationRecord> LatLongBoxes { get; }
	public IReadOnlyList<CustomFieldRecord> CustomFields { get; }

	public LookupRecord Lookup(int? id) => id is int i && _byId.TryGetValue(i, out LookupRecord l) ? l : null;

	public string LookupName(int? id) => Lookup(id)?.LookupFieldName;

	/// <summary>
	/// Lookups of one type in the original's display order: ORDER BY zOrder, which in Jet puts NULLs first;
	/// ties fall back to LookupID (Jet's physical order).
	/// </summary>
	public IReadOnlyList<LookupRecord> OfType(int lookupTypeId) => _ofType.GetOrAdd(lookupTypeId, type =>
		Lookups.Where(l => l.LookupTypeID == type).OrderBy(l => l.zOrder ?? int.MinValue).ThenBy(l => l.LookupID).ToList().AsReadOnly());

	/// <summary>A state's counties by name (the County drop-downs, rebuilt on every edit in the editor).</summary>
	public IReadOnlyList<CountyRecord> CountiesIn(int? stateId) => stateId is not int id ? Array.Empty<CountyRecord>() : _countiesIn.GetOrAdd(id, _ =>
		Counties.Where(c => c.StateID == id).OrderBy(c => c.County).ToList().AsReadOnly());

	public string StateName(int? id) => id is int i && _statesById.TryGetValue(i, out StateRecord s) ? s.State : null;

	public string StateAbbreviation(int? id) => id is int i && _statesById.TryGetValue(i, out StateRecord s) ? s.StateAbb : null;

	public string CountyName(int? id) => id is int i && _countiesById.TryGetValue(i, out CountyRecord c) ? c.County : null;

	/// <summary>
	/// Depreciation % for a rating. Port of DAL LookupService.GetDepreciationValues (decomp/DAL/DAL.Services/LookupService.cs:141):
	/// residential uses story = use = 0; non-residential uses the structure's story and use. 0 when no row matches
	/// (the original's ExecuteScalar returned null → Convert.ToDecimal(null) = 0).
	/// </summary>
	public decimal DepreciationPercent(int storyId, int structureUseId, int structureTypeId, int ratingLookupId)
	{
		if (structureTypeId == 1)
		{
			storyId = 0;
			structureUseId = 0;
		}
		DepreciationValueRecord row = Depreciation
			.Where(d => d.StoriesLookupID == storyId && d.StructureUseLookupID == structureUseId && d.DepreciationLookupID == ratingLookupId)
			.OrderBy(d => Lookup(d.DepreciationLookupID)?.zOrder ?? int.MaxValue)
			.FirstOrDefault();
		return row?.DepreciationPercentage ?? 0m;
	}

	/// <summary>
	/// Standard cost adjustments offered for a structure. Port of DAL LookupService.GetAllCostAdjustments
	/// (decomp/DAL/DAL.Services/LookupService.cs:112): residential = rows joined through CostAdjustment_ResidenceType
	/// for the residence type; non-residential = lookup type 23 rows with LookupValue2 = '2'. Ordered by zOrder.
	/// LookupValue1 holds the unit ("Sq Ft" / "Ea").
	/// </summary>
	public IReadOnlyList<LookupRecord> CostAdjustmentsFor(int? structureTypeId, int? residenceTypeId)
	{
		HashSet<int> set = structureTypeId == 1
			? CostAdjustmentTypes.Where(c => c.ResidenceType == residenceTypeId).Select(c => c.LookupID).ToHashSet()
			: OfType(LookupTypes.CostAdjustment).Where(l => l.LookupValue2 == "2").Select(l => l.LookupID).ToHashSet();
		return OfType(LookupTypes.CostAdjustment).Where(l => set.Contains(l.LookupID)).ToList();
	}
}
