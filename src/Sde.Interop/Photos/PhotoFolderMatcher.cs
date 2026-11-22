using System.Globalization;
using System.Text;

namespace Sde.Interop.Photos;

/// <summary>A structure a photo might belong to.</summary>
/// <param name="Id">The structure's PropertyID.</param>
/// <param name="Address">Street address as shown to the user, e.g. "123 Main St".</param>
/// <param name="Parcel">Parcel number, or null.</param>
/// <param name="RecordId">The community's own record or permit ID, or null.</param>
/// <param name="Latitude">Structure latitude, or null when unknown.</param>
/// <param name="Longitude">Structure longitude, or null when unknown.</param>
public sealed record PhotoMatchCandidate(Guid Id, string Address, string Parcel, string RecordId, double? Latitude, double? Longitude);

/// <summary>The structure chosen for a photo and why. <see cref="Candidate"/> is null when none could be chosen.</summary>
public sealed record PhotoMatch(string PhotoPath, PhotoMatchCandidate Candidate, string Reason, double? DistanceMeters);

/// <summary>Works out which structure each photo in a folder belongs to, from file names first and then from GPS.</summary>
public static class PhotoFolderMatcher
{
	private const double AmbiguousMeters = 10;

	private readonly record struct KeyEntry(PhotoMatchCandidate Candidate, string Key);

	/// <summary>
	/// Matches each photo, in the order given. A file name that contains a candidate's record ID wins, then its parcel
	/// number, then its house number and street name (ignoring case, spaces and punctuation). Otherwise the nearest
	/// candidate within <paramref name="toleranceMeters"/> of the photo's GPS position is used, unless two such
	/// candidates are within 10 m of each other, which is reported as ambiguous.
	/// </summary>
	public static IReadOnlyList<PhotoMatch> Match(IReadOnlyList<string> photoPaths, IReadOnlyList<PhotoMatchCandidate> candidates, double toleranceMeters = 40)
	{
		List<PhotoMatch> matches = new List<PhotoMatch>(photoPaths.Count);
		// Each structure's keys are worked out once, sorted longest-first so name matching stops as soon as a shorter key can't win.
		KeyEntry[] recordIds = BuildKeys(candidates, static c => Normalize(c.RecordId));
		KeyEntry[] parcels = BuildKeys(candidates, static c => Normalize(c.Parcel));
		KeyEntry[] addresses = BuildKeys(candidates, AddressKey);
		List<PhotoMatchCandidate> located = new List<PhotoMatchCandidate>(candidates.Count);
		for (int i = 0; i < candidates.Count; i++)
		{
			if (candidates[i].Latitude != null && candidates[i].Longitude != null)
			{
				located.Add(candidates[i]);
			}
		}
		for (int i = 0; i < photoPaths.Count; i++)
		{
			matches.Add(MatchOne(photoPaths[i], located, recordIds, parcels, addresses, toleranceMeters));
		}
		return matches;
	}

	private static KeyEntry[] BuildKeys(IReadOnlyList<PhotoMatchCandidate> candidates, Func<PhotoMatchCandidate, string> selector)
	{
		List<KeyEntry> list = new List<KeyEntry>(candidates.Count);
		for (int i = 0; i < candidates.Count; i++)
		{
			string key = selector(candidates[i]);
			if (key.Length > 0)
			{
				list.Add(new KeyEntry(candidates[i], key));
			}
		}
		list.Sort(static (a, b) => b.Key.Length.CompareTo(a.Key.Length));
		return list.ToArray();
	}

	private static PhotoMatch MatchOne(string path, List<PhotoMatchCandidate> located, KeyEntry[] recordIds, KeyEntry[] parcels, KeyEntry[] addresses, double toleranceMeters)
	{
		string name = Normalize(Path.GetFileNameWithoutExtension(path));

		PhotoMatchCandidate byName = FindByName(name, recordIds);
		if (byName != null)
		{
			return new PhotoMatch(path, byName, "file name has the record ID", null);
		}
		byName = FindByName(name, parcels);
		if (byName != null)
		{
			return new PhotoMatch(path, byName, "file name has the parcel number", null);
		}
		byName = FindByName(name, addresses);
		if (byName != null)
		{
			return new PhotoMatch(path, byName, "file name has the address", null);
		}

		PhotoInfo info;
		try
		{
			info = PhotoMetadata.Read(path);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			info = new PhotoInfo(null, null, null);
		}
		if (info.Latitude == null || info.Longitude == null || located.Count == 0)
		{
			return new PhotoMatch(path, null, "no match", null);
		}

		double pLat = info.Latitude.Value;
		double pLon = info.Longitude.Value;
		double maxLatDelta = (toleranceMeters / 111_000.0) + 1e-4;
		double cosLat = Math.Max(0.01, Math.Cos(pLat * (Math.PI / 180.0)));
		double maxLonDelta = (toleranceMeters / (111_000.0 * cosLat)) + 1e-4;

		PhotoMatchCandidate best1 = null;
		double bestDist1 = double.PositiveInfinity;
		PhotoMatchCandidate best2 = null;
		double bestDist2 = double.PositiveInfinity;

		for (int i = 0; i < located.Count; i++)
		{
			PhotoMatchCandidate c = located[i];
			double cLat = c.Latitude.Value;
			double cLon = c.Longitude.Value;
			if (Math.Abs(cLat - pLat) > maxLatDelta || Math.Abs(cLon - pLon) > maxLonDelta)
			{
				continue;
			}
			double d = DistanceMeters(pLat, pLon, cLat, cLon);
			if (d > toleranceMeters)
			{
				continue;
			}
			if (d < bestDist1)
			{
				best2 = best1;
				bestDist2 = bestDist1;
				best1 = c;
				bestDist1 = d;
			}
			else if (d < bestDist2)
			{
				best2 = c;
				bestDist2 = d;
			}
		}
		if (best1 == null)
		{
			return new PhotoMatch(path, null, "no match", null);
		}
		if (best2 != null)
		{
			double between = DistanceMeters(best1.Latitude.Value, best1.Longitude.Value,
				best2.Latitude.Value, best2.Longitude.Value);
			if (between <= AmbiguousMeters)
			{
				return new PhotoMatch(path, null, "ambiguous: two structures within 10 m", bestDist1);
			}
		}
		return new PhotoMatch(path, best1, string.Format(CultureInfo.InvariantCulture, "GPS, {0:0} m from the structure", bestDist1), bestDist1);
	}

	/// <summary>The one candidate whose key is in the file name (the longest key wins; a tie at the longest length matches nothing).</summary>
	private static PhotoMatchCandidate FindByName(string name, KeyEntry[] entries)
	{
		PhotoMatchCandidate best = null;
		int bestLength = 0;
		bool tie = false;
		for (int i = 0; i < entries.Length; i++)
		{
			string k = entries[i].Key;
			if (k.Length < bestLength)
			{
				break;
			}
			if (k.Length > name.Length || !name.Contains(k, StringComparison.Ordinal))
			{
				continue;
			}
			if (best == null)
			{
				best = entries[i].Candidate;
				bestLength = k.Length;
			}
			else if (!ReferenceEquals(best, entries[i].Candidate))
			{
				tie = true;
				break;
			}
		}
		return tie ? null : best;
	}

	/// <summary>House number plus the first word of the street name, e.g. "123 N Main St" gives "123n"; "123 Main St" gives "123main".</summary>
	private static string AddressKey(PhotoMatchCandidate c)
	{
		if (string.IsNullOrWhiteSpace(c.Address))
		{
			return "";
		}
		string[] words = c.Address.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
		if (words.Length < 2 || !char.IsDigit(words[0][0]))
		{
			return "";
		}
		return Normalize(words[0] + words[1]);
	}

	/// <summary>Lower case letters and digits only.</summary>
	private static string Normalize(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		Span<char> buf = text.Length <= 128 ? stackalloc char[text.Length] : new char[text.Length];
		int n = 0;
		for (int i = 0; i < text.Length; i++)
		{
			char ch = text[i];
			if (char.IsLetterOrDigit(ch))
			{
				buf[n++] = char.ToLowerInvariant(ch);
			}
		}
		return n == 0 ? "" : new string(buf.Slice(0, n));
	}

	/// <summary>Great-circle distance in meters (haversine).</summary>
	internal static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
	{
		const double EarthRadius = 6371008.8;
		double dLat = (lat2 - lat1) * Math.PI / 180;
		double dLon = (lon2 - lon1) * Math.PI / 180;
		double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
			+ Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
		return 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
	}
}
