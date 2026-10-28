using System.Runtime.CompilerServices;

namespace Sde.TestSupport;

/// <summary>Runs before any test in the project: the process gets a home folder of its own (see <see cref="TestHome"/>).</summary>
internal static class IsolateHome
{
	[ModuleInitializer]
	internal static void Run() => TestHome.Isolate();
}
