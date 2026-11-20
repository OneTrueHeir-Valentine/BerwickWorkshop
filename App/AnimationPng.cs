using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Berwick.Core;

namespace Berwick.Workshop;

public static class AnimationPng
{
    public static void Write(Stream stream, AnimationImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, checked(image.Width * 4));
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
