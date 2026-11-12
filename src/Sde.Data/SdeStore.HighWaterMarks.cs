using System.Data;
using Dapper;
using Sde.Core.Depth;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>A set of high-water marks kept with an event (Mac-only HighWaterMarkSet table, never exported).</summary>
[Table("HighWaterMarkSet")]
public sealed class HighWaterMarkSetRecord
{
	[Key] public Guid SetID { get; set; }

	/// <summary>The event the marks are for, or null for the set used by every event that has none of its own.</summary>
	public Guid? EventID { get; set; }

	/// <summary>Where the marks came from, e.g. "File: marks.csv" or "USGS STN event 123 (September flood)".</summary>
	public string Source { get; set; }

	/// <summary>The datum given for marks that don't name one, or null.</summary>
	public string DatumName { get; set; }

	public int MarkCount { get; set; }

	public DateTime? LoadedOn { get; set; }

	public string LoadedBy { get; set; }
}

/// <summary>One stored high-water mark (Mac-only HighWaterMarkRow table).</summary>
[Table("HighWaterMarkRow")]
public sealed class HighWaterMarkRowRecord
{
	[Key] public Guid SetID { get; set; }

	[Key] public int Seq { get; set; }

	public string MarkId { get; set; }

	public string Label { get; set; }

	public double Latitude { get; set; }

	public double Longitude { get; set; }

	public decimal ElevationFt { get; set; }

	public string DatumName { get; set; }

	public int? Quality { get; set; }

	public string Kind { get; set; }
}

/// <summary>A stored set of high-water marks and the marks in it, in the order they were loaded.</summary>
public sealed record StoredHighWaterMarks(HighWaterMarkSetRecord Set, IReadOnlyList<HighWaterMark> Marks);

/// <summary>
/// High-water marks kept with an event, so the depth tool, the Review checks and the map can use them again without the
/// file or the internet. One set per event (and one for every event without its own); loading new marks for an event
/// replaces its set. Marks belong to the event, not to a structure, so keeping or removing them writes no edit history.
/// They go when their event is deleted.
/// </summary>
public sealed partial class SdeStore
{
	/// <summary>
	/// Keeps <paramref name="marks"/> as the event's set (null: the set for every event without its own), replacing the
	/// set it had. Refused when there are no marks or the event no longer exists.
	/// </summary>
	public HighWaterMarkSetRecord SaveHighWaterMarks(Guid? eventId, IReadOnlyList<HighWaterMark> marks, string source, string datumName = null)
	{
		ArgumentNullException.ThrowIfNull(marks);
		if (marks.Count == 0)
		{
			throw new ArgumentException("There are no high-water marks to keep.");
		}
		HighWaterMarkSetRecord set = new HighWaterMarkSetRecord
		{
			SetID = Guid.NewGuid(),
			EventID = eventId,
			Source = Clean(source) ?? "Unknown",
			DatumName = Clean(datumName),
			MarkCount = marks.Count,
			LoadedOn = Clock(),
			LoadedBy = UserName,
		};
		Database.InTransaction((c, tx) =>
		{
			if (eventId is Guid id && c.ExecuteScalar<int>("SELECT COUNT(*) FROM Event WHERE EventID = @id", new { id }, tx) == 0)
			{
				throw new ArgumentException("That event no longer exists.");
			}
			DeleteMarkSet(c, tx, eventId);
			TableMap<HighWaterMarkSetRecord>.Insert(c, set, tx);
			for (int i = 0; i < marks.Count; i++)
			{
				HighWaterMark m = marks[i];
				TableMap<HighWaterMarkRowRecord>.Insert(c, new HighWaterMarkRowRecord
				{
					SetID = set.SetID,
					Seq = i + 1,
					MarkId = m.Id,
					Label = m.Label,
					Latitude = m.Latitude,
					Longitude = m.Longitude,
					ElevationFt = m.ElevationFt,
					DatumName = m.DatumName,
					Quality = m.Quality,
					Kind = m.Kind,
				}, tx);
			}
		});
		return set;
	}

	/// <summary>The event's own stored marks (null: the set for every event without its own), or null when it has none.</summary>
	public StoredHighWaterMarks GetHighWaterMarks(Guid? eventId) => Database.Read(c =>
	{
		HighWaterMarkSetRecord set = MarkSet(c, eventId);
		return set == null ? null : new StoredHighWaterMarks(set, Marks(c, set.SetID));
	});

	/// <summary>
	/// The marks that apply to an event: its own set, or else the set for every event without its own. Null when neither
	/// is stored. For an assessment in no event, pass null.
	/// </summary>
	public StoredHighWaterMarks HighWaterMarksFor(Guid? eventId) =>
		GetHighWaterMarks(eventId) ?? (eventId == null ? null : GetHighWaterMarks(null));

	/// <summary>Every stored set (without its marks), the set for every event first, then by when it was loaded.</summary>
	public List<HighWaterMarkSetRecord> GetHighWaterMarkSets() =>
		Database.Read(c => TableMap<HighWaterMarkSetRecord>.Select(c, orderBy: "EventID IS NOT NULL, LoadedOn").ToList());

	/// <summary>Forgets the event's stored marks (null: the set for every event without its own). Returns false when there were none.</summary>
	public bool ClearHighWaterMarks(Guid? eventId) => Database.InTransaction((c, tx) => DeleteMarkSet(c, tx, eventId) > 0);

	private static HighWaterMarkSetRecord MarkSet(IDbConnection c, Guid? eventId) => eventId == null
		? TableMap<HighWaterMarkSetRecord>.Select(c, "EventID IS NULL").FirstOrDefault()
		: TableMap<HighWaterMarkSetRecord>.Select(c, "EventID = @eventId", new { eventId }).FirstOrDefault();

	private static List<HighWaterMark> Marks(IDbConnection c, Guid setId) =>
		TableMap<HighWaterMarkRowRecord>.Select(c, "SetID = @setId", new { setId }, orderBy: "Seq")
			.Select(r => new HighWaterMark
			{
				Id = r.MarkId,
				Label = r.Label,
				Latitude = r.Latitude,
				Longitude = r.Longitude,
				ElevationFt = r.ElevationFt,
				DatumName = r.DatumName,
				Quality = r.Quality,
				Kind = r.Kind,
			}).ToList();

	/// <summary>Deletes the event's set; its marks go with it (ON DELETE CASCADE).</summary>
	private static int DeleteMarkSet(IDbConnection c, IDbTransaction tx, Guid? eventId) => eventId == null
		? c.Execute("DELETE FROM HighWaterMarkSet WHERE EventID IS NULL", transaction: tx)
		: c.Execute("DELETE FROM HighWaterMarkSet WHERE EventID = @eventId", new { eventId }, tx);
}
