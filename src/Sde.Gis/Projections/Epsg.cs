using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace Sde.Gis.Projections;

/// <summary>
/// The EPSG codes SDE knows: geographic NAD83 and WGS 84, WGS 84 / Pseudo-Mercator, and (from EpsgUs.csv, made by
/// tools/gis/make_epsg_table.py from the EPSG Dataset) every State Plane (SPCS83) zone on NAD83, NAD83(HARN),
/// NAD83(NSRS2007) and NAD83(2011) in metres and feet, the UTM zones on those datums and WGS 84, and CONUS Albers.
/// GeoTIFFs name their coordinate system this way.
/// </summary>
public static class Epsg
{
	private readonly record struct EpsgEntry(string Name, string Datum, ProjectionMethod Method, double Lat0, double Lon0, double Sp1, double Sp2, double K0, double Fe, double Fn, LinearUnit Unit);

	private static readonly Lazy<Dictionary<int, EpsgEntry>> _entries = new Lazy<Dictionary<int, EpsgEntry>>(Load);
	private static readonly ConcurrentDictionary<int, CoordinateSystem> _cache = new ConcurrentDictionary<int, CoordinateSystem>();

	/// <summary>The geographic systems by code.</summary>
	private static readonly Dictionary<int, CoordinateSystem> Geographic = new Dictionary<int, CoordinateSystem>
	{
		[4326] = CoordinateSystem.Wgs84,
		[4269] = CoordinateSystem.Nad83,
		[4152] = CoordinateSystem.Geographic("NAD83(HARN)", "NAD83(HARN)", Ellipsoid.Grs80, 4152),
		[4759] = CoordinateSystem.Geographic("NAD83(NSRS2007)", "NAD83(NSRS2007)", Ellipsoid.Grs80, 4759),
		[6318] = CoordinateSystem.Geographic("NAD83(2011)", "NAD83(2011)", Ellipsoid.Grs80, 6318),
		[4617] = CoordinateSystem.Geographic("NAD83(CSRS)", "NAD83(CSRS)", Ellipsoid.Grs80, 4617),
	};

	/// <summary>Every projected code in the table (for listing and tests).</summary>
	public static IReadOnlyCollection<int> ProjectedCodes => _entries.Value.Keys;

	/// <summary>The system for an EPSG code; throws <see cref="UnsupportedCoordinateSystemException"/> when SDE doesn't know it.</summary>
	public static CoordinateSystem Get(int code)
	{
		if (TryGet(code, out CoordinateSystem cs))
		{
			return cs;
		}
		if (IsNad27(code))
		{
			throw new UnsupportedCoordinateSystemException("NAD27", $"EPSG:{code} is on NAD27.");
		}
		throw new UnsupportedCoordinateSystemException($"EPSG:{code}", $"EPSG:{code} isn't one of the State Plane, UTM, Albers, Web Mercator or geographic NAD83 and WGS 84 systems SDE knows.");
	}

	/// <summary>The system for an EPSG code, or false when SDE doesn't know it.</summary>
	public static bool TryGet(int code, out CoordinateSystem cs)
	{
		if (code == 4326)
		{
			cs = CoordinateSystem.Wgs84;
			return true;
		}
		if (code == 4269)
		{
			cs = CoordinateSystem.Nad83;
			return true;
		}
		if (Geographic.TryGetValue(code, out cs))
		{
			return true;
		}
		if (code is 3857 or 900913 or 102100 or 102113 or 3785)
		{
			cs = CoordinateSystem.WebMercator;
			return true;
		}
		if (_cache.TryGetValue(code, out cs))
		{
			return true;
		}
		if (!_entries.Value.TryGetValue(code, out EpsgEntry entry))
		{
			cs = null;
			return false;
		}
		cs = _cache.GetOrAdd(code, static (c, e) => Build(c, e), entry);
		return true;
	}

	/// <summary>True for the NAD27 geographic, UTM and State Plane codes (4267, 267xx, 320xx).</summary>
	public static bool IsNad27(int code) => code == 4267 || (code >= 26701 && code <= 26799) || (code >= 32001 && code <= 32099);

	private static CoordinateSystem Build(int code, EpsgEntry e)
	{
		ProjectionDefinition definition = new ProjectionDefinition
		{
			Method = e.Method,
			LatitudeOfOrigin = e.Lat0,
			CentralMeridian = e.Lon0,
			StandardParallel1 = e.Sp1,
			StandardParallel2 = e.Sp2,
			ScaleFactor = e.K0,
			FalseEasting = e.Fe,
			FalseNorthing = e.Fn,
		};
		Ellipsoid ellipsoid = e.Datum == "WGS 84" ? Ellipsoid.Wgs84 : Ellipsoid.Grs80;
		return CoordinateSystem.Projected(e.Name, e.Datum, ellipsoid, definition, e.Unit, code);
	}

	private static Dictionary<int, EpsgEntry> Load()
	{
		Dictionary<int, EpsgEntry> table = new Dictionary<int, EpsgEntry>(1024);
		using Stream stream = typeof(Epsg).Assembly.GetManifestResourceStream("Sde.Gis.Projections.EpsgUs.csv")
			?? throw new InvalidOperationException("The EPSG table resource is missing.");
		using StreamReader reader = new StreamReader(stream);
		reader.ReadLine();
		string line;
		while ((line = reader.ReadLine()) != null)
		{
			if (line.Length == 0)
			{
				continue;
			}
			// code,"name",datum,method,lat0,lon0,sp1,sp2,k0,fe_m,fn_m,unit (the name is quoted and has no quotes inside)
			int q1 = line.IndexOf('"');
			int q2 = line.IndexOf('"', q1 + 1);
			int code = int.Parse(line.AsSpan(0, q1 - 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
			string name = line.Substring(q1 + 1, q2 - q1 - 1);
			ReadOnlySpan<char> rest = line.AsSpan(q2 + 2);
			ReadOnlySpan<char> datumSpan = NextField(ref rest);
			string datum = InternDatum(datumSpan);
			ReadOnlySpan<char> methodSpan = NextField(ref rest);
			ProjectionMethod method = methodSpan switch
			{
				"TM" => ProjectionMethod.TransverseMercator,
				"LCC2" => ProjectionMethod.LambertConformalConic2SP,
				"LCC1" => ProjectionMethod.LambertConformalConic1SP,
				"AEA" => ProjectionMethod.AlbersEqualArea,
				_ => ProjectionMethod.WebMercator,
			};
			double lat0 = ParseDouble(NextField(ref rest));
			double lon0 = ParseDouble(NextField(ref rest));
			double sp1 = ParseDouble(NextField(ref rest));
			double sp2 = ParseDouble(NextField(ref rest));
			double k0 = ParseDouble(NextField(ref rest));
			double fe = ParseDouble(NextField(ref rest));
			double fn = ParseDouble(NextField(ref rest));
			ReadOnlySpan<char> unitSpan = NextField(ref rest);
			LinearUnit unit = unitSpan switch
			{
				"ftUS" => LinearUnit.UsSurveyFoot,
				"ft" => LinearUnit.InternationalFoot,
				_ => LinearUnit.Metre,
			};
			table[code] = new EpsgEntry(name, datum, method, lat0, lon0, sp1, sp2, k0, fe, fn, unit);
		}
		return table;
	}

	private static ReadOnlySpan<char> NextField(ref ReadOnlySpan<char> span)
	{
		int comma = span.IndexOf(',');
		if (comma < 0)
		{
			ReadOnlySpan<char> tail = span;
			span = ReadOnlySpan<char>.Empty;
			return tail;
		}
		ReadOnlySpan<char> field = span.Slice(0, comma);
		span = span.Slice(comma + 1);
		return field;
	}

	private static double ParseDouble(ReadOnlySpan<char> span) =>
		span.Length == 0 ? 0.0 : double.Parse(span, NumberStyles.Float, CultureInfo.InvariantCulture);

	private static string InternDatum(ReadOnlySpan<char> span) => span switch
	{
		"NAD83" => "NAD83",
		"NAD83(HARN)" => "NAD83(HARN)",
		"NAD83(NSRS2007)" => "NAD83(NSRS2007)",
		"NAD83(2011)" => "NAD83(2011)",
		"WGS 84" => "WGS 84",
		_ => span.ToString(),
	};
}
