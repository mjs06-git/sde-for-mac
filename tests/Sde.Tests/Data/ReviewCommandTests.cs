using Sde.Cli;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.TestSupport;

namespace Sde.Tests.Data;

/// <summary>The read-only sde commands: events, qa, compliance and report.</summary>
[Collection("CommandLine")]
public class ReviewCommandTests : IDisposable
{
	private static readonly DateTime Today = new DateTime(2026, 10, 1);

	private readonly TempDatabase _t = new TempDatabase();

	public ReviewCommandTests()
	{
		ReviewCommands.Today = () => Today;
	}

	public void Dispose()
	{
		ReviewCommands.Today = () => DateTime.Today;
		_t.Dispose();
	}

	private string Path_ => _t.Database.FilePath;

	private (int Code, string Output, string Errors) Run(params string[] args)
	{
		TextWriter oldOut = Console.Out;
		TextWriter oldError = Console.Error;
		StringWriter output = new StringWriter();
		StringWriter errors = new StringWriter();
		Console.SetOut(output);
		Console.SetError(errors);
		try
		{
			string[] full = new[] { "--db", Path_ }.Concat(args).ToArray();
			return (Program.Main(full), output.ToString(), errors.ToString());
		}
		finally
		{
			Console.SetOut(oldOut);
			Console.SetError(oldError);
		}
	}

	/// <summary>One substantially damaged structure in an event, with an open compliance case due on <paramref name="deadline"/>.</summary>
	private AssessmentFile Seed(DateTime? deadline, string eventName = "September flood")
	{
		AssessmentFile f = SampleCases.RanchFlood(_t.Store);
		EventRecord e = _t.Store.SaveEvent(new EventRecord { Name = eventName, Kind = "Flood", DRNumber = "DR-1234", StartDate = new DateTime(2026, 9, 13), EndDate = new DateTime(2026, 9, 16) });
		_t.Store.SetEvent(new[] { f.Assessment.AssessmentID }, e.EventID);
		ComplianceCaseRecord c = _t.Store.OpenComplianceCase(f.Assessment.AssessmentID);
		c.Deadline = deadline;
		_t.Store.SaveComplianceCase(c);
		return f;
	}

	[Theory]
	[InlineData("events")]
	[InlineData("qa")]
	[InlineData("compliance")]
	[InlineData("report")]
	public void Help_PrintsUsage_AndDoesNotNeedADatabase(string command)
	{
		(int code, string output, string _) = Run(command, "--help");
		Assert.Equal(0, code);
		Assert.Contains("Usage: sde", output);
		Assert.Contains(command, output);
	}

	[Fact]
	public void MainHelp_ListsTheNewCommands()
	{
		(int _, string output, string _) = Run("help");
		foreach (string command in new[] { "events", "qa", "compliance", "report" })
		{
			Assert.Contains("  " + command, output);
		}
	}

	[Fact]
	public void Events_ListsDatesAndCounts()
	{
		Seed(Today.AddDays(5));
		(int code, string output, string _) = Run("events");
		Assert.Equal(0, code);
		Assert.Contains("September flood (DR-1234)", output);
		Assert.Contains("2026-09-13", output);
		Assert.Contains("2026-09-16", output);
		Assert.Matches(@"2026-09-16\s+1\r?\n", output);
		Assert.Contains("1 events", output);
	}

	[Fact]
	public void Events_OnAnEmptyDatabase_ListsNone()
	{
		(int code, string output, string _) = Run("events");
		Assert.Equal(0, code);
		Assert.Contains("0 events", output);
	}

	[Fact]
	public void Qa_WithNoRequiredFindings_ExitsZero()
	{
		AssessmentFile f = Seed(null);
		f.Assessment.DateofInspection = new DateTime(2026, 9, 20);
		_t.Store.SaveAssessment(f);
		(int code, string output, string _) = Run("qa");
		Assert.Equal(0, code);
		Assert.Contains("Checked 1 assessments in the whole database.", output);
		Assert.Contains("0 required", output);
	}

	[Fact]
	public void Qa_SaysWhichChecksCouldNotRun_WithoutChangingTheExitCode()
	{
		AssessmentFile f = Seed(null);
		f.Assessment.DateofInspection = new DateTime(2026, 9, 20);
		_t.Store.SaveAssessment(f);
		(int code, string output, string _) = Run("qa");
		Assert.Equal(0, code);
		Assert.Contains("Not checked: Zone differs from the flood map (no stored flood lookup); Flood elevation unlike high-water marks (no high-water marks loaded).", output);
	}

	[Fact]
	public void Qa_WithAMarksFile_RunsTheHighWaterMarkCheck()
	{
		AssessmentFile f = Seed(null);
		f.Assessment.DateofInspection = new DateTime(2026, 9, 20);
		f.Assessment.DepthOfFlood = 10m;
		f.Property.Latitude = 39.7m;
		f.Property.Longitude = -84.2m;
		_t.Store.SaveAssessment(f);
		AssessmentFile saved = _t.Store.LoadAssessment(f.Assessment.AssessmentID);
		string lat = "39.7";
		string lon = "-84.2";
		string marks = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path_), "marks.csv");
		File.WriteAllText(marks, "Latitude,Longitude,Elevation,Datum\n" + string.Join("\n", Enumerable.Range(0, 3).Select(_ => $"{lat},{lon},25,{saved.Property.Datum}")) + "\n");
		(int code, string output, string _) = Run("qa", "--marks", marks);
		Assert.Equal(0, code);
		Assert.Contains("Flood elevation unlike", output);
		Assert.DoesNotContain("high-water marks loaded", output);
		Assert.Contains("Not checked: Zone differs from the flood map (no stored flood lookup).", output);
	}

	[Fact]
	public void Qa_WithARequiredFinding_ExitsNonZero_AndNamesTheStructure()
	{
		AssessmentFile f = Seed(null);
		f.Assessment.DateofInspection = new DateTime(2026, 9, 1);
		_t.Store.SaveAssessment(f);
		(int code, string output, string _) = Run("qa");
		Assert.Equal(1, code);
		Assert.Contains("Required", output);
		Assert.Contains("Inspected before the damage", output);
		Assert.Contains("123 River", output);
		Assert.Contains("1 required", output);
	}

	[Fact]
	public void Qa_ForOneEvent_ChecksOnlyItsAssessments()
	{
		AssessmentFile f = Seed(null);
		f.Assessment.DateofInspection = new DateTime(2026, 9, 1);
		_t.Store.SaveAssessment(f);
		_t.Store.SaveEvent(new EventRecord { Name = "Empty event" });
		(int code, string output, string _) = Run("qa", "--event", "empty event");
		Assert.Equal(0, code);
		Assert.Contains("Checked 0 assessments in the event Empty event.", output);
		(int code2, string output2, string _) = Run("qa", "--event", "September flood");
		Assert.Equal(1, code2);
		Assert.Contains("Checked 1 assessments", output2);
	}

	[Fact]
	public void Qa_UnknownEvent_IsAnError()
	{
		(int code, string _, string errors) = Run("qa", "--event", "nope");
		Assert.Equal(2, code);
		Assert.Contains("There is no event 'nope'", errors);
	}

	[Fact]
	public void Compliance_ListsOpenCasesWithDeadlineAndAddress()
	{
		Seed(Today.AddDays(10));
		(int code, string output, string _) = Run("compliance");
		Assert.Equal(0, code);
		Assert.Contains("2026-10-11", output);
		Assert.Contains("10 days", output);
		Assert.Contains("Open", output);
		Assert.Contains("123 River", output);
		Assert.Contains("1 cases", output);
	}

	[Fact]
	public void Compliance_Overdue_ListsOnlyPastDeadlines()
	{
		Seed(Today.AddDays(-3));
		Assert.Contains("1 cases", Run("compliance", "--overdue").Output);
		Assert.Contains("3 late", Run("compliance", "--overdue").Output);
	}

	[Fact]
	public void Compliance_Overdue_LeavesOutFutureDeadlines()
	{
		Seed(Today.AddDays(3));
		Assert.Contains("0 cases", Run("compliance", "--overdue").Output);
	}

	[Theory]
	[InlineData(7, 3, 1)]
	[InlineData(3, 3, 1)]
	[InlineData(2, 3, 0)]
	[InlineData(7, -1, 0)]
	public void Compliance_DueWithin_KeepsDeadlinesInTheWindow(int days, int deadlineOffset, int expected)
	{
		Seed(Today.AddDays(deadlineOffset));
		Assert.Contains($"{expected} cases", Run("compliance", "--due-within", days.ToString()).Output);
	}

	[Fact]
	public void Compliance_BadArguments_AreErrors()
	{
		Assert.Equal(2, Run("compliance", "--due-within", "soon").Code);
		Assert.Equal(2, Run("compliance", "--all", "--overdue").Code);
	}

	[Fact]
	public void ReadOnlyCommands_DoNotChangeTheDatabase()
	{
		Seed(Today.AddDays(1));
		DateTime before = File.GetLastWriteTimeUtc(Path_);
		long size = new FileInfo(Path_).Length;
		Run("events");
		Run("qa");
		Run("compliance");
		Assert.Equal(before, File.GetLastWriteTimeUtc(Path_));
		Assert.Equal(size, new FileInfo(Path_).Length);
	}

	[Fact]
	public void ReadOnlyCommands_WithAMistypedPath_StillSayThereIsNoDatabase()
	{
		TextWriter oldError = Console.Error;
		StringWriter errors = new StringWriter();
		Console.SetError(errors);
		try
		{
			Assert.Equal(2, Program.Main(new[] { "--db", System.IO.Path.Combine(_t.Folder, "missing.sqlite"), "events" }));
		}
		finally
		{
			Console.SetError(oldError);
		}
		Assert.Contains("There is no SDE database at", errors.ToString());
	}

	[Theory]
	[InlineData("summary")]
	[InlineData("detail")]
	public void ReportStructure_WritesAPdf(string kind)
	{
		AssessmentFile f = Seed(null);
		string file = System.IO.Path.Combine(_t.Folder, kind + ".pdf");
		(int code, string output, string _) = Run("report", "structure", f.Assessment.AssessmentID.ToString(), file, "--kind", kind, "--no-photos");
		Assert.Equal(0, code);
		Assert.Contains(file, output);
		Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file), 0, 4));
	}

	[Fact]
	public void ReportEvent_WritesAPdf_ForAnEventAndForTheWholeDatabase()
	{
		Seed(null);
		string one = System.IO.Path.Combine(_t.Folder, "event.pdf");
		string all = System.IO.Path.Combine(_t.Folder, "all.pdf");
		Assert.Equal(0, Run("report", "event", one, "--event", "September flood").Code);
		Assert.Equal(0, Run("report", "event", all).Code);
		Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(one), 0, 4));
		Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(all), 0, 4));
	}

	[Fact]
	public void Report_BadArguments_AreErrors()
	{
		Assert.Equal(2, Run("report").Code);
		Assert.Equal(2, Run("report", "poster", "x.pdf").Code);
		Assert.Equal(2, Run("report", "structure", Guid.NewGuid().ToString(), System.IO.Path.Combine(_t.Folder, "x.pdf")).Code);
		Assert.Equal(2, Run("report", "structure", Guid.NewGuid().ToString(), "x.pdf", "--kind", "poster").Code);
	}
}
