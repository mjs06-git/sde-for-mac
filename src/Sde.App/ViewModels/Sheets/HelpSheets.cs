using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Help;
using Sde.App.Services;

namespace Sde.App.ViewModels.Sheets;

/// <summary>A sheet that can open FEMA's SDE User Manual, or say where to read it when the PDF is not here.</summary>
public abstract partial class ManualSheetViewModel : SheetViewModel
{
	protected ManualSheetViewModel(AppServices app, string title)
		: base(title)
	{
		Services = app;
		ManualAvailable = app.Manual.IsAvailable;
	}

	protected AppServices Services { get; }

	/// <summary>The manual PDF was found (see <see cref="ManualService.FindPdf"/>).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ManualMissing), nameof(MissingMessage))]
	public partial bool ManualAvailable { get; protected set; }

	public bool ManualMissing => !ManualAvailable;

	/// <summary>Shown instead of the open buttons when the manual is not here.</summary>
	public string MissingMessage => ManualAvailable ? "" : ManualService.MissingMessage;

	/// <summary>Opens the manual at a page, or notes that the manual is missing.</summary>
	protected void OpenManualPage(int page) => ManualAvailable = Services.Manual.OpenPage(page);

	[RelayCommand]
	private void OpenManual() => ManualAvailable = Services.Manual.OpenFull();

	/// <summary>Opens FEMA's web page for the Substantial Damage Estimator.</summary>
	[RelayCommand]
	private void OpenFemaPage() => Services.Manual.OpenPublishedPage();
}

/// <summary>Help, then FEMA SDE User Manual, when the manual PDF is not here: says so and offers FEMA's page.</summary>
public sealed class ManualMissingSheetViewModel(AppServices app) : ManualSheetViewModel(app, "FEMA SDE User Manual")
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Medium;
}

/// <summary>The info button beside a field: what to enter and why, the manual page, and a button to open the manual there.</summary>
public sealed partial class FieldHelpSheetViewModel : ManualSheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Medium;

	public FieldHelpSheetViewModel(AppServices app, FieldHelpEntry entry, string label = null)
		: base(app, "Help: " + (label ?? entry.Label))
	{
		Entry = entry;
	}

	public FieldHelpEntry Entry { get; }

	public string Text => Entry.Text;

	/// <summary>"On the Location &amp; owner step. See the FEMA SDE User Manual, page 3-28."</summary>
	public string Where => $"On the {Entry.Step} step. See the FEMA SDE User Manual, {Entry.PageText}.";

	[RelayCommand]
	private void OpenAtPage() => OpenManualPage(Entry.Page);
}

/// <summary>Help, then SDE Help: every field's help in one searchable list.</summary>
public sealed partial class HelpIndexSheetViewModel : ManualSheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Large;

	public HelpIndexSheetViewModel(AppServices app)
		: base(app, "SDE Help")
	{
		Entries = FieldHelp.All;
		Selected = Entries.FirstOrDefault();
	}

	/// <summary>The fields whose name, step or help matches the search.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<FieldHelpEntry> Entries { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelection))]
	public partial FieldHelpEntry Selected { get; set; }

	[ObservableProperty]
	public partial string Search { get; set; }

	public bool HasSelection => Selected != null;

	/// <summary>"12 fields" or "No fields match".</summary>
	public string CountText => Entries.Count == 0 ? "No fields match." : Entries.Count == 1 ? "1 field" : $"{Entries.Count} fields";

	partial void OnSearchChanged(string value)
	{
		string[] words = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Entries = FieldHelp.All
			.Where(e => words.All(w => e.Label.Contains(w, StringComparison.OrdinalIgnoreCase)
				|| e.Step.Contains(w, StringComparison.OrdinalIgnoreCase)
				|| e.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))
			.ToList();
		if (Selected == null || !Entries.Contains(Selected))
		{
			Selected = Entries.FirstOrDefault();
		}
		OnPropertyChanged(nameof(CountText));
	}

	[RelayCommand]
	private void OpenAtPage()
	{
		if (Selected != null)
		{
			OpenManualPage(Selected.Page);
		}
	}
}

/// <summary>
/// One page of the walkthrough: one or two short sentences. The sheet's title is the only heading on screen, so a
/// page shows only its body; <see cref="Heading"/> names the page.
/// </summary>
public sealed record WalkthroughPage(string Heading, string Body);

/// <summary>The walkthrough shown on first launch and from Help: a few short pages with Back and Next.</summary>
public sealed partial class WalkthroughSheetViewModel : SheetViewModel
{
	/// <summary>The sheet's width (see <see cref="SheetSize"/>).</summary>
	public override SheetSize Size => SheetSize.Medium;

	/// <summary>The result of the sheet when "Open the sample database" was chosen.</summary>
	public const string OpenSampleResult = "sample";

	public WalkthroughSheetViewModel()
		: base("Welcome to the Substantial Damage Estimator")
	{
	}

	public IReadOnlyList<WalkthroughPage> Pages { get; } = new[]
	{
		new WalkthroughPage("What SDE does",
			"SDE works out whether a building is substantially damaged: repairs costing 50 percent or more of its value before the damage. It uses FEMA's own method."),
		new WalkthroughPage("The Structures list",
			"Structures lists every building with its percent damaged. Press Return to open one, or ⌘N to add one."),
		new WalkthroughPage("Eight steps and a live result",
			"Each assessment has eight steps, and the result updates as you type. Your work saves itself."),
		new WalkthroughPage("Import and export",
			"Import SDE Data (⌘I) and Export All to SDE (⌘E) exchange folders with FEMA's SDE."),
		new WalkthroughPage("Where to find help",
			"The ? beside a field explains it. The Help menu has SDE Help, the sample database and this walkthrough."),
	};

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Current), nameof(CanGoBack), nameof(IsLast), nameof(NextText), nameof(PositionText))]
	[NotifyCanExecuteChangedFor(nameof(BackCommand))]
	public partial int PageIndex { get; private set; }

	public WalkthroughPage Current => Pages[PageIndex];

	public bool CanGoBack => PageIndex > 0;

	public bool IsLast => PageIndex == Pages.Count - 1;

	public string NextText => IsLast ? "Done" : "Next";

	/// <summary>"Page 2 of 5".</summary>
	public string PositionText => $"Page {PageIndex + 1} of {Pages.Count}";

	[RelayCommand(CanExecute = nameof(CanGoBack))]
	private void Back() => PageIndex--;

	/// <summary>Next, or Done on the last page.</summary>
	[RelayCommand]
	private void Next()
	{
		if (IsLast)
		{
			Close(null);
		}
		else
		{
			PageIndex++;
		}
	}

	/// <summary>Closes the walkthrough and asks for the sample database to be opened.</summary>
	[RelayCommand]
	private void OpenSample() => Close(OpenSampleResult);
}
