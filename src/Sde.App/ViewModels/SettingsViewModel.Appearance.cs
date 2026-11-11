using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Sde.App.Services;

namespace Sde.App.ViewModels;

/// <summary>
/// Settings, Appearance: match the Mac, light or dark; SDE's blue or the Mac's accent colour; Comfortable or Compact.
/// The theme and the accent change at once and are saved; the density is saved and used the next time SDE opens.
/// </summary>
public sealed partial class SettingsViewModel
{
	/// <summary>The theme choices, in the order of the tiles: match the Mac (no setting), light, dark.</summary>
	public static IReadOnlyList<string> ThemeChoices { get; } = new[] { "Match System", "Light", "Dark" };

	/// <summary>The one line under Density.</summary>
	public const string DensityNote = "Takes effect the next time SDE opens.";

	/// <summary>Applies the theme and the accent. The real one is <see cref="Appearance.Apply"/>; tests can watch the calls.</summary>
	internal Action<AppSettings> ApplyAppearance { get; set; } = settings =>
	{
		if (Application.Current is Application app)
		{
			Appearance.Apply(app, settings);
		}
	};

	/// <summary>The Mac's accent colour can only be followed on a Mac.</summary>
	public bool CanUseMacAccent => OperatingSystem.IsMacOS();

	/// <summary>Why the Mac's accent can't be chosen here, for the disabled choice's tooltip; null on a Mac.</summary>
	public string MacAccentNote => CanUseMacAccent ? "Follows the accent colour chosen in System Settings" : "Only on a Mac";

	/// <summary>0 = match the Mac, 1 = light, 2 = dark.</summary>
	public int ThemeIndex
	{
		get => _app.Settings.Theme switch
		{
			Appearance.Light => 1,
			Appearance.Dark => 2,
			_ => 0,
		};
		set
		{
			string theme = value switch
			{
				1 => Appearance.Light,
				2 => Appearance.Dark,
				_ => null,
			};
			if (value < 0 || theme == _app.Settings.Theme)
			{
				return;
			}
			_app.Settings.Theme = theme;
			SaveAndApplyAppearance();
			OnPropertyChanged();
		}
	}

	/// <summary>0 = SDE's blue, 1 = the Mac's accent colour.</summary>
	public int AccentIndex
	{
		get => _app.Settings.UseMacAccent ? 1 : 0;
		set
		{
			bool mac = value == 1;
			if (value < 0 || mac == _app.Settings.UseMacAccent)
			{
				return;
			}
			_app.Settings.UseMacAccent = mac;
			SaveAndApplyAppearance();
			OnPropertyChanged();
		}
	}

	/// <summary>0 = Comfortable, 1 = Compact. Saved now, used the next time SDE opens.</summary>
	public int DensityIndex
	{
		get => _app.Settings.CompactDensity ? 1 : 0;
		set
		{
			bool compact = value == 1;
			if (value < 0 || compact == _app.Settings.CompactDensity)
			{
				return;
			}
			_app.Settings.CompactDensity = compact;
			_app.Settings.Save();
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsDensityChangePending));
		}
	}

	/// <summary>The density chosen is not the one SDE opened with this time.</summary>
	public bool IsDensityChangePending => _app.Settings.CompactDensity != Appearance.IsCompact;

	private void SaveAndApplyAppearance()
	{
		_app.Settings.Save();
		ApplyAppearance(_app.Settings);
	}
}
