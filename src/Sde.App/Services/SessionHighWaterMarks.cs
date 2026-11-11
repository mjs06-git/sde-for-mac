using Sde.Core.Depth;
using Sde.Data;

namespace Sde.App.Services;

/// <summary>
/// The high-water marks available in this session, so the Map page can draw a water surface from them: those loaded in
/// the Depth tool (from a file or from USGS), or those kept in the database with the chosen event (see
/// <see cref="LoadStored"/>). Marks loaded but not kept go when the app closes or another database is opened.
/// </summary>
public sealed class SessionHighWaterMarks
{
	private IReadOnlyList<HighWaterMark> _marks = Array.Empty<HighWaterMark>();

	/// <summary>The marks loaded last (empty when none have been loaded).</summary>
	public IReadOnlyList<HighWaterMark> Marks => _marks;

	/// <summary>Where the marks came from ("marks.csv", or a USGS event's name), or null.</summary>
	public string Source { get; private set; }

	/// <summary>True when there are marks to draw.</summary>
	public bool HasMarks => _marks.Count > 0;

	/// <summary>The marks changed (loaded, replaced or cleared).</summary>
	public event Action Changed;

	/// <summary>Keeps the marks just loaded in the Depth tool, replacing any kept before.</summary>
	public void Set(IEnumerable<HighWaterMark> marks, string source)
	{
		_marks = (marks ?? Enumerable.Empty<HighWaterMark>()).Where(m => m != null).ToList();
		Source = _marks.Count == 0 ? null : source;
		Changed?.Invoke();
	}

	/// <summary>Forgets the marks (another database was opened).</summary>
	public void Clear() => Set(null, null);

	/// <summary>
	/// Takes up the marks kept in the database for an event (its own, or else those kept for every event); for all events
	/// (null), the set loaded most recently. Nothing changes when none are kept, so marks loaded this session stay.
	/// </summary>
	public void LoadStored(SdeStore store, Guid? eventId)
	{
		StoredHighWaterMarks stored = store.HighWaterMarksFor(eventId);
		if (stored == null && eventId == null && store.GetHighWaterMarkSets().OrderByDescending(s => s.LoadedOn).FirstOrDefault() is HighWaterMarkSetRecord newest)
		{
			stored = store.GetHighWaterMarks(newest.EventID);
		}
		if (stored != null)
		{
			Set(stored.Marks, SourceName(stored.Set.Source));
		}
	}

	/// <summary>A stored set's source as the map names it: "marks.csv" for "File: marks.csv".</summary>
	public static string SourceName(string source) =>
		source != null && source.StartsWith("File: ", StringComparison.Ordinal) ? source.Substring(6) : source;
}
