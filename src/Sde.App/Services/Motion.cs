namespace Sde.App.Services;

/// <summary>
/// Whether the app animates (sheets sliding in, the dimming behind them, hover fades). Off when the Mac's Reduce
/// Motion is on, and in the off-screen tests so a screenshot never catches a frame half-way through. Styles animate
/// only inside a window whose root carries the "motion" class, which MainWindow sets from <see cref="Enabled"/>.
/// </summary>
public static class Motion
{
	public const string Class = "motion";

	public static bool Enabled { get; set; } = true;
}
