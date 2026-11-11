using Sde.Core.Depth;

namespace Sde.App.Services;

/// <summary>
/// The high-water marks loaded in this session (from a file or from USGS, in the Depth tool), kept in memory so the
/// Map page can draw a water surface from them. Nothing is saved: the marks go when the app closes or another database
/// is opened.
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
}
