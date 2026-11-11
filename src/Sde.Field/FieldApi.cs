using System.Globalization;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Field;

/// <summary>A choice in a list: its ID (as text) and name.</summary>
public sealed record OptionDto(string Id, string Name);

/// <summary>One field as the phone shows it. <see cref="Value"/> is <see cref="FieldCatalog.Canonical"/> text.</summary>
public sealed record FieldDto(string Key, string Label, int Step, string Kind, string Unit, string Value, IReadOnlyList<OptionDto> Options);

/// <summary>An editor step and its fields.</summary>
public sealed record StepDto(int Step, string Name, IReadOnlyList<FieldDto> Fields);

/// <summary>Every field the phone form knows, with the choices that don't depend on a particular assessment.</summary>
public sealed record LookupsDto(IReadOnlyList<string> Steps, IReadOnlyList<FieldDto> Fields);

/// <summary>A search result.</summary>
public sealed record StructureDto(Guid PropertyId, Guid? AssessmentId, string Address, string City, string Owner, string Parcel,
	string RecordId, string StructureType, string Result);

/// <summary>A field team the phone can choose (no member names or phone numbers).</summary>
public sealed record TeamDto(Guid TeamId, string Name, string Color);

/// <summary>A structure on a team's list, in route order.</summary>
public sealed record AssignmentDto(Guid AssignmentId, string ShortCode, int? RouteOrder, string Status, string DueDate, StructureDto Structure);

/// <summary>
/// One building element on the Damage step, as the engine last worked it out: its id (the element's lookup ID, which the
/// phone sends back), name, share of the replacement cost in percent, the inspector's percent damaged and the damage
/// in dollars.
/// </summary>
public sealed record ElementDto(int Id, string Name, decimal? Share, decimal Damaged, decimal DamageValue);

/// <summary>A standard cost adjustment line (garage, porch and so on): its lookup ID, name, units, the inspector's quantity and unit cost, and the total.</summary>
public sealed record CostAdjustmentDto(int Id, string Name, string Units, decimal? Quantity, decimal? UnitCost, decimal? TotalCost);

/// <summary>One of the 7 free-form additional adjustment lines (1 to 7).</summary>
public sealed record AdditionalAdjustmentDto(int Line, string Name, decimal? Quantity, decimal? UnitCost, decimal? TotalCost);

/// <summary>The cost totals the engine worked out, shown under the adjustments and elements.</summary>
public sealed record TotalsDto(decimal? TotalAdjustments, decimal? ReplacementCost, decimal? ActualCashValue, decimal? ComputedDamages);

/// <summary>Another assessment of the same structure (a re-inspection), for the switcher.</summary>
public sealed record SiblingDto(Guid AssessmentId, string Label, string Status, bool Current);

/// <summary>
/// An assessment as the phone edits it: only the fields that apply to it now. <see cref="PercentDamage"/> and
/// <see cref="DamageMessage"/> are FEMA's result as stored, for the result card and its gauge; the percentage is null
/// while data entry is incomplete, so the gauge never shows a 0% that was not worked out. <see cref="Result"/> is the
/// result as one line of text. <see cref="StatusValue"/> is the status's stored name and <see cref="StatusChoices"/>
/// the statuses the phone may move it to now (empty when it can't change the status).
/// </summary>
public sealed record AssessmentDto(Guid AssessmentId, Guid PropertyId, string Address, string StructureType, string Status, bool Locked,
	string LockedMessage, string Result, decimal? PercentDamage, string DamageMessage, int PhotoCount, IReadOnlyList<StepDto> Steps,
	string StatusValue, IReadOnlyList<OptionDto> StatusChoices, string StatusNote, bool DamageUndetermined, IReadOnlyList<ElementDto> Elements,
	IReadOnlyList<CostAdjustmentDto> CostAdjustments, IReadOnlyList<AdditionalAdjustmentDto> AdditionalAdjustments, TotalsDto Totals,
	IReadOnlyList<SiblingDto> Assessments);

/// <summary>A change sent by the phone: a field key and its new value as text (empty or null clears it).</summary>
public sealed record ChangeDto(string Key, string Value);

public sealed record ChangesRequest(IReadOnlyList<ChangeDto> Changes);

/// <summary>A new percent damaged (0 to 100) for one element, by its id.</summary>
public sealed record ElementChangeDto(int Id, decimal? Damaged);

public sealed record ElementsRequest(IReadOnlyList<ElementChangeDto> Elements);

/// <summary>A whole standard cost adjustment line: its lookup ID and new quantity and unit cost (null clears either).</summary>
public sealed record CostAdjustmentChangeDto(int Id, decimal? Quantity, decimal? UnitCost);

/// <summary>A whole additional adjustment line (1 to 7): its new description, quantity and unit cost (null clears any of them).</summary>
public sealed record AdditionalAdjustmentChangeDto(int Line, string Name, decimal? Quantity, decimal? UnitCost);

public sealed record CostsRequest(IReadOnlyList<CostAdjustmentChangeDto> Adjustments, IReadOnlyList<AdditionalAdjustmentChangeDto> Additional);

/// <summary>A new status, by its stored name (Draft or Reviewed from the phone).</summary>
public sealed record StatusRequest(string Status);

/// <summary>Starting an assessment: <see cref="Another"/> must be true when the structure already has one (a re-inspection).</summary>
public sealed record NewAssessmentRequest(bool Another);

/// <summary>A new structure: 1 residential or 2 non-residential, plus any first values (address, owner…).</summary>
public sealed record AddStructureRequest(int StructureType, IReadOnlyList<ChangeDto> Changes);

public sealed record PairRequest(string Token, string Pin, string Name);

/// <summary>What the phone form reads and how it checks and applies changes. No web code here, so it is easy to test.</summary>
public static class FieldApi
{
	public const int MaxResults = 50;
	public const int MaxTextLength = FieldInput.MaxTextLength;

	public static string KindName(FieldKind kind) => kind switch
	{
		FieldKind.MultilineText => "multiline",
		FieldKind.YesNo => "yesno",
		FieldKind.StructureType => "structureType",
		_ => kind.ToString().ToLowerInvariant(),
	};

	public static LookupsDto Lookups(ReferenceData reference)
	{
		// Choices for a blank residential assessment; counties depend on the state, so they come with each assessment.
		AssessmentFile template = AssessmentFile.CreateNew(new PropertyRecord { StructureTypeID = 1 });
		List<FieldDto> fields = FieldCatalog.All.Select(f => new FieldDto(f.Key, f.Label, f.Step, KindName(f.Kind), f.Unit, null,
			f.Kind == FieldKind.County ? Array.Empty<OptionDto>() : OptionsOf(f, template, reference))).ToList();
		return new LookupsDto(FieldCatalog.Steps, fields);
	}

	private static IReadOnlyList<OptionDto> OptionsOf(FieldInfo f, AssessmentFile file, ReferenceData reference) =>
		FieldCatalog.Options(f, file, reference).Select(o => new OptionDto(o.Id.ToString(CultureInfo.InvariantCulture), o.Name)).ToList();

	/// <summary>
	/// Structures whose address, city, ZIP, owner, parcel number or record ID contain every word of
	/// <paramref name="query"/> (any case). An empty query lists the most recently changed.
	/// A field worksheet's short code finds its structure, listed first.
	/// </summary>
	public static IReadOnlyList<StructureDto> Search(SdeStore store, string query)
	{
		string[] words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		List<StructureSummary> structures = store.ListStructures();
		List<StructureDto> results = new();
		// A field worksheet's code (typed, or read from its QR code) finds its structure first.
		AssignmentRecord coded = words.Length == 0 ? null : store.FindAssignmentByCode(query);
		if (coded != null && structures.FirstOrDefault(x => x.PropertyID == coded.PropertyID) is StructureSummary codedStructure)
		{
			results.Add(Dto(codedStructure));
		}
		foreach (StructureSummary s in structures)
		{
			string haystack = string.Join(" ", s.Address, s.City, s.ZipCode, s.Owner, s.ParcelNumber, s.ExternalId);
			if (words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase)) && s.PropertyID != coded?.PropertyID)
			{
				results.Add(Dto(s));
			}
			if (results.Count >= MaxResults)
			{
				break;
			}
		}
		return results;
	}

	private static StructureDto Dto(StructureSummary s) => new StructureDto(s.PropertyID, s.AssessmentID, Blank(s.Address, "(no address)"), s.City, s.Owner,
		s.ParcelNumber, s.ExternalId, TypeName(s.StructureTypeID), ResultText(s.PercentDamage, s.DamageMessage));

	/// <summary>The active field teams, by name.</summary>
	public static IReadOnlyList<TeamDto> Teams(SdeStore store) =>
		store.GetTeams().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => new TeamDto(t.TeamID, t.Name, t.Color)).ToList();

	/// <summary>A team's structures in route order ("My assignments"); structures removed since are left out.</summary>
	public static IReadOnlyList<AssignmentDto> Assignments(SdeStore store, Guid teamId)
	{
		Dictionary<Guid, StructureSummary> structures = new();
		foreach (StructureSummary s in store.ListStructures())
		{
			structures.TryAdd(s.PropertyID, s);
		}
		List<AssignmentDto> list = new();
		foreach (AssignmentRecord a in store.GetAssignments(teamId))
		{
			if (structures.TryGetValue(a.PropertyID, out StructureSummary s))
			{
				list.Add(new AssignmentDto(a.AssignmentID, a.ShortCode, a.RouteOrder, a.Status,
					a.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Dto(s)));
			}
		}
		return list;
	}

	public static AssessmentDto Describe(SdeStore store, AssessmentFile file)
	{
		ReferenceData r = store.Reference;
		AssessmentStatusRecord status = store.GetStatus(file.Assessment.AssessmentID);
		List<StepDto> steps = new();
		foreach (IGrouping<int, FieldInfo> step in FieldCatalog.All.Where(f => f.AppliesTo(file)).GroupBy(f => f.Step).OrderBy(g => g.Key))
		{
			List<FieldDto> fields = step.Select(f => new FieldDto(f.Key, f.Label, f.Step, KindName(f.Kind), f.Unit,
				FieldCatalog.Canonical(FieldCatalog.GetValue(file, f)), OptionsOf(f, file, r))).ToList();
			steps.Add(new StepDto(step.Key, FieldCatalog.Steps[step.Key - 1], fields));
		}
		// The Damage step has no single fields, only the element rows; it is listed so the steps stay in the editor's order.
		if (file.ElementPercentages.Count > 0 && steps.All(st => st.Step != FieldCatalog.DamageStep))
		{
			steps.Add(new StepDto(FieldCatalog.DamageStep, FieldCatalog.Steps[FieldCatalog.DamageStep - 1], Array.Empty<FieldDto>()));
			steps.Sort((x, y) => x.Step.CompareTo(y.Step));
		}
		PropertyRecord p = file.Property;
		AssessmentRecord a = file.Assessment;
		string address = string.Join(" ", new[] { p.StreetNumber, p.StreetName, r.LookupName(p.StreetSuffixID) }.Where(x => !string.IsNullOrWhiteSpace(x)));
		string locked = LockedMessage(store, a.AssessmentID);
		List<ElementDto> elements = file.ElementPercentages.Select(e => new ElementDto(e.CalcID, e.ElementName ?? r.LookupName(e.CalcID), e.ElementPercentage,
			e.UserDamage, ParseMoney(e.DamageValues))).ToList();
		List<CostAdjustmentDto> costs = file.CostAdjustments.Select(c => new CostAdjustmentDto(c.CostAdjustmentLookupID, r.LookupName(c.CostAdjustmentLookupID),
			c.Units, c.Quantity, c.UnitCost, c.TotalCost)).ToList();
		List<AdditionalAdjustmentDto> extras = file.AdditionalAdjustments.Where(x => x.zOrder is >= 1 and <= AssessmentCalculator.AdditionalAdjustmentRows)
			.OrderBy(x => x.zOrder).Select(x => new AdditionalAdjustmentDto(x.zOrder.Value, x.AdjustmentName, x.Quantity, x.UnitCost, x.TotalCost)).ToList();
		TotalsDto totals = new TotalsDto(a.TotalAdjustments, a.TotalReplacementCost, a.ComputedActualCashValue, a.ComputedDamages);
		IReadOnlyList<OptionDto> choices = locked == null && PhoneStatuses.Contains(status.Value)
			? PhoneStatuses.Select(s => new OptionDto(s.ToString(), AssessmentStatuses.Label(s))).ToList()
			: Array.Empty<OptionDto>();
		string note = locked != null ? null
			: choices.Count == 0 ? $"This assessment is {AssessmentStatuses.Label(status.Value)}. Its status is changed on the Mac."
			: "Set Reviewed when someone has checked it. Final is set on the Mac.";
		return new AssessmentDto(a.AssessmentID, p.PropertyID, Blank(address, "(no address)"), TypeName(p.StructureTypeID),
			AssessmentStatuses.Label(status.Value), locked != null, locked, ResultText(a.PercentDamage, a.DamageMessage),
			GaugePercent(a), a.DamageMessage ?? "", file.Attachments.Count, steps, status.Value.ToString(), choices, note, a.PhysicalDamage,
			elements, costs, extras, totals, Siblings(store, p.PropertyID, a.AssessmentID));
	}

	/// <summary>
	/// The statuses the phone can set: Draft and Reviewed. The Mac has no separate inspector and reviewer roles, so these
	/// are the two statuses before a determination; Final and Letter sent lock an assessment and stay a decision made on
	/// the Mac, and Appeal goes with the appeal record kept on the Mac. The phone can move an assessment only between these two.
	/// </summary>
	public static readonly IReadOnlyList<AssessmentStatus> PhoneStatuses = new[] { AssessmentStatus.Draft, AssessmentStatus.Reviewed };

	/// <summary>
	/// The status the phone asked for, checked: it must be one of <see cref="PhoneStatuses"/>, by its exact stored name.
	/// Throws <see cref="FieldInputException"/> otherwise.
	/// </summary>
	public static AssessmentStatus ParsePhoneStatus(string text)
	{
		foreach (AssessmentStatus s in PhoneStatuses)
		{
			if (string.Equals(text, s.ToString(), StringComparison.Ordinal))
			{
				return s;
			}
		}
		throw new FieldInputException("The phone can set Draft or Reviewed. Final and other statuses are set on the Mac.");
	}

	/// <summary>Every active assessment of the structure, newest inspection first, labelled as the Mac's switcher labels them.</summary>
	private static IReadOnlyList<SiblingDto> Siblings(SdeStore store, Guid propertyId, Guid current)
	{
		List<SiblingDto> list = new();
		foreach (AssessmentRecord a in store.GetAssessmentsForProperty(propertyId))
		{
			string label = (a.DateofInspection?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? "No date")
				+ (GaugePercent(a) is decimal pd ? ", " + pd.ToString("0.0", CultureInfo.InvariantCulture) + "%" : ", not worked out yet");
			list.Add(new SiblingDto(a.AssessmentID, label, AssessmentStatuses.Label(store.GetStatus(a.AssessmentID).Value), a.AssessmentID == current));
		}
		return list;
	}

	/// <summary>The longest additional adjustment description the phone accepts.</summary>
	public const int MaxAdjustmentNameLength = 255;

	/// <summary>
	/// Sets the percent damaged of the elements the phone sent, as the editor's Damage step does: each id must be an
	/// element this structure shows (residential, manufactured or non-residential, from <see cref="AssessmentCalculator.VisibleElements"/>),
	/// each value from 0 to 100, kept to 2 decimals like the editor. Then FEMA's engine recalculates. Throws
	/// <see cref="FieldInputException"/> for the first problem.
	/// </summary>
	public static void ApplyElements(AssessmentFile file, IReadOnlyList<ElementChangeDto> changes, AssessmentCalculator calculator)
	{
		calculator.NormalizeRows(file);
		foreach (ElementChangeDto change in changes ?? Array.Empty<ElementChangeDto>())
		{
			ElementPercentageRecord row = file.ElementPercentages.FirstOrDefault(e => e.CalcID == change?.Id)
				?? throw new FieldInputException("That element doesn't apply to this structure. Reload the page and try again.");
			if (change.Damaged is not decimal d || d < 0m || d > 100m)
			{
				throw new FieldInputException($"{row.ElementName}: type a percentage from 0 to 100.");
			}
			row.UserDamage = Math.Round(d, 2, MidpointRounding.AwayFromZero);
		}
		calculator.Recalculate(file);
	}

	/// <summary>
	/// Sets the cost adjustment lines the phone sent, each line whole, as the editor's Cost step does. A standard line's
	/// id must be one of the adjustments offered for this structure, its quantity 0 or more; an additional line is 1 to 7,
	/// with a description of at most <see cref="MaxAdjustmentNameLength"/> characters. Numbers stay within
	/// <see cref="FieldInput.MaxMagnitude"/>. Then FEMA's engine recalculates. Throws <see cref="FieldInputException"/>
	/// for the first problem.
	/// </summary>
	public static void ApplyCosts(AssessmentFile file, CostsRequest request, AssessmentCalculator calculator, ReferenceData reference)
	{
		calculator.NormalizeRows(file);
		foreach (CostAdjustmentChangeDto change in request?.Adjustments ?? Array.Empty<CostAdjustmentChangeDto>())
		{
			CostAdjustmentResultRecord row = file.CostAdjustments.FirstOrDefault(c => c.CostAdjustmentLookupID == change?.Id)
				?? throw new FieldInputException("That cost adjustment doesn't apply to this structure. Reload the page and try again.");
			string name = reference.LookupName(row.CostAdjustmentLookupID);
			row.Quantity = Checked(change.Quantity, name + " quantity", 0m);
			row.UnitCost = Checked(change.UnitCost, name + " unit cost", -FieldInput.MaxMagnitude);
		}
		foreach (AdditionalAdjustmentChangeDto change in request?.Additional ?? Array.Empty<AdditionalAdjustmentChangeDto>())
		{
			AdditionalAdjustmentRecord row = file.AdditionalAdjustments.FirstOrDefault(x => change != null && x.zOrder == change.Line)
				?? throw new FieldInputException($"Additional adjustments are lines 1 to {AssessmentCalculator.AdditionalAdjustmentRows}.");
			string label = "Additional adjustment " + change.Line.ToString(CultureInfo.InvariantCulture);
			string text = change.Name?.Trim();
			if (text != null && text.Length > MaxAdjustmentNameLength)
			{
				throw new FieldInputException($"{label}: the description is too long (at most {MaxAdjustmentNameLength} characters).");
			}
			if (text != null && text.Any(char.IsControl))
			{
				throw new FieldInputException($"{label}: the description can't contain line breaks or other control characters.");
			}
			row.AdjustmentName = string.IsNullOrWhiteSpace(text) ? null : text;
			row.Quantity = Checked(change.Quantity, label + " quantity", -FieldInput.MaxMagnitude);
			row.UnitCost = Checked(change.UnitCost, label + " unit cost", -FieldInput.MaxMagnitude);
		}
		calculator.Recalculate(file);
	}

	private static decimal? Checked(decimal? value, string label, decimal minimum)
	{
		if (value is decimal d && (d < minimum || d > FieldInput.MaxMagnitude))
		{
			string from = minimum.ToString("#,0.##", CultureInfo.InvariantCulture);
			string to = FieldInput.MaxMagnitude.ToString("#,0", CultureInfo.InvariantCulture);
			throw new FieldInputException($"{char.ToUpperInvariant(label[0])}{label[1..]}: type a number from {from} to {to}.");
		}
		return value;
	}

	private static decimal ParseMoney(string s) =>
		decimal.TryParse(s?.Replace("$", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : 0m;

	/// <summary>
	/// Why the phone can't change an assessment (Final or Letter sent, or checked out to another Mac in a work
	/// package), or null when it can.
	/// </summary>
	public static string LockedMessage(SdeStore store, Guid assessmentId)
	{
		AssessmentStatusRecord status = store.GetStatus(assessmentId);
		if (status.IsLocked)
		{
			return $"This assessment is {AssessmentStatuses.Label(status.Value)}, so it can't be changed here. It can be reopened on the Mac.";
		}
		CheckOutRecord checkOut = store.GetCheckOut(assessmentId);
		return checkOut == null ? null
			: $"This assessment is checked out to {(string.IsNullOrWhiteSpace(checkOut.CheckedOutTo) ? "another Mac" : checkOut.CheckedOutTo)} in a work package, so it can't be changed here.";
	}

	/// <summary>
	/// Applies the phone's changes in order through <see cref="FieldInput.Apply"/>: each key must be a known field that
	/// applies to the assessment at that point, each value must suit the field, and dependent fields reset as they do in
	/// the editor. Throws <see cref="FieldInputException"/> with a plain-English message for the first problem.
	/// </summary>
	public static void Apply(AssessmentFile file, IEnumerable<ChangeDto> changes, ReferenceData reference, AssessmentCalculator calculator) =>
		FieldInput.Apply(file, (changes ?? Array.Empty<ChangeDto>()).Select(c => new FieldInputChange(c?.Key, c?.Value)), reference, calculator,
			"The phone sent a field SDE doesn't know. Reload the page and try again.");

	/// <summary>A new structure and assessment (not saved), set up as the Mac's "New structure" does, with the phone's first values.</summary>
	public static AssessmentFile NewStructure(SdeStore store, AddStructureRequest request)
	{
		if (request == null || request.StructureType is not (1 or 2))
		{
			throw new FieldInputException("Choose residential or non-residential.");
		}
		bool useDefaults = store.GetPreferences()?.UseDefaults == true;
		DefaultValuesRecord defaults = useDefaults ? store.GetDefaults() : null;
		PropertyRecord p = new PropertyRecord { StructureTypeID = request.StructureType };
		if (defaults != null)
		{
			DefaultValues.ApplyToProperty(defaults, p);
		}
		AssessmentFile file = AssessmentFile.CreateNew(p, defaults);
		Apply(file, request.Changes, store.Reference, store.Calculator);
		store.Calculator.Recalculate(file);
		return file;
	}

	/// <summary>
	/// A new assessment (not saved) for a structure, as the Mac's "New assessment for this structure" starts one: the
	/// structure's fields as they are, the assessment's own fields blank (with the default values when they are turned on).
	/// </summary>
	public static AssessmentFile NewAssessment(SdeStore store, Guid propertyId)
	{
		PropertyRecord p = store.GetProperty(propertyId);
		if (p == null || !p.Active)
		{
			return null;
		}
		bool useDefaults = store.GetPreferences()?.UseDefaults == true;
		AssessmentFile file = AssessmentFile.CreateNew(p, useDefaults ? store.GetDefaults() : null);
		store.Calculator.Recalculate(file);
		return file;
	}

	private static string TypeName(int? structureType) => structureType switch
	{
		1 => "Residential",
		2 => "Non-residential",
		_ => "",
	};

	private static string ResultText(decimal? percent, string message) =>
		percent is decimal pd ? pd.ToString("0.0", CultureInfo.InvariantCulture) + "%" + (string.IsNullOrWhiteSpace(message) ? "" : ", " + message) : (message ?? "");

	/// <summary>The percentage the phone's gauge shows: none while FEMA's calculator says data entry is incomplete.</summary>
	private static decimal? GaugePercent(AssessmentRecord a) =>
		string.Equals(a.DamageMessage, "Data Entry Incomplete", StringComparison.OrdinalIgnoreCase) ? null : a.PercentDamage;

	private static string Blank(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
}
