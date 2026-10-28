using System.Diagnostics;

namespace Sde.App.Services;

/// <summary>
/// iPhones save photos as HEIC, which FEMA's SDE (Windows), the PDF reports and the thumbnails can't read. Such photos
/// are converted to JPEG with macOS's built-in <c>sips</c> as they are added; other formats are kept as they are.
/// </summary>
public static class PhotoConversion
{
	private static readonly string[] Convertible = { ".heic", ".heif" };

	/// <summary>How long one conversion, or the wait for a turn, may take.</summary>
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

	/// <summary>
	/// A few conversions at a time: the phone form converts on web-server threads, so many uploads at once mustn't
	/// start many copies of sips.
	/// </summary>
	private static readonly SemaphoreSlim Slots = new SemaphoreSlim(2);

	/// <summary>
	/// The file to store: <paramref name="path"/> itself, or a JPEG copy in <paramref name="tempFolder"/>.
	/// Throws <see cref="IOException"/> when a conversion fails.
	/// </summary>
	public static string Compatible(string path, string tempFolder)
	{
		if (!Convertible.Contains(Path.GetExtension(path).ToLowerInvariant()))
		{
			return path;
		}
		Directory.CreateDirectory(tempFolder);
		string jpeg = Path.Combine(tempFolder, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N")[..8] + ".jpg");
		ProcessStartInfo psi = new ProcessStartInfo("/usr/bin/sips") { RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (string a in new[] { "-s", "format", "jpeg", "-s", "formatOptions", "90", path, "--out", jpeg })
		{
			psi.ArgumentList.Add(a);
		}
		if (!Slots.Wait(Timeout))
		{
			throw new IOException($"Couldn't convert {Path.GetFileName(path)} to JPEG: the Mac is busy converting other photos. Try again.");
		}
		try
		{
			using Process p = Process.Start(psi) ?? throw new IOException("Couldn't start sips to convert " + Path.GetFileName(path));
			// Both streams are read as sips writes them, so neither can fill up and stall it, and the wait can time out.
			Task<string> output = p.StandardOutput.ReadToEndAsync();
			Task<string> error = p.StandardError.ReadToEndAsync();
			if (!p.WaitForExit(Timeout))
			{
				try
				{
					p.Kill(entireProcessTree: true);
				}
				catch (InvalidOperationException)
				{
					// It ended just now.
				}
				File.Delete(jpeg);
				throw new IOException($"Couldn't convert {Path.GetFileName(path)} to JPEG: it took longer than a minute.");
			}
			p.WaitForExit();
			_ = output.Result;
			if (p.ExitCode != 0 || !File.Exists(jpeg))
			{
				throw new IOException($"Couldn't convert {Path.GetFileName(path)} to JPEG. {error.Result.Trim()}");
			}
			return jpeg;
		}
		finally
		{
			Slots.Release();
		}
	}
}
