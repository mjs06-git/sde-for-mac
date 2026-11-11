using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;

namespace Sde.App.Tests;

/// <summary>
/// The Mac-style date field (Controls/DateField) that replaced Fluent's date pickers everywhere: what it reads, the
/// arrow keys, clearing, text that isn't a date, the calendar, and its two-way binding to the view models' dates.
/// </summary>
public class DateFieldTests
{
	private static readonly DateTime ArmisticeDay = new DateTime(2026, 11, 11);

	[Theory]
	[InlineData("11/11/2026")]
	[InlineData("11/11/26")]
	[InlineData("2026-11-11")]
	[InlineData("2026/11/11")]
	[InlineData("11-11-2026")]
	[InlineData("Nov 11 2026")]
	[InlineData("Nov 11, 2026")]
	[InlineData("nov. 11, 2026")]
	[InlineData("November 11, 2026")]
	[InlineData("11 Nov 2026")]
	[InlineData("  11/11/2026  ")]
	[InlineData("20261111")]
	public void CommonUsForms_AreRead(string typed)
	{
		Assert.True(DateField.TryParse(typed, out DateTime date), typed);
		Assert.Equal(ArmisticeDay, date);
	}

	[Fact]
	public void AMissingYear_IsThisYear_AndTodayIsAWord()
	{
		Assert.True(DateField.TryParse("Sept 3", out DateTime date));
		Assert.Equal(new DateTime(DateTime.Today.Year, 9, 3), date);
		Assert.True(DateField.TryParse("today", out DateTime today));
		Assert.Equal(DateTime.Today, today);
	}

	[Theory]
	[InlineData("")]
	[InlineData("banana")]
	[InlineData("13/40/2026")]
	[InlineData("2026-02-30")]
	[InlineData("11/11/1066")]
	[InlineData("11/11/2026 and more")]
	public void AnythingElse_IsNotADate(string typed) => Assert.False(DateField.TryParse(typed, out _), typed);

	[Fact]
	public void Dates_ShowInTheAppsFormat()
	{
		Assert.Equal("Nov 11, 2026", DateField.Show(ArmisticeDay));
		Assert.Equal("Sep 3, 2026", DateField.Show(new DateTime(2026, 9, 3)));
		Assert.Equal("", DateField.Show(null));
	}

	/// <summary>A window with the field and a text box to move to, so leaving the field can be tried.</summary>
	private static (Window Window, DateField Field, TextBox Other) Host(DateTime? date = null)
	{
		DateField field = new DateField { SelectedDate = date };
		TextBox other = new TextBox();
		Window window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { field, other } } };
		window.Show();
		field.Focus();
		return (window, field, other);
	}

	[AvaloniaFact]
	public void ATypedDate_IsReadOnReturn_AndOnLeavingTheField()
	{
		(Window window, DateField field, TextBox other) = Host();
		try
		{
			Assert.True(string.IsNullOrEmpty(field.Text));
			Assert.Equal("No date", field.PlaceholderText);
			field.Text = "11/11/26";
			Assert.Null(field.SelectedDate);                  // nothing is read while typing
			window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
			Assert.Equal(ArmisticeDay, field.SelectedDate);
			Assert.Equal("Nov 11, 2026", field.Text);         // shown the app's way

			field.Text = "2026-12-01";
			other.Focus();
			Assert.Equal(new DateTime(2026, 12, 1), field.SelectedDate);
			Assert.Equal("Dec 1, 2026", field.Text);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void UpAndDown_StepTheDay_AndAnEmptyFieldStartsAtToday()
	{
		(Window window, DateField field, _) = Host(ArmisticeDay);
		try
		{
			window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
			Assert.Equal(new DateTime(2026, 11, 12), field.SelectedDate);
			window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
			window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
			Assert.Equal(new DateTime(2026, 11, 10), field.SelectedDate);
			Assert.Equal("Nov 10, 2026", field.Text);
			// Typed text is read first, then stepped
			field.Text = "12/31/2026";
			window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
			Assert.Equal(new DateTime(2027, 1, 1), field.SelectedDate);

			field.Clear();
			window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
			Assert.Equal(DateTime.Today, field.SelectedDate);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void TheClearButton_EmptiesTheField_AndShowsOnlyWithADate()
	{
		(Window window, DateField field, _) = Host(ArmisticeDay);
		try
		{
			Assert.True(field.ClearButton.IsVisible);
			Assert.Equal("Clear date", ControlAutomationPeer.CreatePeerForElement(field.ClearButton).GetName());
			field.ClearButton.Command = null;
			field.ClearButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
			Assert.Null(field.SelectedDate);
			Assert.True(string.IsNullOrEmpty(field.Text));
			Assert.False(field.ClearButton.IsVisible);
			// A required date can turn the button off; a field that can't be changed never shows it
			field.SelectedDate = ArmisticeDay;
			field.IsClearable = false;
			Assert.False(field.ClearButton.IsVisible);
			field.IsClearable = true;
			field.IsEnabled = false;
			Assert.False(field.ClearButton.IsVisible);
			// Emptying the text and pressing Return clears it too
			field.IsEnabled = true;
			field.Focus();
			field.Text = "";
			window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
			Assert.Null(field.SelectedDate);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void TextThatIsNotADate_ShowsTheError_AndKeepsTheDate()
	{
		(Window window, DateField field, TextBox other) = Host(ArmisticeDay);
		try
		{
			bool returnWentOn = false;
			window.AddHandler(InputElement.KeyDownEvent, (_, e) => returnWentOn |= e.Key == Key.Enter, Avalonia.Interactivity.RoutingStrategies.Bubble);
			field.Text = "next Tuesday-ish";
			window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
			Assert.True(field.HasError);
			Assert.Contains(":error", field.Classes);
			Assert.Equal(ArmisticeDay, field.SelectedDate);   // the date is not lost
			Assert.False(returnWentOn);                       // Return stops at the field instead of pressing a default button
			Assert.Contains(DateField.ErrorText, DataValidationErrors.GetErrors(field)!.Cast<object>().Select(o => o.ToString()));

			// Leaving keeps the error showing, and the date
			other.Focus();
			Assert.True(field.HasError);
			Assert.Equal(ArmisticeDay, field.SelectedDate);

			// Fixing the text clears the error
			field.Text = "banana";
			field.Commit();
			Assert.True(field.HasError);
			field.Text = "11/12/2026";
			field.Commit();
			Assert.False(field.HasError);
			Assert.Equal(new DateTime(2026, 11, 12), field.SelectedDate);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void TheCalendar_OpensFromItsButtonOrOptionDown_AndPicksADate()
	{
		(Window window, DateField field, _) = Host(ArmisticeDay);
		try
		{
			Assert.Equal("Show calendar", ControlAutomationPeer.CreatePeerForElement(field.CalendarButton).GetName());
			Assert.False(field.CalendarButton.Focusable);    // the field is one Tab stop
			PopupFlyoutBase flyout = field.CalendarFlyout;
			flyout.Popup.ShouldUseOverlayLayer = true;
			window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
			Assert.True(flyout.IsOpen);
			Assert.Equal(ArmisticeDay, field.Calendar.SelectedDate);
			Assert.Equal(ArmisticeDay, field.Calendar.DisplayDate);

			field.Calendar.SelectedDate = new DateTime(2026, 11, 20);
			Assert.Equal(new DateTime(2026, 11, 20), field.SelectedDate);
			Assert.Equal("Nov 20, 2026", field.Text);
			Assert.False(flyout.IsOpen);                     // picking a day closes it
		}
		finally
		{
			window.Close();
		}
	}

	/// <summary>A view model with both kinds of date the field binds to.</summary>
	private sealed class Dates : INotifyPropertyChanged
	{
		private DateTime? _date;
		private DateTimeOffset? _offset;

		public event PropertyChangedEventHandler PropertyChanged;

		public DateTime? Date
		{
			get => _date;
			set
			{
				_date = value;
				Changed();
			}
		}

		public DateTimeOffset? Offset
		{
			get => _offset;
			set
			{
				_offset = value;
				Changed();
			}
		}

		private void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	[AvaloniaFact]
	public void ItBindsBothWays_ToADateTimeAndToADateTimeOffset()
	{
		Dates vm = new Dates { Date = ArmisticeDay, Offset = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.FromHours(-5)) };
		DateField plain = new DateField();
		DateField offset = new DateField();
		plain.Bind(DateField.SelectedDateProperty, new ReflectionBinding(nameof(Dates.Date)));
		offset.Bind(DateField.SelectedDateOffsetProperty, new ReflectionBinding(nameof(Dates.Offset)));
		Window window = new Window { DataContext = vm, Content = new StackPanel { Children = { plain, offset } } };
		window.Show();
		try
		{
			Assert.Equal("Nov 11, 2026", plain.Text);
			Assert.Equal("Sep 14, 2026", offset.Text);
			plain.Text = "1/2/2027";
			plain.Commit();
			Assert.Equal(new DateTime(2027, 1, 2), vm.Date);
			offset.Text = "9/15/2026";
			offset.Commit();
			Assert.Equal(new DateTime(2026, 9, 15), vm.Offset!.Value.Date);
			vm.Date = null;
			Assert.Equal("", plain.Text);
			plain.Text = "";
			plain.Commit();
			Assert.Null(vm.Date);
		}
		finally
		{
			window.Close();
		}
	}

	/// <summary>Fluent's date pickers ("Pick a date" and a calendar tile) are gone from every view and field template.</summary>
	[Fact]
	public void NoDatePickerIsLeft_InTheViewsOrTheFieldTemplates()
	{
		string app = Path.Combine(TestPaths.Root, "src", "Sde.App");
		IEnumerable<string> files = Directory.GetFiles(Path.Combine(app, "Views"), "*.axaml").Append(Path.Combine(app, "App.axaml"));
		Regex picker = new Regex("<(CalendarDatePicker|DatePicker)\\b|Pick a date");
		List<string> found = files.Where(f => picker.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
		Assert.True(found.Count == 0, "Date pickers left in: " + string.Join(", ", found));
		int fields = files.Sum(f => Regex.Matches(File.ReadAllText(f), "<c:DateField\\b").Count);
		Assert.True(fields >= 13, $"Only {fields} date fields");
	}

	/// <summary>The editor's dates are date fields that write to the assessment; seen in a form, light and dark, and with the calendar open.</summary>
	[AvaloniaFact]
	public void TheEditorsDates_AreDateFields_ThatEditTheAssessment()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		AssessmentEditorViewModel editor = ui.Main.Editor;
		editor.SelectedSection = editor.Sections.First(s => s.Title == "Inspection");
		ui.Pump();
		DateField inspected = ui.Window.GetVisualDescendants().OfType<DateField>()
			.Single(d => ControlAutomationPeer.CreatePeerForElement(d).GetName() == "Inspection date");
		Assert.Equal(DateField.Show(editor.Assessment.DateofInspection), inspected.Text);
		Assert.Equal(ui.Window.FindResource("ControlHeight") is double h ? h : 0, inspected.Bounds.Height, 0);
		inspected.Focus();
		inspected.Text = "9/16/2026";
		ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(new DateTime(2026, 9, 16), editor.Assessment.DateofInspection);
		ui.Snapshot("60-date-field");

		DateField damaged = ui.Window.GetVisualDescendants().OfType<DateField>()
			.Single(d => ControlAutomationPeer.CreatePeerForElement(d).GetName() == "Date damage occurred");
		damaged.Focus();
		damaged.CalendarFlyout.Popup.ShouldUseOverlayLayer = true;
		damaged.OpenCalendar();
		ui.Snapshot("61-date-field-calendar");
		damaged.CloseCalendar();

		Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
		ui.Pump();
		ui.Snapshot("62-dark-date-field");
		damaged.OpenCalendar();
		ui.Snapshot("63-dark-date-field-calendar");
		damaged.CloseCalendar();
	}

	/// <summary>A sheet's dates bind the same way: the compliance case's deadline, typed and then cleared.</summary>
	[AvaloniaFact]
	public async Task ASheetsDate_IsADateField_BoundToItsViewModel()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		Guid ranch = ui.Store.ListStructures().First(s => s.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value;
		ui.Store.OpenComplianceCase(ranch);
		ui.Main.ShowCommand.Execute(ViewModels.Page.Compliance);
		ui.Pump();
		Task open = ui.Main.Compliance.OpenCaseCommand.ExecuteAsync(ui.Main.Compliance.Rows[0]);
		ui.Pump();
		ComplianceCaseSheetViewModel sheet = Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current);
		DateField deadline = ui.Window.GetVisualDescendants().OfType<DateField>()
			.Single(d => ControlAutomationPeer.CreatePeerForElement(d).GetName() == "Compliance deadline");
		deadline.Text = "3/1/2027";
		deadline.Commit();
		Assert.Equal(new DateTime(2027, 3, 1), sheet.Deadline);
		ui.Snapshot("64-date-field-sheet");
		deadline.Clear();
		Assert.Null(sheet.Deadline);
		Assert.Empty(ui.Window.GetVisualDescendants().OfType<CalendarDatePicker>());
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await open;
	}
}
