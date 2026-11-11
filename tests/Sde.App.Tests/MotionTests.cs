using System.Xml.Linq;

namespace Sde.App.Tests;

/// <summary>
/// Reduce Motion: the app animates only under the window's "motion" class (Services/Motion), which is left off when the
/// Mac's Reduce Motion is on and in the tests. So every transition and animation in the styles must sit in a style whose
/// selector (or an enclosing style's) starts from ".motion"; one anywhere else would still move with Reduce Motion on.
/// </summary>
public class MotionTests
{
	private static IEnumerable<string> XamlFiles()
	{
		string app = Path.Combine(TestPaths.Root, "src", "Sde.App");
		return Directory.GetFiles(Path.Combine(app, "Styles"), "*.axaml")
			.Concat(Directory.GetFiles(Path.Combine(app, "Views"), "*.axaml"))
			.Append(Path.Combine(app, "App.axaml"))
			.OrderBy(f => f, StringComparer.Ordinal);
	}

	/// <summary>An element that makes something move: a Transitions list, an Animation, a style's animations, or a setter of a *Transitions property.</summary>
	private static bool Moves(XElement e) =>
		e.Name.LocalName is "Transitions" or "Animation" or "Style.Animations" ||
		e.Name.LocalName == "Setter" && ((string)e.Attribute("Property"))?.EndsWith("Transitions", StringComparison.Ordinal) == true;

	/// <summary>"Styles/Sheets.axaml:84 Animation" for every moving part with no ".motion" style round it.</summary>
	internal static List<string> UngatedMotion(IEnumerable<string> files)
	{
		List<string> found = new();
		foreach (string file in files)
		{
			XDocument doc = XDocument.Load(file, LoadOptions.SetLineInfo);
			foreach (XElement e in doc.Descendants().Where(Moves))
			{
				// A setter's own Transitions element is reported once, as the setter
				if (e.Name.LocalName == "Transitions" && e.Parent?.Name.LocalName == "Setter")
				{
					continue;
				}
				bool gated = e.Ancestors().Where(a => a.Name.LocalName == "Style")
					.Any(s => ((string)s.Attribute("Selector"))?.Split(',').All(part => part.Trim().StartsWith(".motion", StringComparison.Ordinal)) == true);
				if (!gated)
				{
					int line = ((System.Xml.IXmlLineInfo)e).LineNumber;
					found.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}:{line} {e.Name.LocalName} {(string)e.Attribute("Property")}".TrimEnd());
				}
			}
		}
		return found;
	}

	[Fact]
	public void EveryTransitionAndAnimation_IsUnderTheMotionClass()
	{
		List<string> found = UngatedMotion(XamlFiles());
		Assert.True(found.Count == 0, "Moves even with Reduce Motion on:\n" + string.Join("\n", found));
	}

	[Fact]
	public void TheStylesDoAnimate_SoTheCheckHasSomethingToCheck()
	{
		int moving = XamlFiles().Sum(f => XDocument.Load(f).Descendants().Count(Moves));
		Assert.True(moving >= 8, $"Only {moving} transitions and animations found");
	}

	/// <summary>The check itself: an animation outside ".motion" is caught, one inside is not.</summary>
	[Fact]
	public void TheCheck_CatchesAnAnimationOutsideTheMotionClass()
	{
		string file = Path.Combine(Path.GetTempPath(), $"motion-check-{Guid.NewGuid():N}.axaml");
		File.WriteAllText(file, """
			<Styles xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <Style Selector=".motion Button">
			    <Setter Property="Transitions"><Transitions><BrushTransition Property="Background" Duration="0:0:0.1" /></Transitions></Setter>
			  </Style>
			  <Style Selector="Border.sheet">
			    <Style.Animations><Animation Duration="0:0:0.2" /></Style.Animations>
			  </Style>
			  <ControlTheme x:Key="Switch" TargetType="ToggleSwitch">
			    <Setter Property="KnobTransitions"><Transitions /></Setter>
			  </ControlTheme>
			</Styles>
			""");
		try
		{
			List<string> found = UngatedMotion(new[] { file });
			Assert.Equal(3, found.Count);                   // the style's animations, its Animation, and the knob's setter
			Assert.Contains(found, f => f.EndsWith("Setter KnobTransitions", StringComparison.Ordinal));
			Assert.DoesNotContain(found, f => f.Contains(":3 ", StringComparison.Ordinal));
		}
		finally
		{
			File.Delete(file);
		}
	}
}
