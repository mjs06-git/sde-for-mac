namespace Sde.Gis.Projections;

/// <summary>
/// Albers Equal Area (EPSG method 9822), EPSG Guidance Note 7-2 section 3.1.3: used by the CONUS Albers systems (EPSG
/// 5070 and 6350), and by <see cref="Analysis.Measure"/> to measure areas on the ellipsoid.
/// </summary>
public sealed class AlbersEqualArea : MapProjection
{
	private readonly double _lon0;
	private readonly double _a;
	private readonly double _e;
	private readonly double _e2;
	private readonly double _n;
	private readonly double _c;
	private readonly double _rho0;
	private readonly double _fe;
	private readonly double _fn;
	private readonly double _qPole;
	private readonly double _aOverN;
	private readonly double _invN;
	private readonly double _nOverASq;
	private readonly double _invQPole;
	private readonly double _oneMinusE2;
	private readonly double _invOneMinusE2;
	private readonly double _inv2E;
	private readonly double _fnPlusRho0;
	private readonly double _sign;
	private readonly double _b2;
	private readonly double _b4;
	private readonly double _b6;

	/// <param name="ellipsoid">The ellipsoid.</param>
	/// <param name="latitudeOfOrigin">φO, the latitude of the false origin, degrees.</param>
	/// <param name="longitudeOfOrigin">λO, degrees.</param>
	/// <param name="standardParallel1">φ1, degrees.</param>
	/// <param name="standardParallel2">φ2, degrees.</param>
	/// <param name="falseEasting">EF, metres.</param>
	/// <param name="falseNorthing">NF, metres.</param>
	public AlbersEqualArea(Ellipsoid ellipsoid, double latitudeOfOrigin, double longitudeOfOrigin, double standardParallel1, double standardParallel2,
		double falseEasting, double falseNorthing)
		: base(ellipsoid)
	{
		_lon0 = longitudeOfOrigin * DegToRad;
		_a = ellipsoid.SemiMajorAxis;
		_e = ellipsoid.Eccentricity;
		_e2 = ellipsoid.EccentricitySquared;
		_oneMinusE2 = 1.0 - _e2;
		_invOneMinusE2 = 1.0 / _oneMinusE2;
		_inv2E = _e < 1e-12 ? 0.0 : 1.0 / (2.0 * _e);
		_fe = falseEasting;
		_fn = falseNorthing;
		double phi1 = standardParallel1 * DegToRad;
		double phi2 = standardParallel2 * DegToRad;
		double m1 = M(phi1);
		double a1 = Alpha(phi1);
		if (Math.Abs(standardParallel1 - standardParallel2) < 1e-10)
		{
			_n = Math.Sin(phi1);
		}
		else
		{
			double m2 = M(phi2);
			double a2 = Alpha(phi2);
			_n = (m1 * m1 - m2 * m2) / (a2 - a1);
		}
		_c = m1 * m1 + _n * a1;
		_aOverN = _a / _n;
		_invN = 1.0 / _n;
		_nOverASq = _n / (_a * _a);
		_rho0 = _aOverN * Math.Sqrt(_c - _n * Alpha(latitudeOfOrigin * DegToRad));
		_fnPlusRho0 = _fn + _rho0;
		_sign = _n < 0 ? -1.0 : 1.0;
		_qPole = _e < 1e-12 ? 2.0 : 1.0 - _oneMinusE2 * _inv2E * Math.Log((1.0 - _e) / (1.0 + _e));
		_invQPole = 1.0 / _qPole;
		double e4 = _e2 * _e2;
		double e6 = e4 * _e2;
		_b2 = _e2 / 3.0 + 31.0 * e4 / 180.0 + 517.0 * e6 / 5040.0;
		_b4 = 23.0 * e4 / 360.0 + 251.0 * e6 / 3780.0;
		_b6 = 761.0 * e6 / 45360.0;
	}

	public override string MethodName => "Albers Equal Area";

	public override (double X, double Y) Forward(double longitude, double latitude)
	{
		double theta = _n * WrapRadians(longitude * DegToRad - _lon0);
		double rho = _aOverN * Math.Sqrt(Math.Max(0.0, _c - _n * Alpha(latitude * DegToRad)));
		(double sinTheta, double cosTheta) = Math.SinCos(theta);
		return (_fe + rho * sinTheta, _fnPlusRho0 - rho * cosTheta);
	}

	public override (double Longitude, double Latitude) Inverse(double x, double y)
	{
		double dx = x - _fe;
		double dy = _fnPlusRho0 - y;
		double rho2 = dx * dx + dy * dy;
		double theta = Math.Atan2(_sign * dx, _sign * dy);
		double alpha = _c * _invN - rho2 * _nOverASq;
		double beta = Math.Asin(Math.Clamp(alpha * _invQPole, -1.0, 1.0));
		(double sin2, double cos2) = Math.SinCos(2.0 * beta);
		double sin4 = 2.0 * sin2 * cos2;
		double cos4 = 2.0 * cos2 * cos2 - 1.0;
		double sin6 = sin4 * cos2 + cos4 * sin2;
		double phi = beta + _b2 * sin2 + _b4 * sin4 + _b6 * sin6;
		// GN 7-2's series leaves about a millimetre; two Newton steps on α (Snyder eq. 3-16) take it to the micrometre.
		if (_e > 1e-12)
		{
			double alphaScaled = alpha * _invOneMinusE2;
			for (int i = 0; i < 2; i++)
			{
				(double sin, double cos) = Math.SinCos(phi);
				double es = _e * sin;
				double w = 1.0 - es * es;
				phi += w * w / (2.0 * cos) * (alphaScaled - sin / w + _inv2E * Math.Log((1.0 - es) / (1.0 + es)));
			}
		}
		double lambda = _lon0 + theta * _invN;
		return (WrapDegrees(lambda * RadToDeg), phi * RadToDeg);
	}

	private double M(double phi)
	{
		double s = _e * Math.Sin(phi);
		return Math.Cos(phi) / Math.Sqrt(1.0 - s * s);
	}

	/// <summary>α (Snyder's q) for a latitude in radians.</summary>
	private double Alpha(double phi)
	{
		double sin = Math.Sin(phi);
		if (_e < 1e-12)
		{
			return 2.0 * sin;
		}
		double es = _e * sin;
		return _oneMinusE2 * (sin / (1.0 - es * es) - _inv2E * Math.Log((1.0 - es) / (1.0 + es)));
	}
}
