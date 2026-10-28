namespace Sde.TestSupport;

/// <summary>
/// Gives each test process a home folder of its own, so nothing a test runs can reach the person's own
/// ~/Library/Application Support/SDE (their database, settings, backups, map tiles, letter templates) or
/// ~/Library/Logs/SDE. SDE_HOME moves SDE's folder (SdeDatabase.DefaultPath, which everything kept beside the database
/// follows; .NET finds Application Support through macOS, which ignores HOME), and HOME moves the logs and "~" paths.
/// The SDE processes the tests start inherit both. Every test project calls <see cref="Isolate"/> from a module
/// initializer (tests/IsolateHome.cs, linked in by tests/Directory.Build.props), before any test runs.
/// </summary>
public static class TestHome
{
	/// <summary>The home folder the tests run with (a new folder under the system's temporary folder).</summary>
	public static string Home { get; private set; }

	/// <summary>The person's real home folder, which no test may write under.</summary>
	public static string RealHome { get; private set; }

	public static void Isolate()
	{
		if (Home != null)
		{
			return;
		}
		RealHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		Home = Path.Combine(Path.GetTempPath(), $"sde-test-home-{Environment.ProcessId}");
		Directory.CreateDirectory(Home);
		Environment.SetEnvironmentVariable("HOME", Home);
		Environment.SetEnvironmentVariable("SDE_HOME", Path.Combine(Home, "Library", "Application Support", "SDE"));
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			try
			{
				Directory.Delete(Home, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Left for the system to clear from its temporary folder.
			}
		};
	}
}
