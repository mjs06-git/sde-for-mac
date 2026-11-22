namespace Sde.Gis.Projections;

/// <summary>
/// Transverse Mercator (EPSG method 9807) with the JHS formulas of EPSG Guidance Note 7-2 section 3.2.3.1, which EPSG
/// recommends and which stay accurate to about ±40° of the central meridian. Used by UTM and the State Plane zones
/// that run north to south (Indiana, Illinois, New Jersey and others).
/// </summary>
public sealed class TransverseMercator : MapProjection
{
	private readonly double _lon0;
	private readonly double _k0;
	private readonly double _fe;
	private readonly double _fn;
	private readonly double _e;
	private readonly double _b;
	private readonly double _mo;
	private readonly double _k0B;
	private readonly double _invK0B;
	private readonly double _k0Mo;
	private readonly double _fnMinusK0Mo;
	private readonly double _h1, _h2, _h3, _h4;
	private readonly double _r1, _r2, _r3, _r4;

	/// <param name="ellipsoid">The ellipsoid.</param>
	/// <param name="latitudeOfOrigin">Latitude of natural origin, degrees.</param>
	/// <param name="centralMeridian">Longitude of natural origin, degrees.</param>
	/// <param name="scaleFactor">Scale factor at the natural origin (k0).</param>
	/// <param name="falseEasting">False easting, metres.</param>
	/// <param name="falseNorthing">False northing, metres.</param>
	public TransverseMercator(Ellipsoid ellipsoid, double latitudeOfOrigin, double centralMeridian, double scaleFactor, double falseEasting, double falseNorthing)
		: base(ellipsoid)
	{
		LatitudeOfOrigin = latitudeOfOrigin;
		CentralMeridian = centralMeridian;
		ScaleFactor = scaleFactor;
		FalseEasting = falseEasting;
		FalseNorthing = falseNorthing;
		_lon0 = centralMeridian * DegToRad;
		_k0 = scaleFactor;
		_fe = falseEasting;
		_fn = falseNorthing;
		_e = ellipsoid.Eccentricity;
		double f = ellipsoid.Flattening;
		double n = f / (2.0 - f);
		double n2 = n * n, n3 = n2 * n, n4 = n3 * n;
		_b = ellipsoid.SemiMajorAxis / (1.0 + n) * (1.0 + n2 / 4.0 + n4 / 64.0);
		_h1 = n / 2.0 - 2.0 / 3.0 * n2 + 5.0 / 16.0 * n3 + 41.0 / 180.0 * n4;
		_h2 = 13.0 / 48.0 * n2 - 3.0 / 5.0 * n3 + 557.0 / 1440.0 * n4;
		_h3 = 61.0 / 240.0 * n3 - 103.0 / 140.0 * n4;
		_h4 = 49561.0 / 161280.0 * n4;
		_r1 = n / 2.0 - 2.0 / 3.0 * n2 + 37.0 / 96.0 * n3 - 1.0 / 360.0 * n4;
		_r2 = 1.0 / 48.0 * n2 + 1.0 / 15.0 * n3 - 437.0 / 1440.0 * n4;
		_r3 = 17.0 / 480.0 * n3 - 37.0 / 840.0 * n4;
		_r4 = 4397.0 / 161280.0 * n4;

		double lat0 = latitudeOfOrigin * DegToRad;
		if (lat0 == 0.0)
		{
			_mo = 0.0;
		}
		else if (Math.Abs(latitudeOfOrigin - 90.0) < 1e-12)
		{
			_mo = _b * (Math.PI / 2.0);
		}
		else if (Math.Abs(latitudeOfOrigin + 90.0) < 1e-12)
		{
			_mo = _b * (-Math.PI / 2.0);
		}
		else
		{
			double qo = Math.Asinh(Math.Tan(lat0)) - _e * Math.Atanh(_e * Math.Sin(lat0));
			double xi = Math.Atan(Math.Sinh(qo));
			_mo = _b * (xi + _h1 * Math.Sin(2.0 * xi) + _h2 * Math.Sin(4.0 * xi) + _h3 * Math.Sin(6.0 * xi) + _h4 * Math.Sin(8.0 * xi));
		}
		_k0B = _k0 * _b;
		_invK0B = 1.0 / _k0B;
		_k0Mo = _k0 * _mo;
		_fnMinusK0Mo = _fn - _k0Mo;
	}

	public override string MethodName => "Transverse Mercator";

	public double LatitudeOfOrigin { get; }
	public double CentralMeridian { get; }
	public double ScaleFactor { get; }
	public double FalseEasting { get; }
	public double FalseNorthing { get; }

	public override (double X, double Y) Forward(double longitude, double latitude)
	{
		double phi = latitude * DegToRad;
		double dLambda = WrapRadians(longitude * DegToRad - _lon0);
		(double sinPhi, double cosPhi) = Math.SinCos(phi);
		double q = Math.Asinh(sinPhi / cosPhi) - _e * Math.Atanh(_e * sinPhi);
		double sinhQ = Math.Sinh(q);
		double invCoshQ = 1.0 / Math.Sqrt(1.0 + sinhQ * sinhQ);
		double cosBeta = invCoshQ;
		double sinBeta = sinhQ * invCoshQ;
		double eta0 = Math.Atanh(cosBeta * Math.Sin(dLambda));
		double xi0 = Math.Asin(Math.Clamp(sinBeta * Math.Cosh(eta0), -1.0, 1.0));
		(double sin2, double cos2) = Math.SinCos(2.0 * xi0);
		double sin4 = 2.0 * sin2 * cos2;
		double cos4 = 2.0 * cos2 * cos2 - 1.0;
		double sin6 = sin4 * cos2 + cos4 * sin2;
		double cos6 = cos4 * cos2 - sin4 * sin2;
		double sin8 = 2.0 * sin4 * cos4;
		double cos8 = 2.0 * cos4 * cos4 - 1.0;
		double exp2 = Math.Exp(2.0 * eta0);
		double inv2 = 1.0 / exp2;
		double sinh2 = 0.5 * (exp2 - inv2);
		double cosh2 = 0.5 * (exp2 + inv2);
		double sinh4 = 2.0 * sinh2 * cosh2;
		double cosh4 = 2.0 * cosh2 * cosh2 - 1.0;
		double sinh6 = sinh4 * cosh2 + cosh4 * sinh2;
		double cosh6 = cosh4 * cosh2 + sinh4 * sinh2;
		double sinh8 = 2.0 * sinh4 * cosh4;
		double cosh8 = 2.0 * cosh4 * cosh4 - 1.0;
		double xi = xi0
			+ _h1 * sin2 * cosh2
			+ _h2 * sin4 * cosh4
			+ _h3 * sin6 * cosh6
			+ _h4 * sin8 * cosh8;
		double eta = eta0
			+ _h1 * cos2 * sinh2
			+ _h2 * cos4 * sinh4
			+ _h3 * cos6 * sinh6
			+ _h4 * cos8 * sinh8;
		return (_fe + _k0B * eta, _fnMinusK0Mo + _k0B * xi);
	}

	public override (double Longitude, double Latitude) Inverse(double x, double y)
	{
		double etaP = (x - _fe) * _invK0B;
		double xiP = ((y - _fn) + _k0Mo) * _invK0B;
		(double sin2, double cos2) = Math.SinCos(2.0 * xiP);
		double sin4 = 2.0 * sin2 * cos2;
		double cos4 = 2.0 * cos2 * cos2 - 1.0;
		double sin6 = sin4 * cos2 + cos4 * sin2;
		double cos6 = cos4 * cos2 - sin4 * sin2;
		double sin8 = 2.0 * sin4 * cos4;
		double cos8 = 2.0 * cos4 * cos4 - 1.0;
		double exp2 = Math.Exp(2.0 * etaP);
		double inv2 = 1.0 / exp2;
		double sinh2 = 0.5 * (exp2 - inv2);
		double cosh2 = 0.5 * (exp2 + inv2);
		double sinh4 = 2.0 * sinh2 * cosh2;
		double cosh4 = 2.0 * cosh2 * cosh2 - 1.0;
		double sinh6 = sinh4 * cosh2 + cosh4 * sinh2;
		double cosh6 = cosh4 * cosh2 + sinh4 * sinh2;
		double sinh8 = 2.0 * sinh4 * cosh4;
		double cosh8 = 2.0 * cosh4 * cosh4 - 1.0;
		double xi0 = xiP
			- _r1 * sin2 * cosh2
			- _r2 * sin4 * cosh4
			- _r3 * sin6 * cosh6
			- _r4 * sin8 * cosh8;
		double eta0 = etaP
			- _r1 * cos2 * sinh2
			- _r2 * cos4 * sinh4
			- _r3 * cos6 * sinh6
			- _r4 * cos8 * sinh8;
		(double sinXi0, double cosXi0) = Math.SinCos(xi0);
		double expE = Math.Exp(eta0);
		double invE = 1.0 / expE;
		double sinhEta0 = 0.5 * (expE - invE);
		double coshEta0 = 0.5 * (expE + invE);
		double qp = Math.Atanh(Math.Clamp(sinXi0 / coshEta0, -1.0, 1.0));
		double qpp = qp + _e * Math.Atanh(_e * Math.Tanh(qp));
		for (int i = 0; i < 15; i++)
		{
			double next = qp + _e * Math.Atanh(_e * Math.Tanh(qpp));
			bool done = Math.Abs(next - qpp) < 1e-14;
			qpp = next;
			if (done)
			{
				break;
			}
		}
		double phi = Math.Atan(Math.Sinh(qpp));
		// The same as GN 7-2's asin(tanh(η0') / cos β'), written with atan2 so it can't leave asin's domain.
		double lambda = _lon0 + Math.Atan2(sinhEta0, cosXi0);
		return (WrapDegrees(lambda * RadToDeg), phi * RadToDeg);
	}
}
