using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Sde.App.Controls;

/// <summary>
/// The result as the one dark card in the content area: the percent damaged very large in white, the determination in
/// words (in the frame's red when the structure is substantially damaged), how far it stands from the 50 percent line,
/// and the staff gauge drawn for the dark card. Structures' inspector and the editor's result panel both open with it,
/// so the result looks the same wherever it is shown. The look is its control theme in Styles/Themes.axaml.
/// </summary>
public sealed class ResultHero : TemplatedControl
{
	/// <summary>An optional heading at the top of the card (Structures shows the address there).</summary>
	public static readonly StyledProperty<string> TitleProperty =
		AvaloniaProperty.Register<ResultHero, string>(nameof(Title));

	/// <summary>An optional line under the heading (what the structure is).</summary>
	public static readonly StyledProperty<string> SubtitleProperty =
		AvaloniaProperty.Register<ResultHero, string>(nameof(Subtitle));

	/// <summary>An optional small label over the figure ("Percent damaged"), for when there is no heading.</summary>
	public static readonly StyledProperty<string> LabelProperty =
		AvaloniaProperty.Register<ResultHero, string>(nameof(Label));

	/// <summary>The figure, for example "75.0%".</summary>
	public static readonly StyledProperty<string> PercentTextProperty =
		AvaloniaProperty.Register<ResultHero, string>(nameof(PercentText));

	/// <summary>Whether there is a percentage to show (not while the assessment is incomplete or undetermined).</summary>
	public static readonly StyledProperty<bool> HasPercentProperty =
		AvaloniaProperty.Register<ResultHero, bool>(nameof(HasPercent));

	/// <summary>The determination in words, for example "Substantially Damaged".</summary>
	public static readonly StyledProperty<string> VerdictProperty =
		AvaloniaProperty.Register<ResultHero, string>(nameof(Verdict));

	/// <summary>True when the structure is substantially damaged: the determination is written in the frame's red.</summary>
	public static readonly StyledProperty<bool> IsSubstantialProperty =
		AvaloniaProperty.Register<ResultHero, bool>(nameof(IsSubstantial));

	/// <summary>Where the figure stands against the line, for example "25 points above the 50% line".</summary>
	public static readonly StyledProperty<string> LineTextProperty =
		AvaloniaProperty.Register<ResultHero, string>(nameof(LineText));

	/// <summary>The gauge's value, 0 to 100.</summary>
	public static readonly StyledProperty<double> ValueProperty =
		AvaloniaProperty.Register<ResultHero, double>(nameof(Value));

	/// <summary>An optional note under the gauge (what is missing before there is a result).</summary>
	public static readonly StyledProperty<string> HintProperty =
		AvaloniaProperty.Register<ResultHero, string>(nameof(Hint));

	/// <summary>Whether <see cref="Hint"/> is shown.</summary>
	public static readonly StyledProperty<bool> ShowHintProperty =
		AvaloniaProperty.Register<ResultHero, bool>(nameof(ShowHint));

	static ResultHero()
	{
		IsSubstantialProperty.Changed.AddClassHandler<ResultHero>((hero, _) => hero.PseudoClasses.Set(":substantial", hero.IsSubstantial));
	}

	/// <inheritdoc cref="TitleProperty"/>
	public string Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	/// <inheritdoc cref="SubtitleProperty"/>
	public string Subtitle
	{
		get => GetValue(SubtitleProperty);
		set => SetValue(SubtitleProperty, value);
	}

	/// <inheritdoc cref="LabelProperty"/>
	public string Label
	{
		get => GetValue(LabelProperty);
		set => SetValue(LabelProperty, value);
	}

	/// <inheritdoc cref="PercentTextProperty"/>
	public string PercentText
	{
		get => GetValue(PercentTextProperty);
		set => SetValue(PercentTextProperty, value);
	}

	/// <inheritdoc cref="HasPercentProperty"/>
	public bool HasPercent
	{
		get => GetValue(HasPercentProperty);
		set => SetValue(HasPercentProperty, value);
	}

	/// <inheritdoc cref="VerdictProperty"/>
	public string Verdict
	{
		get => GetValue(VerdictProperty);
		set => SetValue(VerdictProperty, value);
	}

	/// <inheritdoc cref="IsSubstantialProperty"/>
	public bool IsSubstantial
	{
		get => GetValue(IsSubstantialProperty);
		set => SetValue(IsSubstantialProperty, value);
	}

	/// <inheritdoc cref="LineTextProperty"/>
	public string LineText
	{
		get => GetValue(LineTextProperty);
		set => SetValue(LineTextProperty, value);
	}

	/// <inheritdoc cref="ValueProperty"/>
	public double Value
	{
		get => GetValue(ValueProperty);
		set => SetValue(ValueProperty, value);
	}

	/// <inheritdoc cref="HintProperty"/>
	public string Hint
	{
		get => GetValue(HintProperty);
		set => SetValue(HintProperty, value);
	}

	/// <inheritdoc cref="ShowHintProperty"/>
	public bool ShowHint
	{
		get => GetValue(ShowHintProperty);
		set => SetValue(ShowHintProperty, value);
	}
}
