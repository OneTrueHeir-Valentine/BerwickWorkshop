using Berwick.Core;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PngTestImages
{
    public static AnimationImage Read(Stream stream)
    {
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    public static AnimationImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static void Equal(AnimationImage expected, AnimationImage actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height || !expected.Pixels.SequenceEqual(actual.Pixels))
            throw new Exception("PNG dimensions or BGRA pixels differ");
    }
}
