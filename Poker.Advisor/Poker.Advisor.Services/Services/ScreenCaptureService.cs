using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Poker.Advisor.Services;

public sealed class ScreenFrame : IDisposable
{
    public required byte[] Jpeg { get; init; }
    public required Rectangle Bounds { get; init; }
    public required Bitmap Bitmap { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Single-shot primary-screen grab (used by the continuous recorder).
/// </summary>
public static class ScreenCaptureService
{
    public static ScreenFrame CapturePrimaryScreen(long quality = 75L)
    {
        var bounds = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Primary screen is not available.");

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            using var stream = new MemoryStream();
            var encoder = GetJpegEncoder();
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(stream, encoder, encoderParams);

            return new ScreenFrame
            {
                Jpeg = stream.ToArray(),
                Bounds = bounds,
                Bitmap = bitmap,
                TimestampUtc = DateTime.UtcNow,
            };
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public static Bitmap DecodeJpeg(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg, writable: false);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static ImageCodecInfo GetJpegEncoder() =>
        ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
}
