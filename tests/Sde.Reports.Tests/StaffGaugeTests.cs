using Sde.Reports.Internal;

namespace Sde.Reports.Tests;

/// <summary>The staff gauge drawn in the result panels: its colours, and that it adds no text or pictures to a report.</summary>
public class StaffGaugeTests : IDisposable
{
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	[Theory]
	[InlineData(0, "water")]
	[InlineData(39.9, "water")]
	[InlineData(40, "close")]
	[InlineData(49.9, "close")]
	[InlineData(50, "red")]
	[InlineData(130, "red")]
	public void Water_IsBlue_AmberNearTheLine_AndRedAtOrAboveIt(double percent, string expected)
	{
		MigraDoc.DocumentObjectModel.Color c = StaffGauge.WaterColor((decimal)percent, 50m);
		MigraDoc.DocumentObjectModel.Color want = expected switch
		{
			"water" => Theme.GaugeWater,
			"close" => Theme.GaugeClose,
			_ => Theme.GaugeRed,
		};
		Assert.Equal(want.Argb, c.Argb);
	}

	[Fact]
	public void Drawing_IsVector_WithItsLabelsAsOutlines()
	{
		string path = Path.Combine(_db.Folder, "gauge.pdf");
		File.WriteAllBytes(path, StaffGauge.Pdf(75m, 50m, 138));
		Assert.Equal(1, Poppler.Pages(path));
		Assert.Empty(Poppler.Images(path));
		Assert.Equal("", Poppler.Text(path).Trim());
	}
}
