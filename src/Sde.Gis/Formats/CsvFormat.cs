using System.Globalization;
using System.Text;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Sde.Gis.Features;
using Sde.Gis.Projections;

namespace Sde.Gis.Formats;

/// <summary>Which columns of a CSV hold the location, and in what coordinate system.</summary>
public sealed class CsvOptions
{
	/// <summary>The latitude (or northing) column; found by name when null.</summary>
	public string LatitudeColumn { get; init; }

	/// <summary>The longitude (or easting) column; found by name when null.</summary>
	public string LongitudeColumn { get; init; }

	/// <summary>A column of WKT geometries, used instead of latitude and longitude; found by name when null.</summary>
	public string WktColumn { get; init; }

	/// <summary>The system the coordinates are in; WGS 84 longitude and latitude when null. Use a State Plane system for X and Y columns in feet.</summary>
	public CoordinateSystem CoordinateSystem { get; init; }

	/// <summary>The separator; worked out from the header line (comma, semicolon, tab or pipe) when null.</summary>
	public char? Delimiter { get; init; }
}

/// <summary>The location columns found in a CSV header.</summary>
public sealed record CsvLocationColumns(string Latitude, string Longitude, string Wkt);

/// <summary>
/// Reads a CSV of points (latitude and longitude columns, found by their usual names) or of any geometry (a WKT
/// column). Column types are worked out from the values; codes with leading zeros, such as parcel numbers, stay text.
/// Rows without a usable location are left out and counted in <see cref="FeatureLayer.Warnings"/>.
/// </summary>
public static class CsvFormat
{
	private static readonly string[] LatitudeNames = { "latitude", "lat", "y", "pointy", "latdd", "latitudedd", "ycoord", "ycoordinate", "latwgs84", "latitudewgs84", "gpslat", "gpslatitude", "northing" };
	private static readonly string[] LongitudeNames = { "longitude", "lon", "long", "lng", "x", "pointx", "londd", "longdd", "longitudedd", "xcoord", "xcoordinate", "lonwgs84", "longitudewgs84", "gpslon", "gpslong", "gpslongitude", "easting" };
	private static readonly string[] WktNames = { "wkt", "geometry", "geom", "thegeom", "shape", "wktgeometry", "geometrywkt" };
	private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
	private static readonly NetTopologySuite.NtsGeometryServices WktServices =
		new NetTopologySuite.NtsGeometryServices(GisSetup.Factory.CoordinateSequenceFactory, GisSetup.Factory.PrecisionModel, 4326);

	/// <summary>Reads a CSV file. Throws <see cref="GisException"/> when no location columns can be found.</summary>
	public static FeatureLayer Read(string path, CsvOptions options = null)
	{
		if (!File.Exists(path))
		{
			throw new GisException("The CSV file couldn't be found.", path);
		}
		return ReadString(DecodeText(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path), options);
	}

	/// <summary>Reads CSV text.</summary>
	public static FeatureLayer ReadString(string text, string name = "layer", CsvOptions options = null)
	{
		options ??= new CsvOptions();
		char delimiter = options.Delimiter ?? DetectDelimiter(text);
		List<string[]> rows = Parse(text, delimiter);
		if (rows.Count == 0)
		{
			throw new GisException("This CSV file is empty.", name);
		}
		string[] header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToArray();
		CsvLocationColumns found = DetectColumns(header);
		string latName = options.LatitudeColumn ?? found.Latitude;
		string lonName = options.LongitudeColumn ?? found.Longitude;
		string wktName = options.WktColumn ?? (options.LatitudeColumn == null && options.LongitudeColumn == null ? found.Wkt : null);
		int lat = IndexOf(header, latName);
		int lon = IndexOf(header, lonName);
		int wkt = IndexOf(header, wktName);
		if (wkt < 0 && (lat < 0 || lon < 0))
		{
			throw new GisException("SDE couldn't find the location columns in this CSV. Name them Latitude and Longitude (or give a WKT column), or choose them.",
				$"Columns: {string.Join(", ", header)}");
		}
		bool useWkt = wkt >= 0;
		CoordinateSystem cs = options.CoordinateSystem ?? CoordinateSystem.Wgs84;

		FeatureLayer layer = new FeatureLayer(name);
		layer.SourceCoordinateSystem = cs.IsGeographic ? null : cs;
		List<int> attributeColumns = new List<int>(header.Length);
		for (int i = 0; i < header.Length; i++)
		{
			if (i == wkt || header[i].Length == 0 || layer.Fields.Contains(header[i]))
			{
				continue;
			}
			attributeColumns.Add(i);
			layer.AddField(header[i], InferType(rows, i));
		}

		WKTReader wktReader = useWkt ? new WKTReader(WktServices) : null;
		int skipped = 0;
		long rowNumber = 0;
		for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
		{
			string[] row = rows[rowIndex];
			rowNumber++;
			bool blank = true;
			for (int c = 0; c < row.Length; c++)
			{
				if (!string.IsNullOrWhiteSpace(row[c]))
				{
					blank = false;
					break;
				}
			}
			if (blank)
			{
				continue;
			}
			Geometry g = useWkt ? ReadWkt(wktReader, Cell(row, wkt), cs) : ReadPoint(Cell(row, lat), Cell(row, lon), cs);
			if (g == null)
			{
				skipped++;
				continue;
			}
			Feature feature = layer.NewFeature(g, rowNumber);
			for (int k = 0; k < attributeColumns.Count; k++)
			{
				FieldValues.TryConvert(layer.Fields[k].Type, Cell(row, attributeColumns[k]), out object value);
				feature.SetRaw(k, value);
			}
			layer.Features.Add(feature);
		}
		if (skipped > 0)
		{
			layer.Warnings.Add(skipped == 1
				? "1 row was left out because it had no usable location."
				: $"{skipped} rows were left out because they had no usable location.");
		}
		layer.DetectGeometryKind();
		return layer;
	}

	/// <summary>The latitude, longitude and WKT columns a header names (any may be null).</summary>
	public static CsvLocationColumns DetectColumns(IReadOnlyList<string> header)
	{
		string Find(string[] names)
		{
			foreach (string n in names)
			{
				string match = header.FirstOrDefault(h => Key(h) == n);
				if (match != null)
				{
					return match;
				}
			}
			return null;
		}
		return new CsvLocationColumns(Find(LatitudeNames), Find(LongitudeNames), Find(WktNames));
	}

	private static Geometry ReadPoint(string latText, string lonText, CoordinateSystem cs)
	{
		if (!TryNumber(latText, out double y) || !TryNumber(lonText, out double x))
		{
			return null;
		}
		if (cs.IsGeographic)
		{
			if (Math.Abs(y) > 90 || Math.Abs(x) > 180 || (x == 0 && y == 0))
			{
				return null;
			}
			return GisSetup.Factory.CreatePoint(new Coordinate(x, y));
		}
		(double lonW, double latW) = cs.ToWgs84(x, y);
		return GisSetup.Factory.CreatePoint(new Coordinate(lonW, latW));
	}

	private static Geometry ReadWkt(WKTReader reader, string text, CoordinateSystem cs)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		try
		{
			Geometry g = reader.Read(text.Trim());
			return g == null || g.IsEmpty ? null : Reproject.ToWgs84InPlace(g, cs);
		}
		catch (Exception ex) when (ex is ParseException or ArgumentException or FormatException or InvalidOperationException)
		{
			return null;
		}
	}

	private static bool TryNumber(string text, out double value)
	{
		if (string.IsNullOrEmpty(text))
		{
			value = 0;
			return false;
		}
		return double.TryParse(text.AsSpan().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
	}

	private static FieldType InferType(List<string[]> rows, int columnIndex)
	{
		bool allInteger = true;
		bool any = false;
		for (int i = 1; i < rows.Count; i++)
		{
			string[] row = rows[i];
			string raw = columnIndex < row.Length ? row[columnIndex] : null;
			if (!CheckInferValue(raw, ref any, ref allInteger))
			{
				return FieldType.Text;
			}
		}
		if (!any)
		{
			return FieldType.Text;
		}
		return allInteger ? FieldType.Integer : FieldType.Real;
	}

	/// <summary>Integer when every value is a whole number without leading zeros, Real when every value is a number, otherwise Text.</summary>
	internal static FieldType InferType(IEnumerable<string> values)
	{
		bool allInteger = true;
		bool any = false;
		foreach (string raw in values)
		{
			if (!CheckInferValue(raw, ref any, ref allInteger))
			{
				return FieldType.Text;
			}
		}
		if (!any)
		{
			return FieldType.Text;
		}
		return allInteger ? FieldType.Integer : FieldType.Real;
	}

	private static bool CheckInferValue(string raw, ref bool any, ref bool allInteger)
	{
		if (string.IsNullOrEmpty(raw))
		{
			return true;
		}
		ReadOnlySpan<char> v = raw.AsSpan().Trim();
		if (v.Length == 0)
		{
			return true;
		}
		any = true;
		if (v.Length > 1 && v[0] == '0' && char.IsAsciiDigit(v[1]))
		{
			return false;
		}
		int digits = 0;
		for (int i = 0; i < v.Length; i++)
		{
			if (char.IsAsciiDigit(v[i]))
			{
				digits++;
				if (digits > 15)
				{
					return false;
				}
			}
		}
		if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
		{
			return false;
		}
		if (allInteger && !long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
		{
			allInteger = false;
		}
		return true;
	}

	/// <summary>UTF-8 (with or without a byte order mark), falling back to Windows-1252 when the bytes aren't valid UTF-8.</summary>
	internal static string DecodeText(byte[] bytes)
	{
		try
		{
			int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
			return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
		}
		catch (DecoderFallbackException)
		{
			GisSetup.EnsureInitialized();
			return Encoding.GetEncoding(1252).GetString(bytes);
		}
	}

	private static char DetectDelimiter(string text)
	{
		int end = text.IndexOf('\n');
		ReadOnlySpan<char> first = end < 0 ? text.AsSpan() : text.AsSpan(0, end);
		char best = ',';
		int bestCount = 0;
		ReadOnlySpan<char> candidates = stackalloc char[] { ',', ';', '\t', '|' };
		for (int j = 0; j < candidates.Length; j++)
		{
			char c = candidates[j];
			int count = 0;
			bool quoted = false;
			for (int i = 0; i < first.Length; i++)
			{
				char ch = first[i];
				if (ch == '"')
				{
					quoted = !quoted;
				}
				else if (ch == c && !quoted)
				{
					count++;
				}
			}
			if (count > bestCount)
			{
				best = c;
				bestCount = count;
			}
		}
		return best;
	}

	/// <summary>RFC 4180 parsing: quoted fields may hold the delimiter, doubled quotes and line breaks.</summary>
	internal static List<string[]> Parse(string text, char delimiter)
	{
		List<string[]> rows = new List<string[]>();
		List<string> row = new List<string>();
		StringBuilder cell = null;
		int len = text.Length;
		int i = 0;
		bool rowHasContent = false;
		while (i < len)
		{
			char c = text[i];
			if (c == '"')
			{
				rowHasContent = true;
				i++;
				int start = i;
				bool hasEscaped = false;
				while (i < len)
				{
					if (text[i] == '"')
					{
						if (i + 1 < len && text[i + 1] == '"')
						{
							hasEscaped = true;
							cell ??= new StringBuilder();
							cell.Append(text, start, i - start);
							cell.Append('"');
							i += 2;
							start = i;
							continue;
						}
						break;
					}
					i++;
				}
				string value;
				if (hasEscaped)
				{
					cell.Append(text, start, i - start);
					value = cell.ToString();
					cell.Clear();
				}
				else
				{
					value = i > start ? text.Substring(start, i - start) : "";
				}
				if (i < len && text[i] == '"')
				{
					i++;
				}
				// Handle any trailing characters before delimiter/newline (defensive RFC compatibility).
				if (i < len && text[i] != delimiter && text[i] != '\r' && text[i] != '\n')
				{
					cell ??= new StringBuilder();
					cell.Append(value);
					while (i < len && text[i] != delimiter && text[i] != '\r' && text[i] != '\n')
					{
						if (text[i] == '"')
						{
							i++;
							while (i < len)
							{
								if (text[i] == '"')
								{
									if (i + 1 < len && text[i + 1] == '"')
									{
										cell.Append('"');
										i += 2;
										continue;
									}
									i++;
									break;
								}
								cell.Append(text[i]);
								i++;
							}
						}
						else
						{
							cell.Append(text[i]);
							i++;
						}
					}
					value = cell.ToString();
					cell.Clear();
				}
				row.Add(value);
				if (i < len && text[i] == delimiter)
				{
					i++;
					if (i == len || text[i] == '\r' || text[i] == '\n')
					{
						row.Add("");
					}
				}
				else if (i < len && (text[i] == '\r' || text[i] == '\n'))
				{
					if (text[i] == '\r' && i + 1 < len && text[i + 1] == '\n')
					{
						i += 2;
					}
					else
					{
						i++;
					}
					rows.Add(row.ToArray());
					row.Clear();
					rowHasContent = false;
				}
			}
			else if (c == '\r' || c == '\n')
			{
				if (c == '\r' && i + 1 < len && text[i + 1] == '\n')
				{
					i += 2;
				}
				else
				{
					i++;
				}
				if (rowHasContent)
				{
					rows.Add(row.ToArray());
					row.Clear();
					rowHasContent = false;
				}
			}
			else if (c == delimiter)
			{
				rowHasContent = true;
				row.Add("");
				i++;
				if (i == len || text[i] == '\r' || text[i] == '\n')
				{
					row.Add("");
				}
			}
			else
			{
				rowHasContent = true;
				int start = i;
				while (i < len && text[i] != delimiter && text[i] != '\r' && text[i] != '\n' && text[i] != '"')
				{
					i++;
				}
				string value;
				if (i < len && text[i] == '"')
				{
					cell ??= new StringBuilder();
					cell.Append(text, start, i - start);
					while (i < len && text[i] != delimiter && text[i] != '\r' && text[i] != '\n')
					{
						if (text[i] == '"')
						{
							i++;
							while (i < len)
							{
								if (text[i] == '"')
								{
									if (i + 1 < len && text[i + 1] == '"')
									{
										cell.Append('"');
										i += 2;
										continue;
									}
									i++;
									break;
								}
								cell.Append(text[i]);
								i++;
							}
						}
						else
						{
							cell.Append(text[i]);
							i++;
						}
					}
					value = cell.ToString();
					cell.Clear();
				}
				else
				{
					value = text.Substring(start, i - start);
				}
				row.Add(value);
				if (i < len && text[i] == delimiter)
				{
					i++;
					if (i == len || text[i] == '\r' || text[i] == '\n')
					{
						row.Add("");
					}
				}
				else if (i < len && (text[i] == '\r' || text[i] == '\n'))
				{
					if (text[i] == '\r' && i + 1 < len && text[i + 1] == '\n')
					{
						i += 2;
					}
					else
					{
						i++;
					}
					rows.Add(row.ToArray());
					row.Clear();
					rowHasContent = false;
				}
			}
		}
		if (rowHasContent && row.Count > 0)
		{
			rows.Add(row.ToArray());
		}
		return rows;
	}

	private static string Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : null;

	private static int IndexOf(string[] header, string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return -1;
		}
		for (int i = 0; i < header.Length; i++)
		{
			if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		return -1;
	}

	private static string Key(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		Span<char> buf = s.Length <= 64 ? stackalloc char[s.Length] : new char[s.Length];
		int n = 0;
		for (int i = 0; i < s.Length; i++)
		{
			char c = s[i];
			if (char.IsAsciiLetterOrDigit(c))
			{
				buf[n++] = char.ToLowerInvariant(c);
			}
		}
		return new string(buf.Slice(0, n));
	}
}
