using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Models;
using Poker.Advisor.Core.Recommendations;
using Poker.Advisor.Services;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace Poker.Advisor;

public partial class MainWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolwindow = 0x00000080;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _timer;
    private readonly TableStateMachine _tableStateMachine = new();
    private readonly AmountOcrService _amountOcr = new();
    private readonly HeroRecommendationService _recommendations = HeroRecommendationService.CreateDefault();
    private PythonPredictor? _predictor;
    private CancellationTokenSource? _loopCts;
    private Canvas _frontCanvas = null!;
    private Canvas _backCanvas = null!;
    private int _isBusy;
    private bool _isPaused;
    private string _status = "Starting predictor...";

    public MainWindow()
    {
        InitializeComponent();
        _frontCanvas = OverlayCanvasA;
        _backCanvas = OverlayCanvasB;
        _timer = new DispatcherTimer { Interval = CaptureInterval };
        _timer.Tick += async (_, _) => await CaptureAndPredictAsync();
        Loaded += OnLoaded;
        Closed += OnClosed;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                EnsureTopmost();
            }
        };
        Deactivated += (_, _) => EnsureTopmost();
    }

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        if (paused)
        {
            OverlayCanvasA.Children.Clear();
            OverlayCanvasB.Children.Clear();
            TableStateText.Text = "paused";
            StatusText.Text = "Paused";
            Hide();
        }
        else
        {
            StatusText.Text = _status;
            Show();
            EnsureTopmost();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CoverPrimaryScreen();

        var hwnd = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(hwnd, GwlExstyle);
        _ = SetWindowLong(
            hwnd,
            GwlExstyle,
            extendedStyle | WsExTransparent | WsExLayered | WsExToolwindow);

        // Keep overlay visible to the user, but omit it from screen captures.
        _ = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        EnsureTopmost();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            CoverPrimaryScreen();
            EnsureTopmost();
            _loopCts = new CancellationTokenSource();
            StatusText.Text = "Loading YOLO model...";
            _predictor = await PythonPredictor.StartAsync(_loopCts.Token).ConfigureAwait(true);
            _status = "Live";
            StatusText.Text = _status;
            EnsureTopmost();
            _timer.Start();
        }
        catch (Exception ex)
        {
            _status = "Predictor failed";
            StatusText.Text = $"Predictor failed: {ex.Message}";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _loopCts?.Cancel();
        _predictor?.Dispose();
        _predictor = null;
        _amountOcr.Dispose();
    }

    private async Task CaptureAndPredictAsync()
    {
        if (_isPaused || _predictor is null)
        {
            return;
        }

        EnsureTopmost();

        if (Interlocked.Exchange(ref _isBusy, 1) == 1)
        {
            return;
        }

        try
        {
            // Previous boxes stay visible while capture + prediction run.
            using var frame = await Task.Run(() => ScreenCaptureService.CapturePrimaryScreen())
                .ConfigureAwait(true);
            var detections = await _predictor.PredictAsync(frame.Jpeg).ConfigureAwait(true);

            var tableStates = _tableStateMachine.Update(detections);
            if (_amountOcr.IsAvailable)
            {
                await _amountOcr.EnrichAsync(tableStates, detections, frame.Bitmap).ConfigureAwait(true);
            }

            _recommendations.Apply(tableStates);

            await Dispatcher.InvokeAsync(
                () =>
                {
                    EnsureTopmost();
                    DrawDetections(detections, frame.Bounds);
                    UpdateTableStatePanel(tableStates);
                    _status = detections.Count == 0
                        ? "Live · no detections"
                        : $"Live · {detections.Count} boxes · {tableStates.Count} table(s)";
                    if (!_amountOcr.IsAvailable)
                    {
                        _status += " · OCR unavailable";
                    }

                    StatusText.Text = _status;
                },
                DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _status = "Error";
                StatusText.Text = $"Error: {ex.Message}";
            });
        }
        finally
        {
            Interlocked.Exchange(ref _isBusy, 0);
        }
    }

    private void CoverPrimaryScreen()
    {
        var screen = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Primary screen is not available.");

        var dpi = VisualTreeHelper.GetDpi(this);
        WindowState = WindowState.Normal;
        Left = screen.Left / dpi.DpiScaleX;
        Top = screen.Top / dpi.DpiScaleY;
        Width = screen.Width / dpi.DpiScaleX;
        Height = screen.Height / dpi.DpiScaleY;
    }

    private void EnsureTopmost()
    {
        if (!IsVisible)
        {
            return;
        }

        Topmost = false;
        Topmost = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            hwnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    private void UpdateTableStatePanel(IReadOnlyList<TableState> tableStates)
    {
        TableStateText.Text = null;
        TableStateText.Inlines.Clear();

        if (tableStates.Count == 0)
        {
            TableStateText.Text = "table: none detected";
            return;
        }

        var normalBrush = new SolidColorBrush(MediaColor.FromRgb(0xF0, 0xF0, 0xF0));
        var foldBrush = new SolidColorBrush(MediaColor.FromRgb(0x9A, 0x9A, 0x9A));
        var raiseBrush = new SolidColorBrush(MediaColor.FromRgb(0x39, 0xFF, 0x14)); // bright green
        var callBrush = new SolidColorBrush(MediaColor.FromRgb(0x22, 0xC5, 0x5E)); // green
        var adviceFoldBrush = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0x3B, 0x30)); // red
        var checkBrush = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xD6, 0x0A)); // yellow
        normalBrush.Freeze();
        foldBrush.Freeze();
        raiseBrush.Freeze();
        callBrush.Freeze();
        adviceFoldBrush.Freeze();
        checkBrush.Freeze();

        for (var t = 0; t < tableStates.Count; t++)
        {
            if (t > 0)
            {
                TableStateText.Inlines.Add(new System.Windows.Documents.Run("\n\n") { Foreground = normalBrush });
            }

            var lines = tableStates[t].FormatPanelLines().ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var suffix = i < lines.Count - 1 ? "\n" : string.Empty;
                var brush = line.Tone switch
                {
                    PanelTone.Fold => foldBrush,
                    PanelTone.AdviceRaise => raiseBrush,
                    PanelTone.AdviceCall => callBrush,
                    PanelTone.AdviceFold => adviceFoldBrush,
                    PanelTone.AdviceCheck => checkBrush,
                    _ => normalBrush,
                };
                TableStateText.Inlines.Add(
                    new System.Windows.Documents.Run(line.Text + suffix)
                    {
                        Foreground = brush,
                    });
            }
        }
    }

    private void DrawDetections(IReadOnlyList<Detection> detections, System.Drawing.Rectangle screenBounds)
    {
        _ = screenBounds;
        var dpi = VisualTreeHelper.GetDpi(this);

        _backCanvas.Children.Clear();
        foreach (var detection in detections)
        {
            // Capture and overlay both cover the primary screen, so map image pixels -> DIPs directly.
            var x = detection.X1 / dpi.DpiScaleX;
            var y = detection.Y1 / dpi.DpiScaleY;
            var width = Math.Max(2, (detection.X2 - detection.X1) / dpi.DpiScaleX);
            var height = Math.Max(2, (detection.Y2 - detection.Y1) / dpi.DpiScaleY);
            var stroke = BrushFor(detection.Name);

            var rect = new WpfRectangle
            {
                Width = width,
                Height = height,
                Stroke = stroke,
                StrokeThickness = 2,
                Fill = MediaBrushes.Transparent,
                SnapsToDevicePixels = true,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            _backCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = $"{detection.Name} {detection.Confidence:0.00}",
                Foreground = MediaBrushes.Black,
                Background = stroke,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(2, 0, 2, 0),
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, Math.Max(0, y - 14));
            _backCanvas.Children.Add(label);
        }

        // Swap buffers in one step so the old frame stays until the new one is ready.
        _backCanvas.Visibility = Visibility.Visible;
        _frontCanvas.Visibility = Visibility.Collapsed;
        (_frontCanvas, _backCanvas) = (_backCanvas, _frontCanvas);

        // Force a layout pass so boxes appear immediately.
        _frontCanvas.InvalidateVisual();
        UpdateLayout();
    }

    private static System.Windows.Media.Brush BrushFor(string name)
    {
        MediaColor color;
        if (name == "table" || name.StartsWith("table_", StringComparison.Ordinal))
        {
            color = MediaColor.FromRgb(0, 160, 255);
        }
        else if (name == "active_player")
        {
            color = MediaColor.FromRgb(255, 180, 0);
        }
        else if (name == "fold_player")
        {
            color = MediaColor.FromRgb(255, 40, 40);
        }
        else if (name == "dealer")
        {
            color = MediaColor.FromRgb(255, 140, 0);
        }
        else
        {
            color = MediaColor.FromRgb(0, 220, 0);
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
