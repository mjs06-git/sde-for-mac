using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>What to do when an imported structure or assessment is already in the database.</summary>
public enum ExistingRecordAction
{
	/// <summary>Leave the existing record alone (new assessments of an existing structure are still added).</summary>
	Skip,
	/// <summary>Replace the existing record with the imported one (the original's "Overwrite").</summary>
	Overwrite,
	/// <summary>Import as a separate copy with new IDs (the original's "Select" on an existing record).</summary>
	Copy,
}

public sealed class ImportResult
{
	public int PropertiesAdded { get; set; }
	public int PropertiesUpdated { get; set; }
	public int AssessmentsAdded { get; set; }
	public int AssessmentsUpdated { get; set; }
	public int Copied { get; set; }
	public int Skipped { get; set; }
	public int PhotosCopied { get; set; }
	public List<string> Warnings { get; } = new();
	public List<Guid> ImportedAssessmentIds { get; } = new();

	public override string ToString() =>
		$"{PropertiesAdded} structures added, {PropertiesUpdated} updated, {AssessmentsAdded} assessments added, " +
		$"{AssessmentsUpdated} updated, {Copied} copied, {Skipped} skipped, {PhotosCopied} photos" +
		(Warnings.Count > 0 ? $", {Warnings.Count} warnings" : "");
}

/// <summary>
/// SDE data exchange: Export.json folders (SDE 3) and FEMA SDE Access databases (.mdb).
/// Ports ucExportSDEdata / ucImportSDEdata (decomp/wpfSDE/wpfSDE.ImportExport) and UtilityService.BulkInsert/BulkUpdate,
/// fixing their ID-collision and custom-field bugs (PORTING-NOTES.md §Import/export).
/// </summary>
public sealed class ExchangeService
{
	private readonly SdeStore _store;

	public ExchangeService(SdeStore store) => _store = store;

	// ───────────────────────── Export ─────────────────────────

	/// <summary>Everything active in the database, as SDE's "Export All Data".</summary>
	public SdeDataset ReadDataset() => ReadDataset(_store.Database);

	/// <summary>Only the given assessments, their structures, and structures listed in <paramref name="propertyIds"/>.</summary>
	public SdeDataset ReadDataset(IEnumerable<Guid> assessmentIds, IEnumerable<Guid> propertyIds = null)
	{
		List<Guid> requestedAids = assessmentIds.Distinct().ToList();
		List<Guid> requestedPids = propertyIds == null ? new List<Guid>() : propertyIds.Distinct().ToList();
		if (requestedAids.Count == 0 && requestedPids.Count == 0)
		{
			return new SdeDataset();
		}
		return _store.Database.Read(c =>
		{
			using IDbTransaction tx = c.BeginTransaction(deferred: true);
			List<AssessmentRecord> assessments = new List<AssessmentRecord>();
			foreach (Guid[] chunk in requestedAids.Chunk(500))
			{
				assessments.AddRange(TableMap<AssessmentRecord>.Select(c, "Active = 1 AND AssessmentID IN @chunk", new { chunk = SdeStore.IdText(chunk) }, tx));
			}
			HashSet<Guid> wantedPids = new HashSet<Guid>(requestedPids);
			foreach (AssessmentRecord a in assessments)
			{
				wantedPids.Add(a.PropertyID);
			}
			List<PropertyRecord> properties = new List<PropertyRecord>();
			foreach (Guid[] chunk in wantedPids.Chunk(500))
			{
				properties.AddRange(TableMap<PropertyRecord>.Select(c, "Active = 1 AND PropertyID IN @chunk", new { chunk = SdeStore.IdText(chunk) }, tx));
			}
			HashSet<Guid> activePids = properties.Select(p => p.PropertyID).ToHashSet();
			assessments.RemoveAll(a => !activePids.Contains(a.PropertyID));
			List<Guid> activeAids = assessments.Select(a => a.AssessmentID).ToList();

			List<AttachmentRecord> attachments = new List<AttachmentRecord>();
			List<ElementPercentageRecord> elements = new List<ElementPercentageRecord>();
			List<CostAdjustmentResultRecord> costs = new List<CostAdjustmentResultRecord>();
			List<AdditionalAdjustmentRecord> additional = new List<AdditionalAdjustmentRecord>();
			List<CustomFieldResultRecord> custom = new List<CustomFieldResultRecord>();
			List<ShapeRecord> shapes = new List<ShapeRecord>();

			foreach (Guid[] chunk in activeAids.Chunk(500))
			{
				object args = new { chunk = SdeStore.IdText(chunk) };
				attachments.AddRange(TableMap<AttachmentRecord>.Select(c, "Active = 1 AND AssessmentID IN @chunk", args, tx, orderBy: "AttachmentID"));
				elements.AddRange(TableMap<ElementPercentageRecord>.Select(c, "AssessmentID IN @chunk", args, tx));
				costs.AddRange(TableMap<CostAdjustmentResultRecord>.Select(c, "AssessmentID IN @chunk", args, tx));
				additional.AddRange(TableMap<AdditionalAdjustmentRecord>.Select(c, "AssessmentID IN @chunk", args, tx, orderBy: "zOrder"));
			}
			if (activeAids.Count > 500)
			{
				attachments.Sort((x, y) => x.AttachmentID.CompareTo(y.AttachmentID));
				additional.Sort((x, y) => Nullable.Compare(x.zOrder, y.zOrder));
			}
			foreach (Guid[] chunk in activePids.Concat(activeAids).Distinct().Chunk(500))
			{
				custom.AddRange(TableMap<CustomFieldResultRecord>.Select(c, "ObjectIdentifier IN @chunk", new { chunk = SdeStore.IdText(chunk) }, tx));
			}
			foreach (Guid[] chunk in activePids.Chunk(500))
			{
				shapes.AddRange(TableMap<ShapeRecord>.Select(c, "PropertyID IN @chunk", new { chunk = SdeStore.IdText(chunk) }, tx, orderBy: "ShapeID"));
			}
			if (activePids.Count > 500)
			{
				shapes.Sort((x, y) => Nullable.Compare(x.ShapeID, y.ShapeID));
			}
			return new SdeDataset
			{
				Properties = properties,
				Assessments = assessments,
				Attachments = attachments,
				ElementPercentages = elements,
				CostAdjustments = costs,
				AdditionalAdjustments = additional,
				CustomFieldsResults = custom,
				Shapes = shapes,
			};
		});
	}

	private static SdeDataset ReadDataset(SdeDatabase db) => db.Read(c =>
	{
		SdeDataset d = new SdeDataset
		{
			Properties = TableMap<PropertyRecord>.Select(c, "Active = 1").ToList(),
			Assessments = TableMap<AssessmentRecord>.Select(c, "Active = 1").ToList(),
			Attachments = TableMap<AttachmentRecord>.Select(c, "Active = 1", orderBy: "AttachmentID").ToList(),
			ElementPercentages = TableMap<ElementPercentageRecord>.Select(c).ToList(),
			CostAdjustments = TableMap<CostAdjustmentResultRecord>.Select(c).ToList(),
			AdditionalAdjustments = TableMap<AdditionalAdjustmentRecord>.Select(c, orderBy: "zOrder").ToList(),
			CustomFieldsResults = TableMap<CustomFieldResultRecord>.Select(c).ToList(),
			Shapes = TableMap<ShapeRecord>.Select(c, orderBy: "ShapeID").ToList(),
		};
		HashSet<Guid> pids = d.Properties.Select(p => p.PropertyID).ToHashSet();
		HashSet<Guid> aids = d.Assessments.Where(a => pids.Contains(a.PropertyID)).Select(a => a.AssessmentID).ToHashSet();
		return d.Filter(pids, aids);
	});

	/// <summary>Writes an SDE_n exchange folder (Export.json + photos) inside <paramref name="parentFolder"/>.</summary>
	public (string Folder, IReadOnlyList<string> MissingPhotos) Export(SdeDataset data, string parentFolder) =>
		SdeExchangeFolder.Write(data, parentFolder, _store.AttachmentsFolder);

	// ───────────────────────── Import ─────────────────────────

	/// <summary>Imports an SDE 3 exchange folder (Export.json + photos/), e.g. one exported by FEMA's SDE on Windows.</summary>
	public ImportResult ImportFolder(string folder, ExistingRecordAction existing) =>
		Import(SdeExchangeFolder.Read(folder), SdeExchangeFolder.PhotosFolder(folder), existing, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));

	/// <summary>
	/// Reads a FEMA SDE Access database (SDEDatabase.mdb) into a data set, via mdbtools. Photos are expected in
	/// SDE's layout: an "attachments" folder next to the database's folder.
	/// </summary>
	public static (SdeDataset Data, string PhotosFolder) ReadMdb(string mdbPath)
	{
		string temp = Path.Combine(Path.GetTempPath(), "sde-mdb-" + Guid.NewGuid().ToString("N"), "read.sqlite");
		try
		{
			SdeDatabase scratch = SdeDatabase.Create(temp);
			string[] tables = { "Property", "Assessment", "Attachment", "ElementPercentage", "CostAdjustmentResult", "AdditionalAdjustment", "CustomField_Results", "Shapes" };
			HashSet<string> present = MdbTools.ListTables(mdbPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
			scratch.InTransaction((c, tx) =>
			{
				c.Execute("PRAGMA defer_foreign_keys = ON", transaction: tx);
				foreach (string t in tables.Where(present.Contains))
				{
					using StringReader csv = new StringReader(MdbTools.ExportCsv(mdbPath, t));
					CsvTableLoader.Load((SqliteConnection)c, tx, t, csv);
				}
			});
			string photos = Path.Combine(Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(mdbPath))!)!.FullName, "attachments");
			return (ReadDataset(scratch), photos);
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			try
			{
				Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	public ImportResult ImportMdb(string mdbPath, ExistingRecordAction existing)
	{
		(SdeDataset data, string photos) = ReadMdb(mdbPath);
		return Import(data, photos, existing, Path.GetFileName(mdbPath));
	}

	/// <summary>
	/// Merges a data set into the database in one transaction, then recalculates every imported assessment with
	/// FEMA's engine (the original also recalculated on import). Child-row IDs are always regenerated, so imported
	/// rows can never collide with existing ones (an original bug). The import is one batch in edit history, named
	/// after <paramref name="sourceName"/>; Final and Letter sent assessments are never replaced.
	/// </summary>
	public ImportResult Import(SdeDataset data, string photosFolder, ExistingRecordAction existing, string sourceName = null, SaveContext context = null)
	{
		ImportResult result = new ImportResult();
		AssessmentCalculator calculator = _store.Calculator;
		List<(string From, string To)> photoCopies = new();
		DatasetIndex index = new DatasetIndex(data);

		SaveContext ctx = context?.AsBatch() ?? SaveContext.Batch(SaveVia.Import, "Imported" + (string.IsNullOrEmpty(sourceName) ? "" : " from " + sourceName));
		_store.Database.InTransaction((c, tx) =>
		{
			// Rows in Recently deleted with the same IDs would make the import skip or overwrite something nobody can
			// see; they are replaced by what's imported.
			result.Warnings.AddRange(_store.PurgeTrashedCollisions(c, tx, data.Properties.Select(p => p.PropertyID), data.Assessments.Select(a => a.AssessmentID)));
			HashSet<Guid> dbProperties = c.Query<Guid>("SELECT PropertyID FROM Property", transaction: tx).ToHashSet();
			HashSet<Guid> dbAssessments = c.Query<Guid>("SELECT AssessmentID FROM Assessment", transaction: tx).ToHashSet();
			Dictionary<Guid, PropertyRecord> propertiesById = data.Properties.GroupBy(p => p.PropertyID).ToDictionary(g => g.Key, g => g.First());
			// Which assessments use each photo name (case-insensitive, like the Mac's disk): an imported photo never
			// replaces another assessment's file.
			Dictionary<string, HashSet<Guid>> photoOwners = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
			foreach (AttachmentRecord used in c.Query<AttachmentRecord>("SELECT AssessmentID, FileName FROM Attachment WHERE FileName IS NOT NULL", transaction: tx))
			{
				ClaimPhotoName(photoOwners, Path.GetFileName(used.FileName), used.AssessmentID);
			}

			foreach (AssessmentRecord orphan in data.Assessments.Where(a => !propertiesById.ContainsKey(a.PropertyID)))
			{
				result.Warnings.Add($"Assessment {orphan.AssessmentID} skipped: its structure {orphan.PropertyID} is not in the import.");
			}

			foreach (PropertyRecord source in propertiesById.Values)
			{
				Guid sourceId = source.PropertyID;
				bool propertyExists = dbProperties.Contains(sourceId);
				List<AssessmentRecord> assessments = index.Assessments[sourceId].ToList();
				bool copyAll = propertyExists && existing == ExistingRecordAction.Copy;

				// The structure row.
				PropertyRecord target = Clone(source);
				if (copyAll)
				{
					target.PropertyID = Guid.NewGuid();
					result.Copied++;
				}
				bool writeProperty = !propertyExists || existing != ExistingRecordAction.Skip;
				if (writeProperty)
				{
					TableMap<PropertyRecord>.Upsert(c, target, tx);
					_store.BumpVersion(c, tx, target.PropertyID, VersionKind.Property);
					if (!copyAll && propertyExists)
					{
						result.PropertiesUpdated++;
					}
					else if (!copyAll)
					{
						result.PropertiesAdded++;
					}
					ReplacePropertyChildren(c, tx, index, sourceId, target.PropertyID);
				}
				else
				{
					target = TableMap<PropertyRecord>.Get(c, new { PropertyID = sourceId }, tx);
					if (assessments.Count == 0)
					{
						result.Skipped++;
					}
				}

				// Its assessments.
				foreach (AssessmentRecord sourceAssessment in assessments)
				{
					bool assessmentExists = dbAssessments.Contains(sourceAssessment.AssessmentID);
					if (assessmentExists && existing == ExistingRecordAction.Skip)
					{
						result.Skipped++;
						continue;
					}
					bool newIds = copyAll || (assessmentExists && existing == ExistingRecordAction.Copy);
					if (assessmentExists && !newIds && SdeStore.GetStatus(c, tx, sourceAssessment.AssessmentID).IsLocked)
					{
						result.Skipped++;
						result.Warnings.Add($"Assessment {sourceAssessment.AssessmentID} wasn't replaced: it is Final or its letter was sent. Reopen it first to import over it.");
						continue;
					}
					if (assessmentExists && !newIds && ctx.Via != SaveVia.WorkPackage && SdeStore.IsCheckedOut(c, tx, sourceAssessment.AssessmentID))
					{
						result.Skipped++;
						result.Warnings.Add($"Assessment {sourceAssessment.AssessmentID} wasn't replaced: it is checked out to another Mac in a work package.");
						continue;
					}
					AssessmentFile file = BuildFile(index, sourceAssessment, target, newIds, photoCopies, photoOwners);
					if (assessmentExists && !newIds)
					{
						result.AssessmentsUpdated++;
					}
					else if (newIds && !copyAll)
					{
						result.Copied++;
					}
					else
					{
						result.AssessmentsAdded++;
					}
					calculator.Recalculate(file);
					// Replaces the child rows (attachments get new IDs, so the old ones are removed) and records the import.
					_store.SaveTracked(c, tx, file, ctx);
					result.ImportedAssessmentIds.Add(file.Assessment.AssessmentID);
				}
			}
		});

		_store.DeletePendingFiles();
		// Photos are copied after the transaction commits, so the import has happened whatever goes wrong here: a photo
		// that can't be copied is a warning, never a failure.
		try
		{
			Directory.CreateDirectory(_store.AttachmentsFolder);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			result.Warnings.Add($"The photos weren't copied: the attachments folder couldn't be made ({ex.Message}).");
			return result;
		}
		foreach ((string from, string to) in photoCopies)
		{
			string source = Path.Combine(photosFolder ?? "", from);
			if (!File.Exists(source))
			{
				result.Warnings.Add("Photo not found in import: " + from);
				continue;
			}
			// A link in an import folder could point at any file on the Mac: never copied.
			if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(Path.GetDirectoryName(source)!).HasFlag(FileAttributes.ReparsePoint))
			{
				result.Warnings.Add($"Photo {from} wasn't copied: it is a link, and links aren't followed.");
				continue;
			}
			try
			{
				File.Copy(source, Path.Combine(_store.AttachmentsFolder, to), overwrite: true);
				result.PhotosCopied++;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				result.Warnings.Add($"Photo {from} couldn't be copied: {ex.Message}");
			}
		}
		return result;
	}

	/// <summary>An import's rows grouped by the structure or assessment they belong to (each group in file order), so a large import does not search every list again for every row.</summary>
	private sealed class DatasetIndex
	{
		public DatasetIndex(SdeDataset data)
		{
			Assessments = data.Assessments.ToLookup(a => a.PropertyID);
			CostAdjustments = data.CostAdjustments.ToLookup(x => x.AssessmentID);
			AdditionalAdjustments = data.AdditionalAdjustments.ToLookup(x => x.AssessmentID);
			ElementPercentages = data.ElementPercentages.ToLookup(x => x.AssessmentID);
			CustomFieldsResults = data.CustomFieldsResults.ToLookup(x => x.ObjectIdentifier);
			Attachments = data.Attachments.ToLookup(x => x.AssessmentID);
			Shapes = data.Shapes.ToLookup(x => x.PropertyID);
		}

		public ILookup<Guid, AssessmentRecord> Assessments { get; }
		public ILookup<Guid, CostAdjustmentResultRecord> CostAdjustments { get; }
		public ILookup<Guid, AdditionalAdjustmentRecord> AdditionalAdjustments { get; }
		public ILookup<Guid, ElementPercentageRecord> ElementPercentages { get; }
		public ILookup<Guid, CustomFieldResultRecord> CustomFieldsResults { get; }
		public ILookup<Guid, AttachmentRecord> Attachments { get; }
		public ILookup<Guid, ShapeRecord> Shapes { get; }
	}

	private static AssessmentFile BuildFile(DatasetIndex data, AssessmentRecord source, PropertyRecord property, bool newIds,
		List<(string, string)> photoCopies, Dictionary<string, HashSet<Guid>> photoOwners)
	{
		Guid sourceId = source.AssessmentID;
		AssessmentRecord a = Clone(source);
		if (newIds)
		{
			a.AssessmentID = Guid.NewGuid();
		}
		a.PropertyID = property.PropertyID;
		Guid aid = a.AssessmentID;
		AssessmentFile file = new AssessmentFile
		{
			Property = property,
			Assessment = a,
			CostAdjustments = data.CostAdjustments[sourceId].Select(x =>
			{
				CostAdjustmentResultRecord r = Clone(x);
				r.ResultID = Guid.NewGuid();
				r.AssessmentID = aid;
				return r;
			}).ToList(),
			AdditionalAdjustments = data.AdditionalAdjustments[sourceId].Select(x =>
			{
				AdditionalAdjustmentRecord r = Clone(x);
				r.AdditionalAdjustmentID = Guid.NewGuid();
				r.AssessmentID = aid;
				return r;
			}).ToList(),
			ElementPercentages = data.ElementPercentages[sourceId].Select(x =>
			{
				ElementPercentageRecord r = Clone(x);
				r.ElementID = Guid.NewGuid();
				r.AssessmentID = aid;
				return r;
			}).ToList(),
			CustomFields = data.CustomFieldsResults[sourceId].Select(x =>
			{
				CustomFieldResultRecord r = Clone(x);
				r.ObjectIdentifier = aid;
				return r;
			}).ToList(),
			Attachments = data.Attachments[sourceId].Select(x =>
			{
				AttachmentRecord r = Clone(x);
				r.AttachmentID = 0;
				r.AssessmentID = aid;
				r.Active = true;
				if (!string.IsNullOrEmpty(r.FileName))
				{
					string from = Path.GetFileName(r.FileName);
					// Copies get their own photo files so deleting one copy can't remove the other's photos, and a name another
					// assessment already uses gets a new one so its photo isn't written over.
					bool taken = photoOwners.TryGetValue(from, out HashSet<Guid> owners) && owners.Any(o => o != aid);
					string to = newIds || taken ? Guid.NewGuid() + Path.GetExtension(from) : from;
					ClaimPhotoName(photoOwners, to, aid);
					photoCopies.Add((from, to));
					r.FileName = to;
				}
				return r;
			}).ToList(),
		};
		return file;
	}

	private static void ClaimPhotoName(Dictionary<string, HashSet<Guid>> owners, string name, Guid assessmentId)
	{
		if (!owners.TryGetValue(name, out HashSet<Guid> set))
		{
			owners[name] = set = new HashSet<Guid>();
		}
		set.Add(assessmentId);
	}

	private static void ReplacePropertyChildren(IDbConnection c, IDbTransaction tx, DatasetIndex data, Guid sourceId, Guid targetId)
	{
		TableMap<ShapeRecord>.Delete(c, "PropertyID = @targetId", new { targetId }, tx);
		// The original's grid had one row per assessment and inserted a property's shapes once per row; de-duplicate.
		foreach (ShapeRecord s in data.Shapes[sourceId]
			.GroupBy(x => (x.ShapeType, x.TotalSqFt, x.LengthA, x.WidthA, x.LengthB, x.WidthB, x.LengthC, x.WidthC, x.StoriesA, x.StoriesB, x.StoriesC))
			.Select(g => g.First()))
		{
			ShapeRecord copy = Clone(s);
			copy.ShapeID = null;
			copy.PropertyID = targetId;
			TableMap<ShapeRecord>.Insert(c, copy, tx);
		}
		TableMap<CustomFieldResultRecord>.Delete(c, "ObjectIdentifier = @targetId", new { targetId }, tx);
		foreach (CustomFieldResultRecord r in data.CustomFieldsResults[sourceId])
		{
			CustomFieldResultRecord copy = Clone(r);
			copy.ObjectIdentifier = targetId;
			TableMap<CustomFieldResultRecord>.Insert(c, copy, tx);
		}
	}

	private static T Clone<T>(T source)
		where T : class, new() => TableMap<T>.Clone(source);
}
