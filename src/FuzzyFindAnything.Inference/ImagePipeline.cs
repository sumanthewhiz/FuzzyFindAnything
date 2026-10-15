using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.Inference;

public sealed record RgbImage(int Width, int Height, byte[] Pixels);
public sealed record PreparedImage(RgbImage Canonical, RgbImage Resized, float[] Pixels, long[] Positions, long[] InputIds);

public static class ImagePipeline
{
    private const long MaximumPixels = 89_478_485;

    public static PreparedImage Prepare(string path)
    {
        RgbImage image = Decode(path);
        if (image.Width > 2048 || image.Height > 2048)
        {
            double scale = Math.Min(2048d / image.Width, 2048d / image.Height);
            image = Resize(image, Math.Max(1, (int)Math.Round(image.Width * scale)),
                Math.Max(1, (int)Math.Round(image.Height * scale)));
        }
        var (width, height) = TargetSize(image.Width, image.Height);
        RgbImage resized = Resize(image, width, height);
        int columns = width / 16, rows = height / 16, patches = columns * rows;
        float[] pixels = new float[2520 * 768];
        long[] positions = Enumerable.Repeat(-1L, 2520 * 2).ToArray();
        for (int py = 0; py < rows; py++)
            for (int px = 0; px < columns; px++)
            {
                int patch = py * columns + px;
                positions[patch * 2] = px;
                positions[patch * 2 + 1] = py;
                for (int iy = 0; iy < 16; iy++)
                    for (int ix = 0; ix < 16; ix++)
                        for (int channel = 0; channel < 3; channel++)
                            pixels[patch * 768 + (iy * 16 + ix) * 3 + channel] =
                                resized.Pixels[((py * 16 + iy) * width + px * 16 + ix) * 3 + channel] * (1f / 255f);
            }
        long[] ids = new long[patches / 9 + 4];
        ids[0] = 2; ids[1] = 255999; ids[^2] = 258882; ids[^1] = 1;
        Array.Fill(ids, 258880L, 2, patches / 9);
        return new(image, resized, pixels, positions, ids);
    }

    public static (int Width, int Height) TargetSize(int width, int height)
    {
        if (width < 1 || height < 1) throw new ContentException("The image has invalid dimensions.", "invalid_image");
        double scale = Math.Sqrt(2520d * 16 * 16 / ((double)width * height));
        int targetHeight = (int)Math.Floor(height * scale / 48) * 48;
        int targetWidth = (int)Math.Floor(width * scale / 48) * 48;
        if (targetHeight == 0) { targetHeight = 48; targetWidth = Math.Min((int)Math.Floor((double)width / height) * 48, 13440); }
        else if (targetWidth == 0) { targetWidth = 48; targetHeight = Math.Min((int)Math.Floor((double)height / width) * 48, 13440); }
        if ((long)targetWidth * targetHeight > 2520 * 256)
            throw new ContentException("The image exceeds the model patch budget.", "resource_limit");
        return (targetWidth, targetHeight);
    }

    private static RgbImage Decode(string path)
    {
        using var stream = FilePaths.OpenRead(path);
        BitmapFrame frame;
        if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
                throw new ContentException("Invalid ICO header.", "invalid_image");
            int count = reader.ReadUInt16(), bestArea = -1, bestBits = -1;
            uint bestOffset = 0, bestLength = 0;
            if (count is < 1 or > 1024 || stream.Length < 6 + count * 16)
                throw new ContentException("Invalid ICO directory.", "invalid_image");
            for (int i = 0; i < count; i++)
            {
                int width = reader.ReadByte(), height = reader.ReadByte();
                reader.ReadBytes(2);
                reader.ReadUInt16();
                int bits = reader.ReadUInt16();
                uint length = reader.ReadUInt32(), offset = reader.ReadUInt32();
                int area = (width == 0 ? 256 : width) * (height == 0 ? 256 : height);
                if (area > bestArea || area == bestArea && bits > bestBits)
                { bestArea = area; bestBits = bits; bestOffset = offset; bestLength = length; }
            }
            if (bestLength == 0 || bestLength > 64 * 1024 * 1024 || bestOffset + (long)bestLength > stream.Length)
                throw new ContentException("Invalid ICO image bounds.", "invalid_image");
            stream.Position = bestOffset;
            byte[] encoded = reader.ReadBytes(checked((int)bestLength));
            if (encoded.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                using var png = new MemoryStream(encoded, writable: false);
                frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            }
            else
            {
                stream.Position = 0;
                frame = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
                    .Frames.MaxBy(item => (long)item.PixelWidth * item.PixelHeight)!;
            }
        }
        else
        {
            frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
        }
        int widthValue = frame.PixelWidth, heightValue = frame.PixelHeight;
        if (widthValue < 1 || heightValue < 1 || (long)widthValue * heightValue > MaximumPixels)
            throw new ContentException("The decoded image exceeds the pixel safety limit.", "resource_limit");
        int orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
            orientation = Convert.ToInt32(metadata.GetQuery("/app1/ifd/{ushort=274}"));
        var image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] bgra = new byte[checked(widthValue * heightValue * 4)];
        image.CopyPixels(bgra, checked(widthValue * 4), 0);
        byte[] rgb = new byte[checked(widthValue * heightValue * 3)];
        for (int i = 0; i < widthValue * heightValue; i++)
        {
            rgb[i * 3] = bgra[i * 4 + 2];
            rgb[i * 3 + 1] = bgra[i * 4 + 1];
            rgb[i * 3 + 2] = bgra[i * 4];
        }
        if (orientation <= 1 || orientation > 8) return new(widthValue, heightValue, rgb);
        int outputWidth = orientation >= 5 ? heightValue : widthValue;
        int outputHeight = orientation >= 5 ? widthValue : heightValue;
        byte[] output = new byte[rgb.Length];
        for (int y = 0; y < outputHeight; y++)
            for (int x = 0; x < outputWidth; x++)
            {
                var (sx, sy) = orientation switch
                {
                    2 => (widthValue - 1 - x, y),
                    3 => (widthValue - 1 - x, heightValue - 1 - y),
                    4 => (x, heightValue - 1 - y),
                    5 => (y, x),
                    6 => (y, heightValue - 1 - x),
                    7 => (widthValue - 1 - y, heightValue - 1 - x),
                    8 => (widthValue - 1 - y, x),
                    _ => (x, y)
                };
                Buffer.BlockCopy(rgb, (sy * widthValue + sx) * 3, output, (y * outputWidth + x) * 3, 3);
            }
        return new(outputWidth, outputHeight, output);
    }

    private sealed record Weights(int Start, int[] Values);
    private static Weights[] Coefficients(int input, int output)
    {
        double scale = (double)input / output, filterScale = Math.Max(1, scale), support = 2 * filterScale;
        var result = new Weights[output];
        for (int i = 0; i < output; i++)
        {
            double center = (i + 0.5) * scale;
            int start = Math.Max(0, (int)Math.Floor(center - support + 0.5));
            int end = Math.Min(input, (int)Math.Floor(center + support + 0.5));
            double[] weights = Enumerable.Range(start, end - start).Select(x => Cubic((x - center + 0.5) / filterScale)).ToArray();
            double total = weights.Sum();
            int[] integers = weights.Select(value =>
            {
                double scaled = value / total * (1 << 22);
                return (int)(scaled + (scaled < 0 ? -0.5 : 0.5));
            }).ToArray();
            result[i] = new(start, integers);
        }
        return result;
    }
    private static double Cubic(double value)
    {
        double x = Math.Abs(value);
        if (x < 1) return ((1.5 * x - 2.5) * x) * x + 1;
        return x < 2 ? ((-0.5 * x + 2.5) * x - 4) * x + 2 : 0;
    }
    public static RgbImage Resize(RgbImage input, int width, int height)
    {
        if (width == input.Width && height == input.Height) return input;
        byte[] horizontal = new byte[checked(width * input.Height * 3)];
        Weights[] wx = Coefficients(input.Width, width);
        for (int y = 0; y < input.Height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++)
                {
                    long total = 1 << 21;
                    Weights weights = wx[x];
                    for (int i = 0; i < weights.Values.Length; i++)
                        total += (long)input.Pixels[(y * input.Width + weights.Start + i) * 3 + c] * weights.Values[i];
                    horizontal[(y * width + x) * 3 + c] = (byte)Math.Clamp(total >> 22, 0, 255);
                }
        byte[] output = new byte[checked(width * height * 3)];
        Weights[] wy = Coefficients(input.Height, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++)
                {
                    long total = 1 << 21;
                    Weights weights = wy[y];
                    for (int i = 0; i < weights.Values.Length; i++)
                        total += (long)horizontal[((weights.Start + i) * width + x) * 3 + c] * weights.Values[i];
                    output[(y * width + x) * 3 + c] = (byte)Math.Clamp(total >> 22, 0, 255);
                }
        return new(width, height, output);
    }
}
