using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Calendar = Avalonia.Controls.Calendar;

namespace Sde.App.Controls;

/// <summary>
/// A compact date field, as on the Mac: a text box showing the date the way the app writes dates ("Nov 11, 2026"),
/// a small calendar button that opens a month to pick from, and a clear button while there is a date. Type a date in
/// any common US form (11/11/2026, 11/11/26, 2026-11-11, Nov 11 2026, November 11, 2026, or "today"); it is read when
/// Return is pressed or the field is left. Up and Down step the day (an empty field starts at today); Option-Down or F4
/// opens the calendar. Text that isn't a date shows the field's error state, with a line under it saying what to type,
/// and leaves the date as it was (closing a sheet with Esc keeps it too). Binds two-way to a <see cref="DateTime"/>? (<see cref="SelectedDate"/>) or a <see cref="DateTimeOffset"/>?
/// (<see cref="SelectedDateOffset"/>). Styled in Styles/Accessibility.axaml (TextBox.dateField).
/// <c>&lt;c:DateField SelectedDate="{Binding Deadline}" AutomationProperties.Name="Compliance deadline" /&gt;</c>
/// </summary>
public class DateField : TextBox
{
	/// <summary>How dates are shown: the app's date format.</summary>
	public const string DisplayFormat = "MMM d, yyyy";

	/// <summary>What the error state says under the field.</summary>
	public const string ErrorText = "Type a date such as 11/11/2026";

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>The forms a typed date is read in (with or without a comma after the day; a missing year is this year).</summary>
	private static readonly string[] Formats =
	{
		"M/d/yyyy", "M/d/yy", "M-d-yyyy", "M-d-yy", "M.d.yyyy", "M.d.yy",
		"yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd",
		"MMM d yyyy", "MMMM d yyyy", "MMM d yy", "d MMM yyyy", "d MMMM yyyy",
		"M/d", "M-d", "MMM d", "MMMM d",
	};

	/// <summary>The date shown; null for none. Two-way by default.</summary>
	public static readonly StyledProperty<DateTime?> SelectedDateProperty =
		AvaloniaProperty.Register<DateField, DateTime?>(nameof(SelectedDate), defaultBindingMode: BindingMode.TwoWay);

	/// <summary>The same date as a <see cref="DateTimeOffset"/>, for a view model that keeps one. Two-way by default.</summary>
	public static readonly StyledProperty<DateTimeOffset?> SelectedDateOffsetProperty =
		AvaloniaProperty.Register<DateField, DateTimeOffset?>(nameof(SelectedDateOffset), defaultBindingMode: BindingMode.TwoWay);

	/// <summary>Whether the clear button shows while there is a date (the date is optional). On by default.</summary>
	public static readonly StyledProperty<bool> IsClearableProperty = AvaloniaProperty.Register<DateField, bool>(nameof(IsClearable), true);

	/// <summary>The typed text isn't a date: the field shows its error state and the date is unchanged.</summary>
	public static readonly DirectProperty<DateField, bool> HasErrorProperty =
		AvaloniaProperty.RegisterDirect<DateField, bool>(nameof(HasError), f => f.HasError);

	private readonly Button _clear;
	private readonly Button _pick;
	private readonly Calendar _calendar;
	private readonly Flyout _flyout;
	private bool _hasError;
	private bool _syncing;

	static DateField()
	{
		PlaceholderTextProperty.OverrideDefaultValue<DateField>("No date");
		AcceptsReturnProperty.OverrideDefaultValue<DateField>(false);
		TextWrappingProperty.OverrideDefaultValue<DateField>(Avalonia.Media.TextWrapping.NoWrap);
	}

	public DateField()
	{
		// Styled as a text box (StyleKeyOverride), with its own parts styled through this class
		Classes.Add("dateField");
		_calendar = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate };
		AutomationProperties.SetName(_calendar, "Calendar");
		_calendar.PropertyChanged += OnCalendarChanged;
		_flyout = new Flyout { Content = _calendar, Placement = PlacementMode.BottomEdgeAlignedLeft };
		_flyout.Opened += OnCalendarOpened;
		_flyout.Closed += (_, _) => Focus(NavigationMethod.Unspecified);

		_clear = MakeButton("ErrorCircle", "Clear date", "Clear Date");
		_clear.Classes.Add("dateFieldClear");
		_clear.Click += (_, _) => Clear();
		_pick = MakeButton("Event", "Show calendar", "Show Calendar");
		_pick.Classes.Add("dateFieldPick");
		_pick.Click += (_, _) => OpenCalendar();

		InnerRightContent = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Children = { _clear, _pick },
		};
		UpdateClear();
	}

	protected override Type StyleKeyOverride => typeof(TextBox);

	/// <inheritdoc cref="SelectedDateProperty"/>
	public DateTime? SelectedDate
	{
		get => GetValue(SelectedDateProperty);
		set => SetValue(SelectedDateProperty, value);
	}

	/// <inheritdoc cref="SelectedDateOffsetProperty"/>
	public DateTimeOffset? SelectedDateOffset
	{
		get => GetValue(SelectedDateOffsetProperty);
		set => SetValue(SelectedDateOffsetProperty, value);
	}

	/// <inheritdoc cref="IsClearableProperty"/>
	public bool IsClearable
	{
		get => GetValue(IsClearableProperty);
		set => SetValue(IsClearableProperty, value);
	}

	/// <inheritdoc cref="HasErrorProperty"/>
	public bool HasError
	{
		get => _hasError;
		private set
		{
			if (SetAndRaise(HasErrorProperty, ref _hasError, value))
			{
				PseudoClasses.Set(":error", value);
				if (value)
				{
					DataValidationErrors.SetErrors(this, new object[] { ErrorText });
				}
				else
				{
					DataValidationErrors.ClearErrors(this);
				}
			}
		}
	}

	/// <summary>The calendar button and the clear button, for tests.</summary>
	public Button CalendarButton => _pick;

	public Button ClearButton => _clear;

	/// <summary>The month shown in the calendar flyout, for tests.</summary>
	public Calendar Calendar => _calendar;

	/// <summary>The flyout the calendar opens in, for tests.</summary>
	public Flyout CalendarFlyout => _flyout;

	/// <summary>A date as the field shows it ("Nov 11, 2026"), or empty for none.</summary>
	public static string Show(DateTime? date) => date?.ToString(DisplayFormat, Us) ?? string.Empty;

	/// <summary>
	/// Reads a typed date in the forms the field accepts (see the class summary). False for anything else, including a
	/// year before 1900 or after 2199, which is far more likely a slip than a real date.
	/// </summary>
	public static bool TryParse(string text, out DateTime date)
	{
		date = default;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string t = string.Join(' ', text.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
		switch (t.ToLowerInvariant())
		{
			case "today":
				date = DateTime.Today;
				return true;
			case "yesterday":
				date = DateTime.Today.AddDays(-1);
				return true;
			case "tomorrow":
				date = DateTime.Today.AddDays(1);
				return true;
		}
		// "Nov. 11, 2026" and "Sept 11" read as "Nov 11 2026" and "Sep 11"; a comma after the day is optional
		t = t.Replace(",", " ").Replace(". ", " ");
		t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
		if (t.StartsWith("Sept ", StringComparison.OrdinalIgnoreCase))
		{
			t = "Sep" + t[4..];
		}
		if (!DateTime.TryParseExact(t, Formats, Us, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed) || parsed.Year < 1900 || parsed.Year > 2199)
		{
			return false;
		}
		date = parsed.Date;
		return true;
	}

	/// <summary>Reads the typed text now: a date, nothing (clears it), or the error state (the date stays as it was).</summary>
	public void Commit()
	{
		string text = Text?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			HasError = false;
			SetDate(null);
			return;
		}
		if (TryParse(text, out DateTime date))
		{
			HasError = false;
			SetDate(date);
			ShowDate();                                 // the typed form becomes the app's ("11/11/26" shows "Nov 11, 2026")
			return;
		}
		HasError = true;
	}

	/// <summary>Moves the date by <paramref name="days"/> (an empty field starts at today). Nothing while the text isn't a date.</summary>
	public void Step(int days)
	{
		Commit();
		if (HasError || !IsEnabled || IsReadOnly)
		{
			return;
		}
		SetDate(SelectedDate is DateTime d ? d.AddDays(days) : DateTime.Today);
		ShowDate();
		CaretIndex = Text?.Length ?? 0;
	}

	/// <summary>Empties the field (the clear button).</summary>
	public void Clear()
	{
		HasError = false;
		SetDate(null);
		ShowDate();
		Focus(NavigationMethod.Unspecified);
	}

	/// <summary>Opens the calendar under the field.</summary>
	public void OpenCalendar()
	{
		if (IsEnabled && !IsReadOnly && IsEffectivelyVisible)
		{
			_flyout.ShowAt(this);                         // under the field, from its leading edge
		}
	}

	/// <summary>Closes the calendar, if it is open.</summary>
	public void CloseCalendar() => _flyout.Hide();

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == SelectedDateProperty)
		{
			if (!_syncing)
			{
				_syncing = true;
				SelectedDateOffset = SelectedDate is DateTime d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(d.Date)) : null;
				_syncing = false;
				HasError = false;
				ShowDate();
			}
			UpdateClear();
		}
		else if (change.Property == SelectedDateOffsetProperty && !_syncing)
		{
			_syncing = true;
			SelectedDate = SelectedDateOffset?.Date;
			_syncing = false;
			HasError = false;
			ShowDate();
			UpdateClear();
		}
		else if (change.Property == IsClearableProperty || change.Property == IsEnabledProperty || change.Property == IsReadOnlyProperty)
		{
			UpdateClear();
		}
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		KeyModifiers mods = e.KeyModifiers;
		if ((e.Key == Key.Down && mods == KeyModifiers.Alt) || (e.Key == Key.F4 && mods == KeyModifiers.None))
		{
			OpenCalendar();
			e.Handled = true;
			return;
		}
		if ((e.Key == Key.Up || e.Key == Key.Down) && mods == KeyModifiers.None)
		{
			Step(e.Key == Key.Up ? 1 : -1);
			e.Handled = true;
			return;
		}
		if (e.Key == Key.Enter && mods == KeyModifiers.None)
		{
			Commit();
			// A good date lets Return go on to the sheet's default button; a bad one stops it there.
			e.Handled = HasError;
			if (HasError)
			{
				return;
			}
		}
		base.OnKeyDown(e);
	}

	protected override void OnLostFocus(FocusChangedEventArgs e)
	{
		base.OnLostFocus(e);
		Commit();
	}

	private void SetDate(DateTime? date)
	{
		if (SelectedDate != date)
		{
			SelectedDate = date;
		}
		UpdateClear();
	}

	private void ShowDate() => Text = Show(SelectedDate);

	private void UpdateClear() => _clear.IsVisible = IsClearable && SelectedDate != null && IsEnabled && !IsReadOnly;

	private void OnCalendarOpened(object sender, EventArgs e)
	{
		Commit();
		_syncing = true;
		_calendar.SelectedDate = SelectedDate;
		_calendar.DisplayDate = SelectedDate ?? DateTime.Today;
		_syncing = false;
		Dispatcher.UIThread.Post(() => _calendar.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
	}

	private void OnCalendarChanged(object sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property != Calendar.SelectedDateProperty || _syncing || _calendar.SelectedDate is not DateTime picked)
		{
			return;
		}
		HasError = false;
		SetDate(picked.Date);
		ShowDate();
		_flyout.Hide();
	}

	private static Button MakeButton(string icon, string name, string tip)
	{
		Button button = new Button
		{
			Content = new Icon { Kind = icon, Size = 14 },
			Focusable = false,
			IsTabStop = false,
		};
		button.Classes.Add("dateFieldButton");
		AutomationProperties.SetName(button, name);
		ToolTip.SetTip(button, tip);
		return button;
	}
}
