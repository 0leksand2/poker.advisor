using System.Drawing;
using System.Drawing.Imaging;

namespace Poker.Advisor.Services;

public static class BitmapPngExtensions
{
    public static MemoryStream SaveToPngStream(this Bitmap bitmap)
    {
        var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        return stream;
    }
}
