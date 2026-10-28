using SkiaSharp;
using Svg.Skia;

// IconRender <svg> <png> <width>: draws the SVG at <width> pixels wide (the height follows the SVG's aspect).
if (args.Length != 3 || !int.TryParse(args[2], out int width))
{
	Console.Error.WriteLine("usage: IconRender <svg> <png> <width>");
	return 2;
}
SKSvg svg = new();
svg.Load(args[0]);
SKPicture picture = svg.Picture ?? throw new InvalidOperationException($"Could not read {args[0]}");
float scale = width / picture.CullRect.Width;
int height = (int)Math.Round(picture.CullRect.Height * scale);
using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
surface.Canvas.Clear(SKColors.Transparent);
surface.Canvas.Scale(scale);
surface.Canvas.DrawPicture(picture);
using SKImage image = surface.Snapshot();
using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
File.WriteAllBytes(args[1], data.ToArray());
Console.WriteLine($"{args[1]} ({width} x {height})");
return 0;
