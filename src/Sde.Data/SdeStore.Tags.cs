using System.Data;
using Dapper;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>A label people put on structures, such as "Revisit" or "Owner called" (Mac-only Tag table, never exported).</summary>
[Table("Tag")]
public sealed class TagRecord
{
	[Key, AutoIncrement] public long TagID { get; set; }

	/// <summary>Unique, ignoring case.</summary>
	public string Name { get; set; }

	/// <summary>One of <see cref="TagColours.All"/>.</summary>
	public string Colour { get; set; }

	/// <summary>Where the tag comes in lists (lowest first); tags with the same place are sorted by name.</summary>
	public int? SortOrder { get; set; }
}

/// <summary>A structure's tag (Mac-only PropertyTag table).</summary>
[Table("PropertyTag")]
public sealed class PropertyTagRecord
{
	[Key] public Guid PropertyID { get; set; }

	[Key] public long TagID { get; set; }
}

/// <summary>The colours a tag can have. The app decides how each one looks in light and dark mode.</summary>
public static class TagColours
{
	public const string Blue = "Blue";
	public const string Green = "Green";
	public const string Amber = "Amber";
	public const string Red = "Red";
	public const string Violet = "Violet";
	public const string Slate = "Slate";

	/// <summary>Every colour, in the order a colour picker shows them.</summary>
	public static readonly IReadOnlyList<string> All = new[] { Blue, Green, Amber, Red, Violet, Slate };

	/// <summary>The colour a new tag gets when none is chosen.</summary>
	public const string Default = Blue;

	/// <summary>The stored name for a colour given in any case, or null when it isn't one of <see cref="All"/>.</summary>
	public static string Normalize(string colour) =>
		All.FirstOrDefault(c => string.Equals(c, colour?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Tags on structures. They are a person's own way of grouping structures, not part of FEMA's data, so they are never
/// exported, and like review comments they don't change any structure or assessment: they can be added to structures
/// with Final assessments, nothing is written to edit history, and versions don't change.
/// </summary>
public sealed partial class SdeStore
{
	public const int MaxTagNameLength = 40;

	/// <summary>Every tag, in list order.</summary>
	public List<TagRecord> GetTags() => Database.Read(c => TableMap<TagRecord>.Select(c, orderBy: TagOrder).ToList());

	private const string TagOrder = "COALESCE(SortOrder, 2147483647), Name COLLATE NOCASE, TagID";

	/// <summary>Makes a new tag at the end of the list. Refused when the name is blank, too long or already used (ignoring case).</summary>
	public TagRecord CreateTag(string name, string colour = TagColours.Default)
	{
		string clean = CleanTagName(name);
		string stored = ValidColour(colour);
		return Database.InTransaction((c, tx) =>
		{
			EnsureTagNameFree(c, tx, clean, null);
			TagRecord tag = new TagRecord
			{
				Name = clean,
				Colour = stored,
				SortOrder = (c.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM Tag", transaction: tx) ?? 0) + 1,
			};
			TableMap<TagRecord>.Insert(c, tag, tx);
			return tag;
		});
	}

	/// <summary>Renames a tag. Changing only the case of its name is allowed.</summary>
	public void RenameTag(long tagId, string name)
	{
		string clean = CleanTagName(name);
		Database.InTransaction((c, tx) =>
		{
			GetTag(c, tx, tagId);
			EnsureTagNameFree(c, tx, clean, tagId);
			c.Execute("UPDATE Tag SET Name = @clean WHERE TagID = @tagId", new { clean, tagId }, tx);
		});
	}

	public void SetTagColour(long tagId, string colour)
	{
		string stored = ValidColour(colour);
		Database.InTransaction((c, tx) =>
		{
			GetTag(c, tx, tagId);
			c.Execute("UPDATE Tag SET Colour = @stored WHERE TagID = @tagId", new { stored, tagId }, tx);
		});
	}

	/// <summary>Puts the tags in this order (tags not listed keep their place after these).</summary>
	public void ReorderTags(IReadOnlyList<long> tagIds) => Database.InTransaction((c, tx) =>
	{
		for (int i = 0; i < tagIds.Count; i++)
		{
			c.Execute("UPDATE Tag SET SortOrder = @order WHERE TagID = @id", new { order = i + 1, id = tagIds[i] }, tx);
		}
		int next = tagIds.Count + 1;
		HashSet<long> listed = tagIds.ToHashSet();
		foreach (long id in c.Query<long>("SELECT TagID FROM Tag ORDER BY " + TagOrder, transaction: tx).Where(id => !listed.Contains(id)).ToList())
		{
			c.Execute("UPDATE Tag SET SortOrder = @order WHERE TagID = @id", new { order = next++, id }, tx);
		}
	});

	/// <summary>Deletes a tag and takes it off every structure. Returns how many structures had it.</summary>
	public int DeleteTag(long tagId) => Database.InTransaction((c, tx) =>
	{
		int removed = c.Execute("DELETE FROM PropertyTag WHERE TagID = @tagId", new { tagId }, tx);
		c.Execute("DELETE FROM Tag WHERE TagID = @tagId", new { tagId }, tx);
		return removed;
	});

	/// <summary>A structure's tags, in list order.</summary>
	public List<TagRecord> GetTagsFor(Guid propertyId) => Database.Read(c => c.Query<TagRecord>(
		"SELECT t.* FROM Tag t JOIN PropertyTag pt ON pt.TagID = t.TagID WHERE pt.PropertyID = @propertyId ORDER BY " + TagPrefixedOrder,
		new { propertyId }).ToList());

	private const string TagPrefixedOrder = "COALESCE(t.SortOrder, 2147483647), t.Name COLLATE NOCASE, t.TagID";

	/// <summary>
	/// The tags of many structures at once (the Structures list), by PropertyID, each in list order. Structures with no
	/// tags have no entry.
	/// </summary>
	public ILookup<Guid, TagRecord> GetTagsForMany(IEnumerable<Guid> propertyIds)
	{
		List<string> ids = propertyIds.Distinct().Select(g => g.ToString("D")).ToList();
		return Database.Read(c =>
		{
			Dictionary<long, TagRecord> tags = TableMap<TagRecord>.Select(c).ToDictionary(t => t.TagID);
			List<(Guid PropertyID, TagRecord Tag)> pairs = new List<(Guid, TagRecord)>();
			// IDs go as text, in chunks well under SQLite's limit on parameters.
			foreach (string[] chunk in ids.Chunk(500))
			{
				foreach (PropertyTagRecord pt in c.Query<PropertyTagRecord>("SELECT PropertyID, TagID FROM PropertyTag WHERE PropertyID IN @chunk", new { chunk }))
				{
					if (tags.TryGetValue(pt.TagID, out TagRecord tag))
					{
						pairs.Add((pt.PropertyID, tag));
					}
				}
			}
			return pairs
				.OrderBy(p => p.Tag.SortOrder ?? int.MaxValue).ThenBy(p => p.Tag.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Tag.TagID)
				.ToLookup(p => p.PropertyID, p => p.Tag);
		});
	}

	/// <summary>Gives a structure exactly these tags (the tag picker). Tags that no longer exist are refused.</summary>
	public void SetTags(Guid propertyId, IEnumerable<long> tagIds)
	{
		List<long> wanted = tagIds.Distinct().ToList();
		Database.InTransaction((c, tx) =>
		{
			EnsureStructureExists(c, tx, propertyId);
			foreach (long id in wanted)
			{
				GetTag(c, tx, id);
			}
			HashSet<long> current = c.Query<long>("SELECT TagID FROM PropertyTag WHERE PropertyID = @propertyId", new { propertyId }, tx).ToHashSet();
			foreach (long id in current.Where(id => !wanted.Contains(id)))
			{
				c.Execute("DELETE FROM PropertyTag WHERE PropertyID = @propertyId AND TagID = @id", new { propertyId, id }, tx);
			}
			foreach (long id in wanted.Where(id => !current.Contains(id)))
			{
				TableMap<PropertyTagRecord>.Insert(c, new PropertyTagRecord { PropertyID = propertyId, TagID = id }, tx);
			}
		});
	}

	/// <summary>
	/// Puts a tag on many structures at once (a batch edit on the Structures list), in one transaction. Structures that
	/// already have it, or no longer exist, are skipped. Returns how many structures got it.
	/// </summary>
	public int AddTag(long tagId, IEnumerable<Guid> propertyIds) => Database.InTransaction((c, tx) =>
	{
		GetTag(c, tx, tagId);
		int added = 0;
		foreach (Guid propertyId in propertyIds.Distinct())
		{
			added += c.Execute("""
				INSERT INTO PropertyTag (PropertyID, TagID)
				SELECT @propertyId, @tagId WHERE EXISTS (SELECT 1 FROM Property WHERE PropertyID = @propertyId)
				ON CONFLICT DO NOTHING
				""", new { propertyId, tagId }, tx);
		}
		return added;
	});

	/// <summary>Takes a tag off many structures at once, in one transaction. Returns how many structures had it.</summary>
	public int RemoveTag(long tagId, IEnumerable<Guid> propertyIds) => Database.InTransaction((c, tx) =>
	{
		int removed = 0;
		foreach (Guid propertyId in propertyIds.Distinct())
		{
			removed += c.Execute("DELETE FROM PropertyTag WHERE PropertyID = @propertyId AND TagID = @tagId", new { propertyId, tagId }, tx);
		}
		return removed;
	});

	/// <summary>
	/// The structures with a tag (a filter on the Structures list). Structures in Recently deleted are left out unless
	/// <paramref name="activeOnly"/> is false.
	/// </summary>
	public List<Guid> GetPropertiesWithTag(long tagId, bool activeOnly = true) => Database.Read(c => c.Query<Guid>(
		"SELECT pt.PropertyID FROM PropertyTag pt JOIN Property p ON p.PropertyID = pt.PropertyID WHERE pt.TagID = @tagId"
		+ (activeOnly ? " AND p.Active = 1" : "") + " ORDER BY pt.PropertyID",
		new { tagId }).ToList());

	/// <summary>
	/// How many structures (not in Recently deleted) have each tag, by TagID, for the sidebar. Every tag has an entry,
	/// 0 when no structure has it.
	/// </summary>
	public Dictionary<long, int> GetTagCounts() => Database.Read(c => c.Query<TagCountRow>("""
		SELECT t.TagID, (SELECT COUNT(*) FROM PropertyTag pt JOIN Property p ON p.PropertyID = pt.PropertyID
		                 WHERE pt.TagID = t.TagID AND p.Active = 1) AS Count
		FROM Tag t
		""").ToDictionary(r => r.TagID, r => r.Count));

	private sealed class TagCountRow
	{
		public long TagID { get; set; }

		public int Count { get; set; }
	}

	private static TagRecord GetTag(IDbConnection c, IDbTransaction tx, long tagId) =>
		TableMap<TagRecord>.Get(c, new { TagID = tagId }, tx) ?? throw new ArgumentException("That tag no longer exists.");

	private static void EnsureStructureExists(IDbConnection c, IDbTransaction tx, Guid propertyId)
	{
		if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Property WHERE PropertyID = @propertyId", new { propertyId }, tx) == 0)
		{
			throw new ArgumentException("That structure no longer exists.");
		}
	}

	private static string CleanTagName(string name)
	{
		string clean = string.Join(" ", (name ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
		if (clean.Length == 0)
		{
			throw new ArgumentException("Give the tag a name.");
		}
		if (clean.Length > MaxTagNameLength)
		{
			throw new ArgumentException($"A tag's name can be at most {MaxTagNameLength} characters.");
		}
		return clean;
	}

	private static string ValidColour(string colour) =>
		TagColours.Normalize(colour) ?? throw new ArgumentException($"A tag's colour is one of {string.Join(", ", TagColours.All)}.");

	private static void EnsureTagNameFree(IDbConnection c, IDbTransaction tx, string name, long? exceptTagId)
	{
		// The column compares ignoring case (COLLATE NOCASE), the same rule its UNIQUE constraint uses.
		string existing = c.ExecuteScalar<string>("SELECT Name FROM Tag WHERE Name = @name AND TagID IS NOT @exceptTagId", new { name, exceptTagId }, tx);
		if (existing != null)
		{
			throw new ArgumentException($"There's already a tag called {existing}.");
		}
	}
}
