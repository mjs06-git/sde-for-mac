using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Sde.Core;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Interop.Internal;
using Sde.Interop.Legacy;

namespace Sde.Interop;

/// <summary>
/// An SDE 2.x export converted to SDE 3 records. Pass <see cref="Data"/> (optionally narrowed with
/// <see cref="SdeDataset.Filter"/>) and <see cref="PhotosFolder"/> to <c>ExchangeService.Import</c>, which merges it
/// into the database and recalculates every assessment. Dispose to delete the staged photo copies.
/// </summary>
public sealed class LegacyReadResult : IDisposable
{
	public SdeDataset Data { get; init; }

	/// <summary>A temporary folder holding copies of the photos found, under the names in <see cref="SdeDataset.Attachments"/>.</summary>
	public string PhotosFolder { get; init; }

	/// <summary>Things that were skipped or couldn't be read.</summary>
	public List<string> Warnings { get; } = new();

	/// <summary>The SummaryData.xml index (for showing the structures before importing).</summary>
	public XmlSummaryData.NewDataSet Summary { get; init; }

	public void Dispose()
	{
		try
		{
			string root = Path.GetDirectoryName(PhotosFolder);
			if (root != null && Directory.Exists(root) && Path.GetFileName(root).StartsWith(LegacySde2Import.StagingPrefix, StringComparison.Ordinal))
			{
				Directory.Delete(root, recursive: true);
			}
		}
		catch (IOException)
		{
		}
	}
}

/// <summary>
/// Import of data exported by SDE 2.x (a folder with SummaryData.xml and one sub-folder per structure holding
/// PropertyData.xml and its assessment files).
///
/// Port of the legacy path of decomp/wpfSDE/wpfSDE.ImportExport/ucImportSDEdata.xaml.cs: LoadGrid (SummaryData.xml,
/// lines 222-285), BuildMasterImportList (lines 485-748, the field-by-field mapping), the photo search in ImportData
/// (lines 357-376) and the depreciation handling of RunCalculations/GetDepreciationValue (lines 883-893, 1060-1064).
/// SDE 2.x lookup IDs are translated with LookupViewService.GetSingleLookupByOldID: the Lookup row of that type whose
/// OldID matches (damage element 15 → 19 quirk included). The original wrote straight to the database; this returns
/// an <see cref="SdeDataset"/> for <c>ExchangeService.Import</c>, which also does the recalculation.
///
/// Original bugs fixed:
/// <list type="bullet">
/// <item>PropertyData.xml was rewritten in place with its line breaks removed (line 497-499); it is now only read
/// (the line breaks are still removed, in memory, so values parse as they did).</item>
/// <item>Cost adjustments were mapped through lookup type 10 ("Refinement", IDs 74-91) instead of type 23 ("Cost
/// Adjustments", IDs 365-382) that SDE 3 uses everywhere else, so imported adjustments were orphaned and the unit was
/// blank; they are mapped to type 23 with its unit.</item>
/// <item>Missing elements (null strings) crashed the import through .ToString(); a property without assessments
/// crashed BaseCostPerSqFt; a sub-folder without PropertyData.xml (e.g. photos) stopped everything. Those are now
/// handled, and unreadable files are reported as warnings.</item>
/// <item>Empty dates became 1 January 0001 (Convert.ToDateTime(null)); empty BFE, flood depth and duration became 0.
/// They are left blank. Percentages with decimals ("52.3%") were dropped because they were checked with int.Parse.</item>
/// <item>The undetermined-damage codes 1.00001-1.00006 stored in tblDepreciation.DepreciationValue were also stored
/// as the depreciation % ; the % is left blank for them.</item>
/// <item>Photos were found by file name anywhere under the folder and copied over existing files of the same name
/// (overwrite: true). They are now looked for in the structure's own folder first and staged under names unique to
/// the assessment, so two structures' "IMG_0001.jpg" can't overwrite each other. Windows paths ("C:\…\a.jpg") are
/// handled on macOS.</item>
/// <item>A property without a TotalSquareFoot still got an empty 'c' shape.</item>
/// <item>Story counts: SDE 2.x OldIDs 2 and 3 exist twice in lookup type 14 and the first (non-residential) row was
/// always taken; the row for the structure's type is used.</item>
/// <item>An unknown value / cost-of-repair basis became 0; it falls back to Computed ACV (390) / Computed Damages (393),
/// the defaults of a new assessment.</item>
/// </list>
/// </summary>
public static class LegacySde2Import
{
	internal const string StagingPrefix = "sde-legacy-";

	/// <summary>The undetermined-damage codes SDE 2.x stored in tblDepreciation.DepreciationValue (IsPhysicalDamage).</summary>
	private static readonly HashSet<string> PhysicalDamageCodes = new HashSet<string>(StringComparer.Ordinal) { "1.00001", "1.00002", "1.00003", "1.00004", "1.00005", "1.00006" };

	private static readonly XmlReaderSettings DefaultReaderSettings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Auto };
	private static readonly XmlReaderSettings IgnoreWhitespaceReaderSettings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Auto, IgnoreWhitespace = true, CheckCharacters = false };
	private static readonly XmlSerializer SummarySerializer = new XmlSerializer(typeof(XmlSummaryData.NewDataSet));
	private static readonly XmlSerializer PropertySerializer = new XmlSerializer(typeof(XmlPropertyData.NewDataSet));
	private static readonly XmlSerializer AssessmentSerializer = new XmlSerializer(typeof(XmlAssessmentData.NewDataSet));

	public static LegacyReadResult Read(string folder, ReferenceData reference)
	{
		string summaryPath = Path.Combine(folder, SdeExchangeFolder.LegacySummaryFileName);
		if (!File.Exists(summaryPath))
		{
			throw new FileNotFoundException("This folder is not an SDE 2.x export (it has no SummaryData.xml).", summaryPath);
		}

		string staging = Path.Combine(Path.GetTempPath(), StagingPrefix + Guid.NewGuid().ToString("N"), "photos");
		Directory.CreateDirectory(staging);
		LegacyReadResult result = new LegacyReadResult
		{
			Data = new SdeDataset(),
			PhotosFolder = staging,
			Summary = Deserialize<XmlSummaryData.NewDataSet>(summaryPath, ignoreWhitespace: false, removeLineBreaks: false),
		};
		Mapper map = new Mapper(reference, result);

		List<(string Folder, XmlPropertyData.NewDataSet Property)> properties = new();
		List<(string Folder, string File, XmlAssessmentData.NewDataSet Assessment)> assessments = new();
		foreach (string sub in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
		{
			string propertyFile = Path.Combine(sub, "PropertyData.xml");
			if (!File.Exists(propertyFile))
			{
				result.Warnings.Add($"Folder '{Path.GetFileName(sub)}' has no PropertyData.xml and was skipped.");
				continue;
			}
			try
			{
				XmlPropertyData.NewDataSet p = Deserialize<XmlPropertyData.NewDataSet>(propertyFile, ignoreWhitespace: true, removeLineBreaks: true);
				if (p?.tblProperty == null || !Guid.TryParse(p.tblProperty.PropertyID, out _))
				{
					result.Warnings.Add($"'{Relative(folder, propertyFile)}' has no valid PropertyID and was skipped.");
					continue;
				}
				properties.Add((sub, p));
			}
			catch (Exception ex) when (ex is InvalidOperationException or XmlException)
			{
				result.Warnings.Add($"'{Relative(folder, propertyFile)}' could not be read: {Inner(ex)}");
				continue;
			}

			foreach (string file in Directory.GetFiles(sub).Where(f => Path.GetFileName(f).Contains("assessmentdata", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal))
			{
				try
				{
					XmlAssessmentData.NewDataSet a = Deserialize<XmlAssessmentData.NewDataSet>(file, ignoreWhitespace: true, removeLineBreaks: false);
					if (a?.tblAssessment == null || !Guid.TryParse(a.tblAssessment.AssessmentID, out _) || !Guid.TryParse(a.tblAssessment.PropertyID, out _))
					{
						result.Warnings.Add($"'{Relative(folder, file)}' has no valid AssessmentID/PropertyID and was skipped.");
						continue;
					}
					assessments.Add((sub, file, a));
				}
				catch (Exception ex) when (ex is InvalidOperationException or XmlException)
				{
					result.Warnings.Add($"'{Relative(folder, file)}' could not be read: {Inner(ex)}");
				}
			}
		}

		// Links (to files or folders) are left out: one could point at any file on the Mac.
		ILookup<string, FileInfo> filesByName = new DirectoryInfo(folder).GetFiles("*", new EnumerationOptions
		{
			RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false, MatchType = MatchType.Win32,
		}).ToLookup(f => f.Name, StringComparer.OrdinalIgnoreCase);
		Dictionary<Guid, XmlAssessmentData.NewDataSet> firstAssessmentByProperty = new Dictionary<Guid, XmlAssessmentData.NewDataSet>(properties.Count);
		foreach ((string _, string _, XmlAssessmentData.NewDataSet a) in assessments)
		{
			Guid pid = Guid.Parse(a.tblAssessment.PropertyID);
			firstAssessmentByProperty.TryAdd(pid, a);
		}
		Dictionary<Guid, PropertyRecord> propertiesById = new Dictionary<Guid, PropertyRecord>(properties.Count);
		foreach ((string sub, XmlPropertyData.NewDataSet p) in properties)
		{
			Guid id = new Guid(p.tblProperty.PropertyID);
			firstAssessmentByProperty.TryGetValue(id, out XmlAssessmentData.NewDataSet first);
			PropertyRecord property = map.Property(p, first);
			result.Data.Properties.Add(property);
			propertiesById.TryAdd(property.PropertyID, property);
			if (p.tblSquareFoot_Property != null)
			{
				result.Data.Shapes.Add(new ShapeRecord { PropertyID = id, ShapeType = "c", TotalSqFt = p.tblSquareFoot_Property.TotalSquareFoot });
			}
		}
		HashSet<Guid> seenAssessments = new HashSet<Guid>(assessments.Count);
		foreach ((string sub, string file, XmlAssessmentData.NewDataSet a) in assessments)
		{
			if (!propertiesById.TryGetValue(Guid.Parse(a.tblAssessment.PropertyID), out PropertyRecord property))
			{
				result.Warnings.Add($"'{Relative(folder, file)}' belongs to structure {a.tblAssessment.PropertyID}, which is not in the export; skipped.");
				continue;
			}
			Guid aid = Guid.Parse(a.tblAssessment.AssessmentID);
			if (!seenAssessments.Add(aid))
			{
				result.Warnings.Add($"'{Relative(folder, file)}' repeats assessment {a.tblAssessment.AssessmentID}; skipped.");
				continue;
			}
			map.Assessment(a, property, sub, filesByName);
		}

		// Cross-check against the SummaryData.xml index the original showed in its grid.
		foreach (XmlSummaryData.NewDataSetProperties s in result.Summary?.Properties ?? Array.Empty<XmlSummaryData.NewDataSetProperties>())
		{
			if (!Guid.TryParse(s.PropertyID, out Guid sid) || !propertiesById.ContainsKey(sid))
			{
				result.Warnings.Add($"SummaryData.xml lists structure {s.PropertyID} ({s.StreetAddress}) but its PropertyData.xml was not found.");
			}
		}
		foreach (XmlSummaryData.NewDataSetAssessments s in result.Summary?.Assessments ?? Array.Empty<XmlSummaryData.NewDataSetAssessments>())
		{
			if (!Guid.TryParse(s.AssessmentID, out Guid aid) || !seenAssessments.Contains(aid))
			{
				result.Warnings.Add($"SummaryData.xml lists assessment {s.AssessmentID} but its assessment file was not found.");
			}
		}
		return result;
	}

	/// <summary>
	/// XmlSerializer with the original's reader settings (ConformanceLevel.Auto; IgnoreWhitespace and CheckCharacters off
	/// for the per-structure files). PropertyData.xml had its line breaks removed before reading (line 497-499).
	/// </summary>
	private static T Deserialize<T>(string path, bool ignoreWhitespace, bool removeLineBreaks)
		where T : class
	{
		string text = File.ReadAllText(path);
		if (removeLineBreaks)
		{
			text = text.Replace("\r\n", "", StringComparison.Ordinal);
		}
		XmlReaderSettings settings = ignoreWhitespace ? IgnoreWhitespaceReaderSettings : DefaultReaderSettings;
		XmlSerializer serializer = typeof(T) == typeof(XmlSummaryData.NewDataSet) ? SummarySerializer
			: typeof(T) == typeof(XmlPropertyData.NewDataSet) ? PropertySerializer
			: typeof(T) == typeof(XmlAssessmentData.NewDataSet) ? AssessmentSerializer
			: new XmlSerializer(typeof(T));
		using StringReader sr = new StringReader(text);
		using XmlReader reader = XmlReader.Create(sr, settings);
		return serializer.Deserialize(reader) as T;
	}

	private static string Relative(string root, string path) => Path.GetRelativePath(root, path);

	private static string Inner(Exception ex) => ex.InnerException?.Message ?? ex.Message;

	/// <summary>The field-by-field mapping of BuildMasterImportList.</summary>
	private sealed class Mapper
	{
		private readonly ReferenceData _ref;
		private readonly LegacyReadResult _result;
		private readonly Dictionary<(int Type, int OldId), int> _byOldId = new Dictionary<(int, int), int>();
		private readonly Dictionary<(int Type, string Name), int> _byName = new Dictionary<(int, string), int>();
		private readonly Dictionary<int, List<LookupRecord>> _storiesByOldId = new Dictionary<int, List<LookupRecord>>();
		private readonly Dictionary<string, int> _undeterminedByCode = new Dictionary<string, int>(StringComparer.Ordinal);
		private readonly HashSet<int> _stateIds;
		private readonly HashSet<int> _countyIds;

		public Mapper(ReferenceData reference, LegacyReadResult result)
		{
			_ref = reference;
			_result = result;
			foreach (LookupRecord l in reference.Lookups)
			{
				if (l.OldID is int oldId)
				{
					_byOldId.TryAdd((l.LookupTypeID, oldId), l.LookupID);
					if (l.LookupTypeID == LookupTypes.Stories)
					{
						if (!_storiesByOldId.TryGetValue(oldId, out List<LookupRecord> list))
						{
							list = new List<LookupRecord>();
							_storiesByOldId[oldId] = list;
						}
						list.Add(l);
					}
				}
				if (l.LookupFieldName != null)
				{
					_byName.TryAdd((l.LookupTypeID, l.LookupFieldName.ToLowerInvariant()), l.LookupID);
				}
				if (l.LookupTypeID == LookupTypes.UndeterminedDamageType && l.LookupValue1 != null)
				{
					_undeterminedByCode.TryAdd(l.LookupValue1, l.LookupID);
				}
			}
			_stateIds = new HashSet<int>(reference.States.Count);
			foreach (StateRecord s in reference.States)
			{
				_stateIds.Add(s.StateID);
			}
			_countyIds = new HashSet<int>(reference.Counties.Count);
			foreach (CountyRecord c in reference.Counties)
			{
				_countyIds.Add(c.CountyID);
			}
		}

		private SdeDataset Data => _result.Data;

		/// <summary>LookupViewService.GetSingleLookupByOldID: the lookup of this type whose OldID matches; null when none (the original's 0).</summary>
		public int? ByOldId(int? oldId, int lookupType)
		{
			if (oldId is not int old)
			{
				return null;
			}
			if (lookupType == LookupTypes.DamageElement && old == 15)
			{
				old = 19;
			}
			return _byOldId.TryGetValue((lookupType, old), out int id) ? id : null;
		}

		/// <summary>
		/// Stories (type 14) reuse OldIDs 2 and 3 for a non-residential row (102/103, LookupValue1 "NonResidential") and a
		/// residential one (104/105, "Residential"). The original took the first, so every residential structure got a
		/// non-residential story count; the row matching the structure type is preferred.
		/// </summary>
		private int? Story(int? oldId, int structureTypeId)
		{
			if (oldId is not int old || !_storiesByOldId.TryGetValue(old, out List<LookupRecord> rows))
			{
				return null;
			}
			string kind = structureTypeId == 1 ? "Residential" : "NonResidential";
			for (int i = 0; i < rows.Count; i++)
			{
				if (rows[i].LookupValue1 == kind)
				{
					return rows[i].LookupID;
				}
			}
			return rows.Count > 0 ? rows[0].LookupID : null;
		}

		/// <summary>LookupViewService.GetSingleLookupByName, ignoring case.</summary>
		private int? ByName(string name, int lookupType) =>
			string.IsNullOrWhiteSpace(name) ? null
			: _byName.TryGetValue((lookupType, name.Trim().ToLowerInvariant()), out int id) ? id : null;

		/// <summary>VerifyStateID / VerifyCountyID: the ID if it exists (SDE 2 and 3 share the State and County tables).</summary>
		private int? State(int? id) => id is int s && _stateIds.Contains(s) ? id : null;

		private int? County(int? id) => id is int c && _countyIds.Contains(c) ? id : null;

		public PropertyRecord Property(XmlPropertyData.NewDataSet p, XmlAssessmentData.NewDataSet firstAssessment)
		{
			XmlPropertyData.NewDataSetTblProperty t = p.tblProperty;
			XmlPropertyData.NewDataSetTblPropertyAddress addr = p.tblPropertyAddress ?? new();
			XmlPropertyData.NewDataSetTblOwnerAddress owner = p.tblOwnerAddress ?? new();
			XmlPropertyData.NewDataSetTblNFIPInfo nfip = p.tblNFIPInfo ?? new();
			XmlPropertyData.NewDataSetTblAdjustments adj = p.tblAdjustments ?? new();
			int structureType = t.StructureType == "Residential" ? 1 : 2;
			return new PropertyRecord
			{
				PropertyID = new Guid(t.PropertyID),
				Active = true,
				ImportDate = Text.Now(),
				StructureTypeID = structureType,
				OwnerFirstName = addr.FirstName,
				OwnerLastName = addr.LastName,
				StreetNumber = addr.AddressLine1,
				StreetName = addr.AddressLine2,
				StreetSuffixID = ByOldId(addr.StreetSuffixID, LookupTypes.StreetSuffix),
				City = addr.City,
				CountyID = County(addr.CountyID),
				StateID = State(addr.StateID),
				ZipCode = addr.Zip,
				PhoneNumber = addr.AddressPhone,
				CellPhone = addr.CellPhone,
				Subdivision = t.Subdivision,
				ParcelNumber = t.ParcelNumber,
				LotNumber = t.LotNumber,
				Datum = t.Datum,
				MailingOwnerFirstName = owner.FirstName,
				MailingOwnerLastName = owner.LastName,
				MailingStreetNumber = owner.AddressLine1,
				MailingStreetName = owner.AddressLine2,
				MailingStreetSuffixID = ByOldId(owner.StreetSuffixID, LookupTypes.StreetSuffix),
				MailingCity = owner.City,
				MailingCountyID = County(owner.CountyID),
				MailingStateID = State(owner.StateID),
				MailingZipCode = owner.Zip,
				MailingPhoneNumber = owner.AddressPhone,
				MailingCareOf = owner.CareOf,
				Latitude = Round(Text.ParseDecimal(t.Latitude), 6),
				Longitude = Round(Text.ParseDecimal(t.Longitude), 6),
				FirstFloorElevation = Text.ParseDecimal(t.FloorElevation),
				YearOfConstruction = Text.TryParseInt(t.YearOfConstruction, out int year) ? year : null,
				StructureQualityID = ByOldId(t.QualityID, LookupTypes.StructureQuality),
				FoundationTypeID = ByOldId(adj.FoundationID, LookupTypes.Foundation),
				SuperStructureTypeID = ByOldId(adj.SuperStructureID, LookupTypes.Superstructure),
				RoofCoveringTypeID = ByOldId(adj.RoofingID, LookupTypes.RoofCovering),
				ExteriorFinishTypeID = ByOldId(adj.ExteriorFinishID, LookupTypes.ExteriorFinish),
				HVACTypeID = ByOldId(adj.HVACID, LookupTypes.Hvac),
				Conveyance = ByOldId(adj.ConveyanceID, LookupTypes.YesNo),
				SprinklerSystem = ByOldId(adj.SprinklerID, LookupTypes.SprinklerSystem),
				StoryTypeID = Story(adj.StoryID, structureType),
				StructureUseID = ByOldId(t.StructureUseID, LookupTypes.StructureUse),
				ResidenceTypeID = ByOldId(t.ResidenceTypeID, LookupTypes.ResidenceType),
				StructureInformation = t.ResidenceInformation,
				LastUpdate = Text.ParseDate(t.DateOfLastModification),
				SquareFootage = p.tblSquareFoot_Property?.TotalSquareFoot,
				NFIPCID = nfip.CommunityID ?? "",
				NFIPCommunityName = nfip.CommunityName,
				FIRMPanelDate = Text.ParseDate(nfip.FirmPanelDate),
				FIRMPanelNumber = nfip.FirmPanelNumber,
				FIRMZone = ByOldId(nfip.FirmZoneID, LookupTypes.FirmZone),
				PanelSuffix = nfip.Suffix,
				RegulatoryFloodway = ByOldId(nfip.FloodWayID, LookupTypes.RegulatoryFloodway),
				BFE = Text.ParseDecimal(nfip.BaseFloodElevation),
				// The first assessment's base cost and community use (the original crashed when there was none).
				BaseCostPerSqFt = firstAssessment?.tblAssessment.BaseCostPerSqFt ?? "",
				CommunityInformation = firstAssessment?.tblAssessment.CommunityUse ?? "",
			};
		}

		public void Assessment(XmlAssessmentData.NewDataSet x, PropertyRecord property, string propertyFolder, ILookup<string, FileInfo> filesByName)
		{
			XmlAssessmentData.NewDataSetTblAssessment t = x.tblAssessment;
			XmlAssessmentData.NewDataSetTblAssessmentPreview preview = x.tblAssessmentPreview ?? new();
			XmlAssessmentData.NewDataSetTblDepreciation dep = x.tblDepreciation ?? new();
			XmlAssessmentData.NewDataSetTblOutputSummary output = x.tblOutputSummary ?? new();
			Guid aid = new Guid(t.AssessmentID);

			string code = dep.DepreciationValue.ToString(CultureInfo.InvariantCulture);
			bool physicalDamage = PhysicalDamageCodes.Contains(code);
			int? ratingId = ByOldId(dep.DepreciationValueID, LookupTypes.DepreciationRating);
			decimal? percentDamaged = Text.ParseDecimal(preview.PercentDamaged);

			AssessmentRecord a = new AssessmentRecord
			{
				AssessmentID = aid,
				PropertyID = property.PropertyID,
				Active = true,
				LastUpdate = Text.ParseDate(t.DateOfLastModification),
				DateofInspection = Text.ParseDate(t.DateOfAssessment),
				InspectorName = preview.InspectorName,
				InspectorPhoneNumber = preview.InspectorPhone,
				DateDamageOccurred = Text.ParseDate(t.DateOfDamage),
				CauseofDamageID = ByOldId(t.DamageTypeID, LookupTypes.CauseOfDamage),
				DurationOfFlood = Text.ParseDecimal(t.DurationOfFlood),
				DurationOfFloodUnitsID = ByName(t.UnitMeasureOfTime, LookupTypes.DurationUnits),
				DepthOfFlood = Text.ParseDecimal(t.DepthOfFlood),
				DepthOfFloodAboveFFE = Text.ParseDecimal(t.DepthOfFloodAboveFloor),
				DepreciationRatingID = ratingId,
				DepreciationValue = Depreciation(dep.DepreciationValue, physicalDamage, property, ratingId, aid),
				GeographicAdjustment = t.LocalMultiplier,
				PercentDamage = percentDamaged,
				BaseCost = Text.ParseDecimal(t.BaseCostPerSqFt),
				CostData = dep.ReferenceDocument,
				CostDataDate = Text.ParseDate(dep.PublicationDate),
				DepreciationExplanation = dep.Explanation,
				MarketAppraisal = output.ProfessionalAppraisal,
				TaxAssessedValue = output.TaxAssessment,
				FactorAdjustment = output.FactorAdjustment,
				ContractorEstimate = output.ContractorEstimate,
				CommunityEstimate = output.CommunityEstimate,
				ExistingImprovementPercent = output.PreExistingImprovements,
				CauseOfDamageDescription = t.OtherDamage,
				ValueOfBuildingID = ByOldId(output.ValueOfBuildingID, LookupTypes.ValueOfBuildingBasis) ?? LookupIds.ValueComputedAcv,
				CostOfRepairsID = ByOldId(output.CostOfRepairsID, LookupTypes.CostOfRepairsBasis) ?? LookupIds.RepairComputedDamages,
				ReplacementCost = output.ReplacementCost,
				ComputedDamages = output.ComputedDamages,
				ComputedActualCashValue = output.ComputedActualCashValue,
				RepairReconstructPercent = Text.ParseDecimal(t.RepairRecon),
				TotalReplacementCost = output.ReplacementCost,
				TotalEstimatedDamages = output.ComputedDamages,
				UndeterminedDamageTypeID = _undeterminedByCode.TryGetValue(code, out int undeterminedId) ? undeterminedId : null,
				PhysicalDamage = physicalDamage,
			};
			Data.Assessments.Add(a);

			// Five additional adjustments: three quantity × unit cost rows, two lump sums (lines 642-691).
			XmlAssessmentData.NewDataSetTblAdditionalAdjustments add = x.tblAdditionalAdjustments;
			if (add != null)
			{
				AddExtra(aid, 1, add.AddRefine1Name, add.AddRefine1Qty, add.AddRefine1UnitCost);
				AddExtra(aid, 2, add.AddRefine2Name, add.AddRefine2Qty, add.AddRefine2UnitCost);
				AddExtra(aid, 3, add.AddRefine3Name, add.AddRefine3Qty, add.AddRefine3UnitCost);
				AddExtra(aid, 4, add.LumpSumRefine1Name, 1, add.LumpSumRefine1Value);
				AddExtra(aid, 5, add.LumpSumRefine2Name, 1, add.LumpSumRefine2Value);
			}

			foreach (XmlAssessmentData.NewDataSetTblAssessment_Refinement r in x.tblAssessment_Refinement ?? Array.Empty<XmlAssessmentData.NewDataSetTblAssessment_Refinement>())
			{
				int? lookupId = ByOldId(r.RefinementID, LookupTypes.CostAdjustment);
				if (lookupId == null)
				{
					_result.Warnings.Add($"Assessment {aid}: unknown SDE 2.x cost adjustment {r.RefinementID} skipped.");
					continue;
				}
				Data.CostAdjustments.Add(new CostAdjustmentResultRecord
				{
					ResultID = Guid.NewGuid(),
					AssessmentID = aid,
					CostAdjustmentLookupID = lookupId.Value,
					Units = _ref.Lookup(lookupId)?.LookupValue1,
					Quantity = r.Quantity,
					UnitCost = r.UnitCost,
					TotalCost = r.UnitCost * r.Quantity,
				});
			}

			foreach (XmlAssessmentData.NewDataSetTblAssessment_Damage d in x.tblAssessment_Damage ?? Array.Empty<XmlAssessmentData.NewDataSetTblAssessment_Damage>())
			{
				int? calcId = ByOldId(d.DamageID, LookupTypes.DamageElement);
				if (calcId == null)
				{
					_result.Warnings.Add($"Assessment {aid}: unknown SDE 2.x damage element {d.DamageID} skipped.");
					continue;
				}
				Data.ElementPercentages.Add(new ElementPercentageRecord
				{
					ElementID = Guid.NewGuid(),
					AssessmentID = aid,
					CalcID = calcId.Value,
					ElementName = _ref.LookupName(calcId),
					UserDamage = d.PercentDamage,
				});
			}

			foreach (XmlAssessmentData.NewDataSetTblAttachment att in x.tblAttachment ?? Array.Empty<XmlAssessmentData.NewDataSetTblAttachment>())
			{
				Data.Attachments.Add(Photo(aid, att, propertyFolder, filesByName));
			}
		}

		/// <summary>
		/// The % passed to the engine and stored. SDE 2.x's -1 means "look it up from the condition rating"
		/// (GetDepreciationValue); undetermined-damage codes aren't percentages; anything else is kept as is, as the
		/// original did, with a warning when it looks like a fraction rather than a percentage.
		/// </summary>
		private decimal? Depreciation(decimal value, bool physicalDamage, PropertyRecord p, int? ratingId, Guid aid)
		{
			if (physicalDamage)
			{
				return null;
			}
			if (value == -1m)
			{
				decimal pct = _ref.DepreciationPercent(p.StoryTypeID ?? 0, p.StructureUseID ?? 0, p.StructureTypeID ?? 0, ratingId ?? 0);
				return pct == 0m ? null : pct;
			}
			if (value > 0m && value < 1m)
			{
				_result.Warnings.Add($"Assessment {aid}: depreciation {value.ToString(CultureInfo.InvariantCulture)} was imported as {value.ToString(CultureInfo.InvariantCulture)}% (as FEMA's SDE 3 did); check it.");
			}
			return value;
		}

		private void AddExtra(Guid aid, int zOrder, string name, int quantity, decimal unitCost) =>
			Data.AdditionalAdjustments.Add(new AdditionalAdjustmentRecord
			{
				AdditionalAdjustmentID = Guid.NewGuid(),
				AssessmentID = aid,
				zOrder = zOrder,
				AdjustmentName = name,
				Quantity = quantity,
				UnitCost = unitCost,
				TotalCost = quantity * unitCost,
			});

		/// <summary>
		/// The attachment row, and a staged copy of the photo. The file is looked for by name (ignoring case) in the
		/// structure's folder first, then anywhere under the export, as the original searched.
		/// </summary>
		private AttachmentRecord Photo(Guid aid, XmlAssessmentData.NewDataSetTblAttachment att, string propertyFolder, ILookup<string, FileInfo> filesByName)
		{
			string original = FileNameOf(att.thePath);
			AttachmentRecord r = new AttachmentRecord
			{
				AssessmentID = aid,
				Description = att.Description,
				DefaultImage = IsTrue(att.DisplayDefault),
				Active = true,
				FileName = "",
			};
			if (original.Length == 0)
			{
				return r;
			}
			string folderPrefix = propertyFolder + Path.DirectorySeparatorChar;
			FileInfo found = null;
			foreach (FileInfo f in filesByName[original])
			{
				if (f.FullName.StartsWith(folderPrefix, StringComparison.Ordinal))
				{
					found = f;
					break;
				}
				found ??= f;
			}
			r.FileName = StagedName(aid, original);
			if (found == null)
			{
				_result.Warnings.Add($"Assessment {aid}: photo '{original}' was not found in the export folder.");
				return r;
			}
			File.Copy(found.FullName, Path.Combine(_result.PhotosFolder, r.FileName), overwrite: true);
			return r;
		}

		/// <summary>A name unique to the assessment and photo, stable across re-imports (so an overwrite replaces the same file).</summary>
		private static string StagedName(Guid aid, string original)
		{
			byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(aid.ToString() + "/" + original.ToLowerInvariant()));
			return new Guid(hash) + Path.GetExtension(original).ToLowerInvariant();
		}

		/// <summary>The file name of a Windows or Unix path (new FileInfo(path).Name on Windows).</summary>
		private static string FileNameOf(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return "";
			}
			string s = path.Trim();
			int cut = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
			return cut >= 0 ? s[(cut + 1)..] : s;
		}

		/// <summary>Convert.ToBoolean accepted only "True"/"False"; "1"/"-1"/"yes" are accepted too.</summary>
		private static bool IsTrue(string text)
		{
			string s = (text ?? "").Trim();
			return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s == "-1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
		}

		private static decimal? Round(decimal? v, int decimals) => v is decimal d ? Math.Round(d, decimals, MidpointRounding.AwayFromZero) : null;
	}
}
