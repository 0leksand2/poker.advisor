using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;

namespace Poker.Advisor.Services;

/// <summary>
/// Continuously records the primary screen into an in-memory ring of JPEG frames.
/// Frames older than <see cref="Retention"/> are dropped (default 10 seconds).
/// The latest frame also keeps a raw Bitmap clone for accurate OCR (no JPEG round-trip).
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    private readonly ConcurrentQueue<BufferedFrame> _buffer = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private BufferedFrame? _latest;
    private Bitmap? _latestBitmap;
    private long _jpegBytes;
    private bool _disposed;

    public ScreenRecorder(
        TimeSpan? retention = null,
        TimeSpan? minCaptureInterval = null,
        long jpegQuality = 75L)
    {
        Retention = retention ?? TimeSpan.FromSeconds(10);
        MinCaptureInterval = minCaptureInterval ?? TimeSpan.FromMilliseconds(33); // ~30 fps cap
        JpegQuality = jpegQuality;
    }

    public TimeSpan Retention { get; }
    public TimeSpan MinCaptureInterval { get; }
    public long JpegQuality { get; }
    public bool IsRunning { get; private set; }
    public int BufferedFrameCount => _buffer.Count;
    public long BufferedJpegBytes => Interlocked.Read(ref _jpegBytes);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (IsRunning)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            IsRunning = true;
            _loop = Task.Factory.StartNew(
                () => CaptureLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Stop()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore cancel/aggregate
        }

        cts?.Dispose();
        ClearBuffer();
    }

    /// <summary>
    /// Latest frame for analysis. Bitmap is a clone of the raw capture (not JPEG-decoded).
    /// </summary>
    public ScreenFrame? TryGetLatestFrame()
    {
        byte[] jpeg;
        Rectangle bounds;
        DateTime timestamp;
        Bitmap bitmapClone;

        lock (_gate)
        {
            if (_latest is null || _latestBitmap is null)
            {
                return null;
            }

            jpeg = _latest.Jpeg;
            bounds = _latest.Bounds;
            timestamp = _latest.TimestampUtc;
            bitmapClone = CloneBitmap(_latestBitmap);
        }

        return new ScreenFrame
        {
            Jpeg = jpeg,
            Bounds = bounds,
            Bitmap = bitmapClone,
            TimestampUtc = timestamp,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            try
            {
                using var grabbed = ScreenCaptureService.CapturePrimaryScreen(JpegQuality);
                var buffered = new BufferedFrame(
                    grabbed.Jpeg,
                    grabbed.Bounds,
                    grabbed.TimestampUtc);

                // Keep a raw clone for OCR; JPEG alone loses grey stack digits.
                var rawClone = CloneBitmap(grabbed.Bitmap);
                Enqueue(buffered, rawClone);
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                SleepRemaining(started, TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            SleepRemaining(started, MinCaptureInterval, cancellationToken);
        }
    }

    private void Enqueue(BufferedFrame frame, Bitmap rawClone)
    {
        _buffer.Enqueue(frame);
        Interlocked.Add(ref _jpegBytes, frame.Jpeg.LongLength);

        Bitmap? oldBitmap;
        lock (_gate)
        {
            _latest = frame;
            oldBitmap = _latestBitmap;
            _latestBitmap = rawClone;
        }

        oldBitmap?.Dispose();
        Prune();
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Retention;
        while (_buffer.TryPeek(out var oldest) && oldest.TimestampUtc < cutoff)
        {
            if (!_buffer.TryDequeue(out var removed))
            {
                break;
            }

            Interlocked.Add(ref _jpegBytes, -removed.Jpeg.LongLength);
        }

        const int hardCap = 350;
        while (_buffer.Count > hardCap && _buffer.TryDequeue(out var removed))
        {
            Interlocked.Add(ref _jpegBytes, -removed.Jpeg.LongLength);
        }
    }

    private void ClearBuffer()
    {
        while (_buffer.TryDequeue(out var removed))
        {
            Interlocked.Add(ref _jpegBytes, -removed.Jpeg.LongLength);
        }

        Bitmap? oldBitmap;
        lock (_gate)
        {
            _latest = null;
            oldBitmap = _latestBitmap;
            _latestBitmap = null;
        }

        oldBitmap?.Dispose();
        Interlocked.Exchange(ref _jpegBytes, 0);
    }

    private static Bitmap CloneBitmap(Bitmap source)
    {
        var clone = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(clone);
        g.DrawImageUnscaled(source, 0, 0);
        return clone;
    }

    private static void SleepRemaining(long startedTick, TimeSpan interval, CancellationToken cancellationToken)
    {
        var elapsed = Environment.TickCount64 - startedTick;
        var remaining = (int)interval.TotalMilliseconds - (int)elapsed;
        if (remaining <= 0)
        {
            return;
        }

        try
        {
            Task.Delay(remaining, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // exit
        }
    }

    private sealed class BufferedFrame(byte[] jpeg, Rectangle bounds, DateTime timestampUtc)
    {
        public byte[] Jpeg { get; } = jpeg;
        public Rectangle Bounds { get; } = bounds;
        public DateTime TimestampUtc { get; } = timestampUtc;
    }
}
