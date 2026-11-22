using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Depth;

namespace Sde.Data;

/// <summary>One structure's proposed flood depth from high-water marks, beside what its assessment has now.</summary>
public sealed class DepthRow
{
	public Guid AssessmentID { get; init; }

	public Guid PropertyID { get; init; }

	public string Address { get; init; }

	public WaterSurfaceEstimate Estimate { get; init; }

	public DepthProposal Proposal { get; init; }

	/// <summary>"Flood elevation" (Assessment.DepthOfFlood) now, as the editor shows it.</summary>
	public string CurrentFloodElevation { get; init; }

	/// <summary>Depth above the lowest floor (Assessment.DepthOfFloodAboveFFE) now.</summary>
	public string CurrentAboveFloor { get; init; }

	/// <summary>Why nothing is proposed (not a flood assessment, no position, no marks near, datums differ), or null.</summary>
	public string Skipped { get; init; }

	public bool CanApply => Skipped == null && Proposal?.CanApply == true;
}

/// <summary>What <see cref="DepthFromMarks.Apply"/> did.</summary>
public sealed class DepthApplyResult
{
	public string BatchId { get; init; }

	public int Changed { get; set; }

	/// <summary>Assessments left alone because they are Final, their letter was sent, or they are checked out to another Mac.</summary>
	public List<Guid> Refused { get; } = new();
}

/// <summary>
/// Flood depth from high-water marks for many structures at once (C4): <see cref="Propose"/> estimates the water
/// surface at each structure (see <see cref="HighWaterMarks"/>) using its event's datum offset; <see cref="Apply"/>
/// saves the accepted rows in one edit history batch as "Depth tool", which also refreshes the depth table's damage
/// suggestions. Nothing is changed by proposing.
/// </summary>
public static class DepthFromMarks
{
	public static List<DepthRow> Propose(SdeStore store, IReadOnlyCollection<Guid> assessmentIds, IReadOnlyList<HighWaterMark> marks,
		HighWaterMarkOptions options = null, decimal? datumOffsetFt = null)
	{
		ArgumentNullException.ThrowIfNull(marks);
		HighWaterMarkIndex index = HighWaterMarks.Index(marks, options);
		return Build(store, assessmentIds, index.Estimate, datumOffsetFt);
	}

	/// <summary>
	/// The flood elevation the marks suggest for each assessment that has one, for the quality check "Flood elevation
	/// unlike high-water marks" (<c>input with { HighWaterMarkEstimates = ... }</c>). Uses marks already loaded and makes
	/// no online call. Assessments with no usable estimate (not a flood, no position, no marks near, datums differ) are left out.
	/// </summary>
	public static Dictionary<Guid, decimal> QaEstimates(SdeStore store, IReadOnlyCollection<Guid> assessmentIds, IReadOnlyList<HighWaterMark> marks,
		HighWaterMarkOptions options = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(marks);
		List<AssessmentFile> files = store.LoadAssessmentHeaders(assessmentIds);
		if (files.Count == 0)
		{
			return new Dictionary<Guid, decimal>();
		}
		Dictionary<Guid, Guid> eventOf = store.GetEventMemberships();
		Dictionary<Guid, decimal?> offsets = store.GetEvents().ToDictionary(e => e.EventID, e => e.DatumOffsetFt);
		return QaEstimates(files, marks, eventOf, offsets, options);
	}

	internal static Dictionary<Guid, decimal> QaEstimates(IReadOnlyList<AssessmentFile> files, IReadOnlyList<HighWaterMark> marks,
		IReadOnlyDictionary<Guid, Guid> eventOf, IReadOnlyDictionary<Guid, decimal?> eventOffsets, HighWaterMarkOptions options = null)
	{
		Dictionary<Guid, decimal> estimates = new();
		HighWaterMarkIndex index = HighWaterMarks.Index(marks, options);
		foreach (AssessmentFile f in files)
		{
			if (!FieldCatalog.IsFlood(f))
			{
				continue;
			}
			if (f.Property.Latitude is not decimal lat || f.Property.Longitude is not decimal lon || (lat == 0m && lon == 0m))
			{
				continue;
			}
			WaterSurfaceEstimate estimate = index.Estimate((double)lat, (double)lon);
			decimal? offset = eventOf != null && eventOf.TryGetValue(f.Assessment.AssessmentID, out Guid eventId)
				&& eventOffsets != null && eventOffsets.TryGetValue(eventId, out decimal? o)
				? o : null;
			DepthProposal proposal = HighWaterMarks.Propose(estimate, f.Property, offset);
			if (proposal.CanApply && proposal.FloodElevationFt is decimal elevation)
			{
				estimates[f.Assessment.AssessmentID] = elevation;
			}
		}
		return estimates;
	}

	/// <summary>
	/// The same from a water-surface elevation grid (a GeoTIFF): <paramref name="waterSurfaceFt"/> samples it at a
	/// latitude and longitude, in feet in <paramref name="datumName"/> (null outside the grid or where it has no value).
	/// </summary>
	public static List<DepthRow> ProposeFromGrid(SdeStore store, IReadOnlyCollection<Guid> assessmentIds, Func<double, double, decimal?> waterSurfaceFt,
		string datumName, string gridName, decimal? datumOffsetFt = null)
	{
		ArgumentNullException.ThrowIfNull(waterSurfaceFt);
		return Build(store, assessmentIds, (lat, lon) => waterSurfaceFt(lat, lon) is decimal wse
			? new WaterSurfaceEstimate { ElevationFt = Math.Round(wse, 2), DatumName = datumName, Confidence = EstimateConfidence.High, Note = "From the grid " + gridName + "." }
			: new WaterSurfaceEstimate { DatumName = datumName, Confidence = EstimateConfidence.None, Note = "The structure is outside the grid, or the grid has no water there." },
			datumOffsetFt);
	}

	private static List<DepthRow> Build(SdeStore store, IReadOnlyCollection<Guid> assessmentIds, Func<double, double, WaterSurfaceEstimate> estimateAt, decimal? datumOffsetFt)
	{
		ArgumentNullException.ThrowIfNull(store);
		ReferenceData r = store.Reference;
		FieldInfo floodField = FieldCatalog.Get(DepthProposal.DepthOfFloodKey);
		FieldInfo aboveField = FieldCatalog.Get(DepthProposal.DepthAboveFloorKey);
		Dictionary<Guid, decimal?> offsets = new();
		Dictionary<Guid, Guid> eventOf = null;   // read once, when the first row needs it
		List<DepthRow> rows = new();
		foreach (AssessmentFile f in store.LoadAssessments(assessmentIds))
		{
			string address = Sde.Core.Quality.QaRules.Address(f.Property, r);
			string skipped = null;
			WaterSurfaceEstimate estimate = null;
			DepthProposal proposal = null;
			if (!FieldCatalog.IsFlood(f))
			{
				skipped = "The cause of damage isn't flood.";
			}
			else if (f.Property.Latitude is not decimal lat || f.Property.Longitude is not decimal lon || (lat == 0m && lon == 0m))
			{
				skipped = "The structure has no latitude and longitude.";
			}
			else
			{
				estimate = estimateAt((double)lat, (double)lon);
				proposal = HighWaterMarks.Propose(estimate, f.Property, datumOffsetFt ?? EventOffset(store, f.Assessment.AssessmentID, eventOf ??= store.GetEventMemberships(), offsets));
				if (!proposal.CanApply)
				{
					skipped = proposal.Notes.FirstOrDefault() ?? estimate.Note ?? "No estimate.";
				}
			}
			rows.Add(new DepthRow
			{
				AssessmentID = f.Assessment.AssessmentID,
				PropertyID = f.Property.PropertyID,
				Address = address,
				Estimate = estimate,
				Proposal = proposal,
				CurrentFloodElevation = FieldCatalog.Display(floodField, FieldCatalog.GetValue(f, floodField), r) ?? "",
				CurrentAboveFloor = FieldCatalog.Display(aboveField, FieldCatalog.GetValue(f, aboveField), r) ?? "",
				Skipped = skipped,
			});
		}
		return rows;
	}

	/// <summary>
	/// Saves the rows that can be applied, in one batch. A Final, Letter sent or checked-out assessment is left alone and
	/// listed in <see cref="DepthApplyResult.Refused"/>.
	/// </summary>
	public static DepthApplyResult Apply(SdeStore store, IEnumerable<DepthRow> rows, SaveContext context = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(rows);
		SaveContext ctx = new SaveContext
		{
			User = context?.User,
			Via = SaveVia.DepthTool,
			BatchId = context?.BatchId ?? Guid.NewGuid().ToString("N"),
			Event = context?.Event,
		};
		List<DepthRow> applicable = rows.Where(x => x.CanApply).ToList();
		DepthApplyResult result = new DepthApplyResult { BatchId = ctx.BatchId };
		if (applicable.Count == 0)
		{
			return result;
		}
		ReferenceData reference = store.Reference;
		AssessmentCalculator calc = store.Calculator;
		store.Database.InTransaction((c, tx) =>
		{
			foreach (DepthRow row in applicable)
			{
				AssessmentFile file = SdeStore.LoadAssessment(c, tx, row.AssessmentID) ?? throw new ArgumentException("That assessment no longer exists.");
				FieldInput.Apply(file, row.Proposal.Changes, reference, calc);
				try
				{
					store.SaveTracked(c, tx, file, ctx);
				}
				catch (Exception ex) when (ex is AssessmentLockedException or AssessmentCheckedOutException)
				{
					result.Refused.Add(row.AssessmentID);
				}
			}
		});
		result.Changed = store.GetBatch(ctx.BatchId).Where(h => h.Field != null).Select(h => h.AssessmentID).Distinct().Count();
		return result;
	}

	private static decimal? EventOffset(SdeStore store, Guid assessmentId, Dictionary<Guid, Guid> eventOf, Dictionary<Guid, decimal?> cache)
	{
		if (!eventOf.TryGetValue(assessmentId, out Guid eventId))
		{
			return null;
		}
		if (!cache.TryGetValue(eventId, out decimal? offset))
		{
			offset = store.GetEvent(eventId)?.DatumOffsetFt;
			cache[eventId] = offset;
		}
		return offset;
	}
}
