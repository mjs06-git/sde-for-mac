using System.Buffers.Binary;
using System.Text;

namespace Sde.Field;

/// <summary>Checks that an uploaded file really is a photo SDE can keep, by its content rather than its name.</summary>
public static class PhotoUpload
{
	/// <summary>The largest photo accepted (25 MB).</summary>
	public const long MaxBytes = 25L * 1024 * 1024;

	/// <summary>
	/// The largest picture accepted, in pixels: well above a phone camera's 48 megapixels, and small enough that
	/// decoding it for a thumbnail or report can't run the Mac out of memory (a small file can declare any size).
	/// </summary>
	public const long MaxPixels = 100_000_000;

	private static ReadOnlySpan<byte> PngMagic => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

	private static readonly string[] HeifBrands = { "heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1" };

	/// <summary>
	/// The extension to store a photo under (".jpg", ".png" or ".heic"), or null when it isn't one. The first bytes say
	/// which kind the file claims to be; a JPEG or PNG must then also describe a picture with a width and height in its
	/// own structure no larger than <see cref="MaxPixels"/>, so a file that only starts like one is refused (a HEIC is
	/// checked when the Mac converts it).
	/// <paramref name="s"/> must be seekable.
	/// </summary>
	public static string Detect(Stream s)
	{
		Span<byte> head = stackalloc byte[24];
		s.Position = 0;
		int read = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
		ReadOnlySpan<byte> slice = head.Slice(0, read);
		string extension = DetectExtension(slice);
		return extension switch
		{
			".jpg" => IsJpeg(s) ? extension : null,
			".png" => IsPng(slice) ? extension : null,
			_ => extension,
		};
	}

	/// <summary>Walks a JPEG's segments from the start to its frame header (SOFn), which gives the picture's size.</summary>
	private static bool IsJpeg(Stream s)
	{
		s.Position = 2;   // after the start-of-image marker
		for (int segments = 0; segments < 1000; segments++)
		{
			if (s.ReadByte() != 0xFF)
			{
				return false;
			}
			int marker;
			do
			{
				marker = s.ReadByte();
			}
			while (marker == 0xFF);   // fill bytes
			if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
			{
				continue;   // markers with no length
			}
			if (marker < 0 || marker == 0xD9 || marker == 0xDA)
			{
				return false;   // the file ended, or the image data began, before a frame header
			}
			int length = ReadUInt16(s);
			if (length < 2 || s.Position + length - 2 > s.Length)
			{
				return false;
			}
			if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
			{
				s.ReadByte();   // sample precision
				int height = ReadUInt16(s);
				int width = ReadUInt16(s);
				return length >= 8 && height > 0 && width > 0 && (long)width * height <= MaxPixels;
			}
			s.Position += length - 2;
		}
		return false;
	}

	/// <summary>A PNG's first chunk must be its header (IHDR, 13 bytes) with a width and height.</summary>
	private static bool IsPng(ReadOnlySpan<byte> head)
	{
		if (head.Length < 24
			|| BinaryPrimitives.ReadUInt32BigEndian(head[8..12]) != 13
			|| !head[12..16].SequenceEqual("IHDR"u8))
		{
			return false;
		}
		uint width = BinaryPrimitives.ReadUInt32BigEndian(head[16..20]);
		uint height = BinaryPrimitives.ReadUInt32BigEndian(head[20..24]);
		return width > 0 && height > 0 && (ulong)width * height <= MaxPixels;
	}

	private static int ReadUInt16(Stream s)
	{
		int high = s.ReadByte();
		int low = s.ReadByte();
		return high < 0 || low < 0 ? -1 : high << 8 | low;
	}

	/// <summary>
	/// The extension to store a photo under (".jpg", ".png" or ".heic"), from its first bytes, or null when it isn't a
	/// JPEG, PNG or HEIC/HEIF image.
	/// </summary>
	public static string DetectExtension(ReadOnlySpan<byte> head)
	{
		if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
		{
			return ".jpg";
		}
		if (head.Length >= 8 && head[..8].SequenceEqual(PngMagic))
		{
			return ".png";
		}
		// ISO base media: a box size, then "ftyp" and the major brand.
		if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
		{
			Span<char> brand = stackalloc char[4];
			for (int i = 0; i < 4; i++)
			{
				brand[i] = char.ToLowerInvariant((char)head[8 + i]);
			}
			for (int i = 0; i < HeifBrands.Length; i++)
			{
				if (brand.Equals(HeifBrands[i].AsSpan(), StringComparison.Ordinal))
				{
					return ".heic";
				}
			}
		}
		return null;
	}
}
