using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AsciiLandsWindMap;

public static class ChartImage
{
    public static BitmapSource? Bitmap(byte[] png)
    {
        var pixels = Pixels(png, out var width, out var height, out var stride);
        if (pixels == null)
            return null;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    public static byte[]? Pixels(byte[] png, out int width, out int height, out int stride)
    {
        width = 0;
        height = 0;
        stride = 0;
        try
        {
            using var stream = new MemoryStream(png);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;
            var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            width = converted.PixelWidth;
            height = converted.PixelHeight;
            stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
