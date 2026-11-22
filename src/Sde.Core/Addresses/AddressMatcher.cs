using Sde.Core;
using Sde.Core.Records;

namespace Sde.Core.Addresses;

/// <summary>
/// An address reduced to what identifies a building: the house number, the street name (lower case, directions and
/// ordinals written one way, without the suffix), the street suffix as FEMA's choice, the unit and the 5-digit ZIP.
/// Made by <see cref="AddressMatcher.KeyFor(PropertyRecord)"/>.
/// </summary>
public sealed record AddressKey(string HouseNumber, string Street, int? SuffixId, string Unit, string Zip)
{
	/// <summary>Without both a house number and a street name an address can't be matched.</summary>
	public bool CanMatch => HouseNumber != null && Street != null;

	/// <summary>The house number and street, the part every match must share.</summary>
	internal string IndexKey => HouseNumber + "|" + Street;
}

/// <summary>
/// Matches addresses written in different ways: "123 N. Main St., Apt 4", "123 North Main Street" and "123 north
/// main" are the same building. Case and punctuation are ignored, "North"/"N" and "First"/"1st" are the same, and the
/// street suffix is compared as FEMA's choice, recognised by name or by any abbreviation in FEMA's
/// StreetSuffixAbbreviation table ("st", "ave", "av"). When only one side has a suffix (or a ZIP), the other part
/// decides. Units are ignored unless <see cref="MatchUnit"/> is set. Used to find a record's structure when an import
/// matches by address; it never picks between several matches (the caller shows them as ambiguous).
/// </summary>
public sealed class AddressMatcher
{
	/// <summary>Words that start the unit part of an address.</summary>
	private static readonly HashSet<string> UnitWords = new(StringComparer.Ordinal)
	{
		"apt", "apartment", "unit", "ste", "suite", "bldg", "building", "rm", "room", "fl", "floor", "lot", "trlr", "trailer", "spc", "space", "no",
	};

	private static readonly Dictionary<string, string> Directions = new(StringComparer.Ordinal)
	{
		["north"] = "n", ["south"] = "s", ["east"] = "e", ["west"] = "w",
		["northeast"] = "ne", ["northwest"] = "nw", ["southeast"] = "se", ["southwest"] = "sw",
	};

	private static readonly HashSet<string> DirectionAbbreviations = new(Directions.Values, StringComparer.Ordinal);

	private static readonly Dictionary<string, string> Ordinals = new(StringComparer.Ordinal)
	{
		["first"] = "1st", ["second"] = "2nd", ["third"] = "3rd", ["fourth"] = "4th", ["fifth"] = "5th",
		["sixth"] = "6th", ["seventh"] = "7th", ["eighth"] = "8th", ["ninth"] = "9th", ["tenth"] = "10th",
	};

	private readonly Dictionary<string, int> _suffixes = new(StringComparer.Ordinal);

	public AddressMatcher(ReferenceData reference, bool matchUnit = false)
	{
		MatchUnit = matchUnit;
		// FEMA's suffix names ("Street"), their own abbreviations (LookupValue1 "st, str") and the abbreviation table.
		foreach (LookupRecord l in reference.OfType(LookupTypes.StreetSuffix))
		{
			Add(l.LookupFieldName, l.LookupID);
			foreach (string v in (l.LookupValue1 ?? "").Split(','))
			{
				Add(v, l.LookupID);
			}
		}
		foreach (StreetSuffixRecord a in reference.StreetSuffixes)
		{
			Add(a.StreetSuffixAbbreviation, a.StreetSuffixID);
		}
	}

	/// <summary>Whether the unit (apartment, suite) has to match as well.</summary>
	public bool MatchUnit { get; }

	/// <summary>The key of a structure's address (its unit is whatever follows the house number).</summary>
	public AddressKey KeyFor(PropertyRecord p) => KeyFor(p.StreetNumber, p.StreetName, p.StreetSuffixID, p.ZipCode);

	/// <summary>
	/// The key of an address in parts. <paramref name="streetNumber"/> may carry the unit ("123 Apt 4", "123 4B"), and
	/// <paramref name="streetName"/> may carry the whole rest of the address ("N Main St Apt 4").
	/// </summary>
	public AddressKey KeyFor(string streetNumber, string streetName, int? suffixId, string zip = null)
	{
		List<string> number = Tokens(streetNumber);
		List<string> street = Tokens(streetName);
		if (number.Count == 0 && street.Count > 0 && IsHouseNumber(street[0]))
		{
			number.Add(street[0]);
			street.RemoveAt(0);
		}
		string house = number.Count > 0 && IsHouseNumber(number[0]) ? ParcelNumbers.WithoutLeadingZeros(number[0]) : null;
		List<string> unit = null;
		int unitStart = house == null ? 0 : 1;
		for (int i = unitStart; i < number.Count; i++)
		{
			if (!UnitWords.Contains(number[i]))
			{
				unit ??= new List<string>();
				unit.Add(ParcelNumbers.WithoutLeadingZeros(number[i]));
			}
		}

		int cut = -1;
		for (int i = 0; i < street.Count; i++)
		{
			if (UnitWords.Contains(street[i]))
			{
				cut = i;
				break;
			}
		}
		if (cut >= 0)
		{
			for (int i = cut + 1; i < street.Count; i++)
			{
				unit ??= new List<string>();
				unit.Add(ParcelNumbers.WithoutLeadingZeros(street[i]));
			}
			street.RemoveRange(cut, street.Count - cut);
		}
		for (int i = 0; i < street.Count; i++)
		{
			string t = street[i];
			if (Directions.TryGetValue(t, out string dir))
			{
				street[i] = dir;
			}
			else if (Ordinals.TryGetValue(t, out string ord))
			{
				street[i] = ord;
			}
		}

		// A trailing suffix ("Main St"), or a suffix before a trailing direction ("Main St N").
		int? suffix = suffixId;
		if (street.Count >= 3 && DirectionAbbreviations.Contains(street[^1]) && _suffixes.TryGetValue(street[^2], out int beforeDirection))
		{
			suffix ??= beforeDirection;
			street.RemoveAt(street.Count - 2);
		}
		else if (street.Count >= 2 && _suffixes.TryGetValue(street[^1], out int last))
		{
			suffix ??= last;
			street.RemoveAt(street.Count - 1);
		}

		string zip5 = FirstFiveDigits(zip);
		return new AddressKey(
			house,
			street.Count == 0 ? null : string.Join(" ", street),
			suffix,
			unit == null ? null : string.Join(" ", unit),
			zip5);
	}

	/// <summary>Whether two keys are the same building (see the class notes).</summary>
	public bool Same(AddressKey a, AddressKey b)
	{
		if (a == null || b == null || !a.CanMatch || !b.CanMatch
			|| !string.Equals(a.HouseNumber, b.HouseNumber, StringComparison.Ordinal)
			|| !string.Equals(a.Street, b.Street, StringComparison.Ordinal))
		{
			return false;
		}
		if (a.SuffixId is int sa && b.SuffixId is int sb && sa != sb)
		{
			return false;
		}
		if (a.Zip != null && b.Zip != null && a.Zip != b.Zip)
		{
			return false;
		}
		return !MatchUnit || a.Unit == b.Unit;
	}

	/// <summary>An index of <paramref name="items"/> by address, for finding every item at an address quickly.</summary>
	public AddressIndex<T> Index<T>(IEnumerable<T> items, Func<T, AddressKey> keyOf) => new AddressIndex<T>(this, items, keyOf);

	/// <summary>Matches digits with an optional single trailing ASCII letter ("123", "123a").</summary>
	private static bool IsHouseNumber(string token)
	{
		if (string.IsNullOrEmpty(token))
		{
			return false;
		}
		int end = token.Length;
		char last = token[end - 1];
		if (last is >= 'a' and <= 'z')
		{
			end--;
			if (end == 0)
			{
				return false;
			}
		}
		for (int i = 0; i < end; i++)
		{
			if (!char.IsDigit(token[i]))
			{
				return false;
			}
		}
		return true;
	}

	private static string FirstFiveDigits(string zip)
	{
		if (string.IsNullOrEmpty(zip))
		{
			return null;
		}
		Span<char> buf = stackalloc char[5];
		int count = 0;
		for (int i = 0; i < zip.Length; i++)
		{
			if (char.IsDigit(zip[i]))
			{
				buf[count++] = zip[i];
				if (count == 5)
				{
					return new string(buf);
				}
			}
		}
		return null;
	}

	/// <summary>Lower-case words of letters and digits, with "#" read as a unit ("N. Main St., #4" → n, main, st, unit, 4).</summary>
	private static List<string> Tokens(string text)
	{
		List<string> tokens = new List<string>();
		if (string.IsNullOrEmpty(text))
		{
			return tokens;
		}
		Span<char> lowerBuf = stackalloc char[64];
		int i = 0;
		while (i < text.Length)
		{
			char c = text[i];
			if (c == '#')
			{
				tokens.Add("unit");
				i++;
				continue;
			}
			if (!char.IsLetterOrDigit(c))
			{
				i++;
				continue;
			}
			int start = i;
			bool needsLower = false;
			while (i < text.Length && char.IsLetterOrDigit(text[i]))
			{
				if (char.IsUpper(text[i]))
				{
					needsLower = true;
				}
				i++;
			}
			int len = i - start;
			if (!needsLower)
			{
				tokens.Add(text.Substring(start, len));
			}
			else if (len <= lowerBuf.Length)
			{
				for (int j = 0; j < len; j++)
				{
					lowerBuf[j] = char.ToLowerInvariant(text[start + j]);
				}
				tokens.Add(new string(lowerBuf.Slice(0, len)));
			}
			else
			{
				tokens.Add(text.Substring(start, len).ToLowerInvariant());
			}
		}
		return tokens;
	}

	private void Add(string text, int suffixId)
	{
		string key = string.Concat(Tokens(text));
		if (key.Length > 0)
		{
			_suffixes.TryAdd(key, suffixId);
		}
	}
}

/// <summary>Items (structures) by address; see <see cref="AddressMatcher.Index{T}"/>.</summary>
public sealed class AddressIndex<T>
{
	private readonly AddressMatcher _matcher;
	private readonly Dictionary<(string HouseNumber, string Street), List<(T Item, AddressKey Key)>> _byStreet = new();

	internal AddressIndex(AddressMatcher matcher, IEnumerable<T> items, Func<T, AddressKey> keyOf)
	{
		_matcher = matcher;
		foreach (T item in items)
		{
			AddressKey key = keyOf(item);
			if (key == null || !key.CanMatch)
			{
				continue;
			}
			(string, string) streetKey = (key.HouseNumber, key.Street);
			if (!_byStreet.TryGetValue(streetKey, out List<(T Item, AddressKey Key)> list))
			{
				list = new List<(T Item, AddressKey Key)>();
				_byStreet[streetKey] = list;
			}
			list.Add((item, key));
		}
	}

	/// <summary>Every item at the address (none, one, or several when the match is ambiguous).</summary>
	public IReadOnlyList<T> Find(AddressKey key)
	{
		if (key == null || !key.CanMatch || !_byStreet.TryGetValue((key.HouseNumber, key.Street), out List<(T Item, AddressKey Key)> bucket))
		{
			return Array.Empty<T>();
		}
		List<T> found = new List<T>(bucket.Count);
		for (int i = 0; i < bucket.Count; i++)
		{
			if (_matcher.Same(key, bucket[i].Key))
			{
				found.Add(bucket[i].Item);
			}
		}
		return found;
	}
}

/// <summary>Parcel numbers written in different ways: "0123-0045.67 00", "0123004567 00" and "0123004567-00" are one parcel.</summary>
public static class ParcelNumbers
{
	/// <summary>
	/// The parcel number without spaces, dashes or dots, in upper case; with <paramref name="ignoreLeadingZeros"/>,
	/// without leading zeros too ("00123" → "123"). Null when there is nothing left.
	/// </summary>
	public static string Normalize(string parcel, bool ignoreLeadingZeros = false)
	{
		if (string.IsNullOrEmpty(parcel))
		{
			return null;
		}
		Span<char> buffer = parcel.Length <= 128 ? stackalloc char[parcel.Length] : new char[parcel.Length];
		int length = 0;
		for (int i = 0; i < parcel.Length; i++)
		{
			char c = parcel[i];
			if (!char.IsWhiteSpace(c) && c is not '-' and not '.')
			{
				buffer[length++] = char.ToUpperInvariant(c);
			}
		}
		if (length == 0)
		{
			return null;
		}
		int start = 0;
		if (ignoreLeadingZeros)
		{
			while (start < length - 1 && buffer[start] == '0')
			{
				start++;
			}
		}
		return new string(buffer.Slice(start, length - start));
	}

	/// <summary>"007" → "7", "000" → "0".</summary>
	internal static string WithoutLeadingZeros(string s)
	{
		string trimmed = s.TrimStart('0');
		return trimmed.Length == 0 && s.Length > 0 ? "0" : trimmed;
	}
}
