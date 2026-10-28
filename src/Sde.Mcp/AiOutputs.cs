using System.Globalization;
using System.Text;
using ModelContextProtocol;

namespace Sde.Mcp;

/// <summary>
/// Where the output tools write: an "AI outputs" folder beside the database, made when first needed. Writing only
/// there (never where an assistant asks) keeps every file in one place the person can find, and avoids macOS privacy
/// prompts for other folders that would be attributed to the assistant's app.
/// </summary>
internal static class AiOutputs
{
	/// <summary>The folder's name.</summary>
	public const string FolderName = "AI outputs";

	/// <summary>The most characters (as a person counts them) a file name takes, before the extension and any " 2".</summary>
	private const int MaxNameLength = 100;

	/// <summary>
	/// The most UTF-8 bytes a file name takes before the extension and any " 2": a whole name stays within 255 bytes
	/// (NAME_MAX, which many file systems and shared drives keep to), though 100 characters of emoji or accented
	/// letters can be 400.
	/// </summary>
	private const int MaxNameBytes = 200;

	/// <summary>The most " 2", " 3"… tried for one name before giving up.</summary>
	private const int MaxNumber = 10000;

	/// <summary>
	/// Characters that look like a folder separator but aren't one: harmless to the file system, but a name made of them
	/// ("..∕..∕Desktop") would read like a path where the result names the file.
	/// </summary>
	private static readonly HashSet<int> LookAlikes = new HashSet<int>
	{
		0x2044, // fraction slash
		0x2215, // division slash
		0x2216, // set minus
		0x29F5, // reverse solidus operator
		0x29F8, // big solidus
		0x29F9, // big reverse solidus
		0xFE68, // small reverse solidus
		0xFF0F, // fullwidth solidus
		0xFF3C, // fullwidth reverse solidus
		0xFF1A, // fullwidth colon
	};

	/// <summary>The AI outputs folder for <paramref name="databasePath"/>.</summary>
	public static string Folder(string databasePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, FolderName);

	/// <summary>Makes choosing a free name and taking it one step, so two outputs made at once never share a name.</summary>
	private static readonly object Gate = new object();

	/// <summary>
	/// A new file in the AI outputs folder (made if needed), created and opened for writing: <paramref name="name"/>
	/// made safe (see <see cref="SafeName"/>), or <paramref name="fallback"/>, with " 2", " 3"… added when that name is
	/// taken. <paramref name="extension"/> includes the dot. The file is always created new (never over an existing
	/// file, and never through a link left in the folder), and the output is written through the stream returned, so
	/// nothing can put a link in its place in between.
	/// </summary>
	public static FileStream NewFile(string databasePath, string name, string fallback, string extension, out string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(extension);
		FileStream stream = null;
		path = Take(databasePath, name, fallback, extension, p => stream = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None));
		return stream;
	}

	/// <summary>A new, empty folder in the AI outputs folder (made if needed), named as <see cref="NewFile"/> names a file.</summary>
	public static string NewFolder(string databasePath, string name, string fallback) =>
		Take(databasePath, name, fallback, "", p => Directory.CreateDirectory(p));

	/// <summary>Chooses a free name in the folder and takes it with <paramref name="create"/>, under one lock.</summary>
	private static string Take(string databasePath, string name, string fallback, string extension, Action<string> create)
	{
		string folder = RequireFolder(databasePath);
		string safe = Stem(name, extension);
		if (safe.Length == 0)
		{
			safe = Stem(fallback, extension);
		}
		if (safe.Length == 0)
		{
			safe = "AI output";
		}
		lock (Gate)
		{
			for (int i = 1; i <= MaxNumber; i++)
			{
				string path = Path.Combine(folder, i == 1 ? safe + extension : $"{safe} {i}{extension}");
				if (Taken(path))
				{
					continue;
				}
				try
				{
					create(path);
					return path;
				}
				catch (IOException) when (Taken(path))
				{
					// Another program took the name in the meantime: try the next number.
				}
			}
		}
		throw new McpException($"The \"{FolderName}\" folder already has {MaxNumber.ToString("N0", CultureInfo.InvariantCulture)} outputs with that name. Give another file name.");
	}

	/// <summary>
	/// The AI outputs folder, made if needed. A tool error when it is a link (to somewhere else) or a file: outputs go
	/// only into a real folder beside the database.
	/// </summary>
	private static string RequireFolder(string databasePath)
	{
		string folder = Folder(databasePath);
		if (new FileInfo(folder).LinkTarget != null)
		{
			throw new McpException($"\"{folder}\" is a link to another place, so SDE won't write outputs through it. Replace it with an ordinary folder (or remove it, and SDE makes one), then try again.");
		}
		if (File.Exists(folder))
		{
			throw new McpException($"\"{folder}\" is a file, not a folder, so SDE can't put outputs in it. Move or rename that file, then try again.");
		}
		Directory.CreateDirectory(folder);
		return folder;
	}

	/// <summary>True when something (a file, a folder, or a link, even one that leads nowhere) already has this name.</summary>
	private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget != null;

	/// <summary>A safe name without <paramref name="extension"/> at its end, so "x.pdf" doesn't become "x.pdf.pdf".</summary>
	private static string Stem(string name, string extension)
	{
		string safe = SafeName(name);
		if (extension.Length > 0 && safe.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
		{
			safe = safe[..^extension.Length].TrimEnd(' ', '.');
		}
		return safe;
	}

	/// <summary>Gives back a name <see cref="NewFile"/> or <see cref="NewFolder"/> took when nothing was written to it after all.</summary>
	public static void Release(string path)
	{
		try
		{
			if (new FileInfo(path).LinkTarget != null)
			{
				// Not what was made here: left alone.
				return;
			}
			if (File.Exists(path) && new FileInfo(path).Length == 0)
			{
				File.Delete(path);
			}
			else if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
			{
				Directory.Delete(path);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Left for the person to tidy; it is empty.
		}
	}

	/// <summary>
	/// A name that is only a name: folder separators, characters that look like them, control characters and other
	/// characters file systems refuse replaced with "-"; invisible formatting characters (among them the ones that
	/// reverse the direction of text, which can disguise a name) left out; no leading dots (hidden files, "..") or
	/// dashes; and cut to <see cref="MaxNameLength"/> characters and <see cref="MaxNameBytes"/> bytes without splitting
	/// a character.
	/// </summary>
	public static string SafeName(string name)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		StringBuilder cleaned = new StringBuilder();
		foreach (Rune r in (name ?? "").EnumerateRunes())
		{
			UnicodeCategory category = Rune.GetUnicodeCategory(r);
			if (category == UnicodeCategory.Format)
			{
				continue;
			}
			bool replace = r == Rune.ReplacementChar
				|| category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
				|| LookAlikes.Contains(r.Value)
				|| (r.IsBmp && ((char)r.Value is ':' or '/' or '\\' or '*' or '?' or '"' or '<' or '>' or '|' || invalid.Contains((char)r.Value)));
			if (replace)
			{
				cleaned.Append('-');
			}
			else
			{
				cleaned.Append(r.ToString());
			}
		}
		return Shorten(cleaned.ToString().Trim().TrimStart('.', '-', ' ').Trim());
	}

	/// <summary>The name cut to whole characters (as a person counts them) within both limits.</summary>
	private static string Shorten(string name)
	{
		if (name.Length <= MaxNameLength && Encoding.UTF8.GetByteCount(name) <= MaxNameBytes)
		{
			return name;
		}
		StringBuilder kept = new StringBuilder();
		int count = 0;
		int bytes = 0;
		TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(name);
		while (count < MaxNameLength && elements.MoveNext())
		{
			string element = elements.GetTextElement();
			int size = Encoding.UTF8.GetByteCount(element);
			if (bytes + size > MaxNameBytes)
			{
				break;
			}
			kept.Append(element);
			bytes += size;
			count++;
		}
		return kept.ToString().Trim();
	}
}
