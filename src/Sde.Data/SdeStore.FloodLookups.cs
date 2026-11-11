using System.Data;
using Dapper;
using Sde.Core.Geo;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// What FEMA's flood map (NFHL) showed at a structure the last time it was looked up online, kept whether or not the
/// values were applied to the structure (Mac-only FloodLookup table, never exported). The structure's own FIRM fields
/// stay what was entered; this is what the map said, for the Review checks to compare with.
/// </summary>
[Table("FloodLookup")]
public sealed class FloodLookupRecord
{
	/// <summary>The source written when none is given.</summary>
	public const string NfhlSource = "FEMA NFHL";

	[Key] public Guid PropertyID { get; set; }

	/// <summary>The flood zone (FLD_ZONE), e.g. "AE".</summary>
	public string Zone { get; set; }

	/// <summary>The zone subtype (ZONE_SUBTY), e.g. "FLOODWAY", or null.</summary>
	public string ZoneSubtype { get; set; }

	public bool InFloodway { get; set; }

	/// <summary>The base flood elevation in feet, or null when the map shows none.</summary>
	public decimal? Bfe { get; set; }

	/// <summary>The BFE's vertical datum, e.g. "NAVD88".</summary>
	public string BfeDatum { get; set; }

	public string FirmPanel { get; set; }

	public string PanelSuffix { get; set; }

	public DateTime? PanelEffectiveDate { get; set; }

	/// <summary>The NFIP community ID (CID).</summary>
	public string CommunityId { get; set; }

	public string CommunityName { get; set; }

	/// <summary>Which service answered, e.g. <see cref="NfhlSource"/>.</summary>
	public string Source { get; set; }

	/// <summary>Where the map was asked about. A structure that has moved since has a stale lookup.</summary>
	public double? Latitude { get; set; }

	public double? Longitude { get; set; }

	/// <summary>When the service answered (UTC).</summary>
	public DateTime RetrievedOn { get; set; }

	/// <summary>Who asked.</summary>
	public string RetrievedBy { get; set; }

	/// <summary>
	/// True when the structure is now farther than <paramref name="meters"/> from where the lookup was made. A lookup or a
	/// structure without a position can't be compared, and isn't stale: a lookup made at an address's geocoded position
	/// still stands for a structure whose coordinates were never entered.
	/// </summary>
	public bool IsStaleFor(decimal? latitude, decimal? longitude, double meters) =>
		Latitude is double la && Longitude is double lo && GeoMath.IsPosition(latitude, longitude)
		&& GeoMath.Meters(la, lo, (double)latitude.Value, (double)longitude.Value) > meters;
}

/// <summary>
/// Stored flood lookups, one per structure: the newest replaces the one before. Like tags and review comments they are
/// annotations, not part of the structure's record: saving one writes no edit history and changes no version (applying
/// the looked-up values is what the history records). A structure keeps its lookup while it is in Recently deleted, and
/// it goes with the structure when that is removed for good.
/// </summary>
public sealed partial class SdeStore
{
	/// <summary>
	/// Keeps a lookup as the structure's current one, replacing any earlier one. <see cref="FloodLookupRecord.RetrievedOn"/>
	/// defaults to now, <see cref="FloodLookupRecord.Source"/> to FEMA NFHL and RetrievedBy to <see cref="UserName"/>.
	/// Returns false, saving nothing, when the structure isn't in the database (a new structure not saved yet).
	/// </summary>
	public bool SaveFloodLookup(FloodLookupRecord lookup)
	{
		ArgumentNullException.ThrowIfNull(lookup);
		if (lookup.PropertyID == Guid.Empty)
		{
			throw new ArgumentException("A flood lookup needs the structure it is for.");
		}
		FloodLookupRecord row = new FloodLookupRecord
		{
			PropertyID = lookup.PropertyID,
			Zone = Clean(lookup.Zone),
			ZoneSubtype = Clean(lookup.ZoneSubtype),
			InFloodway = lookup.InFloodway,
			Bfe = lookup.Bfe,
			BfeDatum = Clean(lookup.BfeDatum),
			FirmPanel = Clean(lookup.FirmPanel),
			PanelSuffix = Clean(lookup.PanelSuffix),
			PanelEffectiveDate = lookup.PanelEffectiveDate,
			CommunityId = Clean(lookup.CommunityId),
			CommunityName = Clean(lookup.CommunityName),
			Source = Clean(lookup.Source) ?? FloodLookupRecord.NfhlSource,
			Latitude = lookup.Latitude,
			Longitude = lookup.Longitude,
			RetrievedOn = lookup.RetrievedOn == default ? DateTime.UtcNow : lookup.RetrievedOn.ToUniversalTime(),
			RetrievedBy = Clean(lookup.RetrievedBy) ?? UserName,
		};
		return Database.InTransaction((c, tx) =>
		{
			if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Property WHERE PropertyID = @PropertyID", new { row.PropertyID }, tx) == 0)
			{
				return false;
			}
			TableMap<FloodLookupRecord>.Upsert(c, row, tx);
			return true;
		});
	}

	/// <summary>A structure's stored flood lookup, or null.</summary>
	public FloodLookupRecord GetFloodLookup(Guid propertyId) =>
		Database.Read(c => AsUtc(TableMap<FloodLookupRecord>.Get(c, new { PropertyID = propertyId })));

	/// <summary>The stored flood lookups of many structures, by PropertyID. Structures with none have no entry.</summary>
	public Dictionary<Guid, FloodLookupRecord> GetFloodLookups(IEnumerable<Guid> propertyIds) =>
		Database.Read(c => GetFloodLookups(c, null, propertyIds));

	internal static Dictionary<Guid, FloodLookupRecord> GetFloodLookups(IDbConnection c, IDbTransaction tx, IEnumerable<Guid> propertyIds)
	{
		Dictionary<Guid, FloodLookupRecord> found = new Dictionary<Guid, FloodLookupRecord>();
		// IDs go as text, in chunks well under SQLite's limit on parameters.
		foreach (string[] chunk in propertyIds.Distinct().Select(g => g.ToString("D")).Chunk(500))
		{
			foreach (FloodLookupRecord r in TableMap<FloodLookupRecord>.Select(c, "PropertyID IN @chunk", new { chunk }, tx))
			{
				found[r.PropertyID] = AsUtc(r);
			}
		}
		return found;
	}

	/// <summary>Forgets a structure's stored flood lookup. Returns false when it had none.</summary>
	public bool RemoveFloodLookup(Guid propertyId) =>
		Database.InTransaction((c, tx) => TableMap<FloodLookupRecord>.Delete(c, "PropertyID = @propertyId", new { propertyId }, tx) > 0);

	private static FloodLookupRecord AsUtc(FloodLookupRecord r)
	{
		if (r != null)
		{
			r.RetrievedOn = DateTime.SpecifyKind(r.RetrievedOn, DateTimeKind.Utc);
		}
		return r;
	}

	private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
