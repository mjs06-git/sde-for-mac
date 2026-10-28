using Avalonia.Headless.XUnit;
using Sde.App.Services;
using Sde.Data;
using Sde.TestSupport;
using Xunit;

namespace Sde.App.Tests;

/// <summary>
/// The tests never touch the person's own SDE database, settings or backups: the test process runs with a home folder
/// of its own (<see cref="TestHome"/>), so even code that runs without SDE_DATABASE (after a session clears it, or a
/// test that clears it on purpose) lands in a scratch folder.
/// </summary>
public class IsolationTests
{
	[AvaloniaFact]
	public void WithoutAForcedDatabase_TheAppOpensTheScratchHome_NotThePersonsOwn()
	{
		Assert.NotNull(TestHome.Home);
		Assert.StartsWith(TestHome.Home, SdeDatabase.DefaultPath);
		Assert.False(SdeDatabase.DefaultPath.StartsWith(TestHome.RealHome + Path.DirectorySeparatorChar, StringComparison.Ordinal));

		string forced = Environment.GetEnvironmentVariable("SDE_DATABASE");
		Environment.SetEnvironmentVariable("SDE_DATABASE", null);
		try
		{
			AppServices services = new AppServices(new TestDialogs());
			try
			{
				services.OpeningBackup.Wait(TimeSpan.FromSeconds(30));
				Assert.StartsWith(TestHome.Home, services.Store.Database.FilePath);
				Assert.All(services.Settings.RecentDatabases, p => Assert.StartsWith(TestHome.Home, p));
			}
			finally
			{
				services.Close();
			}
		}
		finally
		{
			Environment.SetEnvironmentVariable("SDE_DATABASE", forced);
		}
	}
}
