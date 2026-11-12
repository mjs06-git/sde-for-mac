using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.ViewModels;

/// <summary>
/// App-level additions: What's New (once after an update, and from Help and Settings, About), Help, then Check for
/// Updates (only when chosen), and files dropped anywhere on the window (<see cref="DropRouter"/>).
/// </summary>
public sealed partial class MainViewModel
{
	private DropRouter _drops;

	/// <summary>Sends files dropped on the window to the flow for their kind.</summary>
	public DropRouter Drops => _drops ??= new DropRouter(_app, this);

	/// <summary>Settings is made again for each database, so its About card's What's New button is connected each time.</summary>
	partial void OnSettingsChanged(SettingsViewModel value)
	{
		if (value != null)
		{
			value.ShowWhatsNew = () => _ = ShowWhatsNew();
		}
	}

	/// <summary>Help, then What's New in SDE (and Settings, About's What's New).</summary>
	[RelayCommand]
	public Task ShowWhatsNew() => Sheets.ShowAsync(new WhatsNewSheetViewModel());

	/// <summary>
	/// Help, then Check for Updates…: asks GitHub for the latest release now, because it was chosen. Nothing else in SDE
	/// asks, and nothing is downloaded or installed.
	/// </summary>
	[RelayCommand]
	public Task CheckForUpdates() => CheckForUpdatesAsync(AppVersion.Running);

	/// <summary><see cref="CheckForUpdates"/> for a given running version (tests use made-up ones).</summary>
	public async Task CheckForUpdatesAsync(string runningVersion)
	{
		UpdateSheetViewModel sheet = new UpdateSheetViewModel(_app, runningVersion);
		Task<object> closed = Sheets.ShowAsync(sheet);
		await sheet.CheckAsync();
		await closed;
	}

	/// <summary>
	/// On launch: What's New, once, when this version differs from the one last seen. Not on a brand-new install, where
	/// the walkthrough shows instead (the version is only recorded), and never when SDE_DATABASE chose the database
	/// (tests and training sessions), like the walkthrough. Call before <see cref="ShowWalkthroughIfFirstRun"/>.
	/// </summary>
	public Task ShowWhatsNewIfUpdated() =>
		string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDE_DATABASE"))
			? ShowWhatsNewIfUpdated(AppVersion.Running, isFirstRun: !_app.Settings.SeenWalkthrough)
			: Task.CompletedTask;

	/// <summary>
	/// <see cref="ShowWhatsNewIfUpdated()"/> for a given running version. Records <paramref name="runningVersion"/> as seen,
	/// and shows What's New only when one had been seen before and it was different, or (an update from a version before
	/// What's New existed) none had been seen and this is not a first run.
	/// </summary>
	public Task ShowWhatsNewIfUpdated(string runningVersion, bool isFirstRun)
	{
		string lastSeen = _app.Settings.LastSeenVersion;
		if (string.Equals(lastSeen, runningVersion, StringComparison.Ordinal))
		{
			return Task.CompletedTask;
		}
		_app.Settings.LastSeenVersion = runningVersion;
		try
		{
			_app.Settings.Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Write("Couldn't remember which version's What's New was shown", ex);
		}
		return isFirstRun && lastSeen == null ? Task.CompletedTask : ShowWhatsNew();
	}
}
