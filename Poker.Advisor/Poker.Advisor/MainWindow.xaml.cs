using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Poker.Advisor.Classes;
using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Extensions;
using Poker.Advisor.Core.Classes;
using Poker.Advisor.Core.Recommendations;
using Poker.Advisor.Enums;
using Poker.Advisor.Services;
using MediaColor = System.Windows.Media.Color;
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
    private const double StrokeThickness = 1.25;
    private const double BracketLenMax = 16;

    private readonly DispatcherTimer _timer;
    private readonly TableStateMachine _tableStateMachine = new();
    private readonly AmountOcrService _amountOcr = new();
    private readonly HeroRecommendationService _recommendations = HeroRecommendationService.CreateDefault();
    private readonly PlayerTracker _playerTracker = new();
    private readonly ScreenRecorder _screenRecorder = new(
        retention: TimeSpan.FromSeconds(10),
        minCaptureInterval: TimeSpan.FromMilliseconds(33));
    private PythonPredictor? _predictor;
    private CancellationTokenSource? _loopCts;
    private Canvas _frontCanvas = null!;
    private Canvas _backCanvas = null!;
    private int _isBusy;
    private bool _isPaused;
    private BoxDisplayMode _boxMode = BoxDisplayMode.Minimal;
    private string _status = "starting";

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
            _screenRecorder.Stop();
            ClearOverlayBoxes();
            AdvicePanelHost.Children.Clear();
            SetStatusUi("paused");
            Hide();
        }
        else
        {
            if (_predictor is not null)
            {
                _screenRecorder.Start();
            }

            SetStatusUi(_status);
            Show();
            EnsureTopmost();
        }
    }

    /// <summary>
    /// All / minimal / advice-only / off. Advice panel keeps updating either way.
    /// </summary>
    internal void SetBoxDisplayMode(BoxDisplayMode mode)
    {
        _boxMode = mode;
        if (mode is BoxDisplayMode.Off or BoxDisplayMode.AdviceOnly)
        {
            ClearOverlayBoxes();
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
            SetStatusUi("loading model…");
            _predictor = await PythonPredictor.StartAsync(_loopCts.Token).ConfigureAwait(true);
            _screenRecorder.Start();
            _status = "live";
            SetStatusUi(_status);
            EnsureTopmost();
            _timer.Start();
        }
        catch (Exception ex)
        {
            _status = "predictor failed";
            SetStatusUi($"predictor failed · {ex.Message}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _screenRecorder.Dispose();
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
            using var frame = await Task.Run(() => _screenRecorder.TryGetLatestFrame())
                .ConfigureAwait(true);
            if (frame is null)
            {
                return;
            }

            var detections = await _predictor.PredictAsync(frame.Jpeg).ConfigureAwait(true);

            var tableStates = _tableStateMachine.Update(detections);
            if (_amountOcr.IsAvailable)
            {
                await _amountOcr.EnrichAsync(tableStates, detections, frame.Bitmap).ConfigureAwait(true);
            }

            _playerTracker.Update(tableStates);
            _recommendations.Apply(tableStates);

            var buffered = _screenRecorder.BufferedFrameCount;
            var ageMs = (int)Math.Max(0, (DateTime.UtcNow - frame.TimestampUtc).TotalMilliseconds);

            await Dispatcher.InvokeAsync(
                () =>
                {
                    EnsureTopmost();
                    DrawOverlay(detections, tableStates);
                    UpdateAdvicePanel(tableStates);
                    _status = detections.Count == 0
                        ? $"live · 0 det · {tableStates.Count} tbl · {ageMs}ms"
                        : $"live · {detections.Count} det · {tableStates.Count} tbl · buf {buffered}";
                    if (!_amountOcr.IsAvailable)
                    {
                        _status += " · no ocr";
                    }

                    SetStatusUi(_status);
                },
                DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _status = "error";
                SetStatusUi($"error · {ex.Message}");
            });
        }
        finally
        {
            Interlocked.Exchange(ref _isBusy, 0);
        }
    }

    private void SetStatusUi(string text)
    {
        StatusText.Text = text;
        App.CurrentApp.UpdateTrayStatus($"Poker Advisor · {text}");
    }

    private void ClearOverlayBoxes()
    {
        OverlayCanvasA.Children.Clear();
        OverlayCanvasB.Children.Clear();
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

    private void UpdateAdvicePanel(IReadOnlyList<TableState> tableStates)
    {
        AdvicePanelHost.Children.Clear();

        var active = tableStates.Where(t => t.HasHeroCards).ToList();
        if (active.Count == 0)
        {
            AdvicePanelHost.Visibility = Visibility.Collapsed;
            return;
        }

        AdvicePanelHost.Visibility = Visibility.Visible;
        foreach (var table in active.OrderBy(ActionSortKey))
        {
            AdvicePanelHost.Children.Add(BuildAdviceCard(table));
        }
    }

    private static int ActionSortKey(TableState table) => table.Recommendation?.Action switch
    {
        RecommendedAction.Raise => 0,
        RecommendedAction.Call => 1,
        RecommendedAction.Check => 2,
        RecommendedAction.Fold => 3,
        RecommendedAction.Wait => 4,
        _ => 5,
    };

    private static Border BuildAdviceCard(TableState table)
    {
        var advice = table.Recommendation;
        var actionColor = advice is null
            ? OverlayPalette.TextMuted
            : OverlayPalette.ForAdvice(advice.Action);
        var actionText = advice?.ShortActionLabel() ?? "WAIT";
        var needsAction = advice is not null && IsPlayAction(advice.Action);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(needsAction ? 5 : 3) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new WpfRectangle
        {
            Fill = OverlayPalette.Brush(actionColor),
            RadiusX = 1,
            RadiusY = 1,
        };
        Grid.SetColumn(bar, 0);
        root.Children.Add(bar);

        var body = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(body, 1);
        root.Children.Add(body);

        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        header.Children.Add(new Border
        {
            Background = OverlayPalette.Brush(actionColor, needsAction ? (byte)0xFF : (byte)0x66),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"T{table.TableNumber}",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = OverlayPalette.Brush(MediaColor.FromRgb(0x0A, 0x0C, 0x10)),
            },
        });
        header.Children.Add(new TextBlock
        {
            Text = actionText,
            FontSize = needsAction ? 22 : 18,
            FontWeight = FontWeights.Bold,
            Foreground = OverlayPalette.Brush(actionColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(header);

        if (advice is not null && !string.IsNullOrWhiteSpace(advice.Summary))
        {
            body.Children.Add(new TextBlock
            {
                Text = advice.Summary,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = OverlayPalette.Brush(OverlayPalette.TextPrimary, 0xD0),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        var hero = table.PlayersClockwiseFromHero.FirstOrDefault();
        var heroCards = table.HeroCards.Count == 0 ? "?" : string.Join(" ", table.HeroCards);
        body.Children.Add(new TextBlock
        {
            Text = $"pot {FormatAmount(table.PotAmount, table.PotRawText)}  ·  hero {FormatAmount(hero?.BankAmount, hero?.BankRawText)}  ·  {heroCards}",
            FontSize = 11,
            Foreground = OverlayPalette.Brush(OverlayPalette.TextMuted),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var player in table.PlayersClockwiseFromHero.Skip(1))
        {
            var isFold = player.Status == PlayerStatus.Folded;
            var nick = string.IsNullOrWhiteSpace(player.Nickname)
                ? (player.Position.Length == 0 ? "?" : player.Position)
                : player.Nickname;
            var style = player.Stats is { Hands: > 0 } s ? s.StyleLabel : "?";
            var styleTone = isFold
                ? PanelTone.Fold
                : player.Stats is { Hands: > 0 } st ? st.StyleTone : PanelTone.Normal;

            var line = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            line.Inlines.Add(new System.Windows.Documents.Run($"{nick}: ")
            {
                Foreground = OverlayPalette.Brush(isFold ? OverlayPalette.TextFold : OverlayPalette.TextPrimary, 0xC0),
            });
            line.Inlines.Add(new System.Windows.Documents.Run(style)
            {
                Foreground = OverlayPalette.Brush(OverlayPalette.ForTone(styleTone)),
            });
            line.Inlines.Add(new System.Windows.Documents.Run(
                $" · {FormatAmount(player.BankAmount, player.BankRawText)} · {FormatAmount(player.BetAmount, player.BetRawText)}")
            {
                Foreground = OverlayPalette.Brush(isFold ? OverlayPalette.TextFold : OverlayPalette.TextMuted),
            });
            body.Children.Add(line);
        }

        return new Border
        {
            Child = root,
            Background = OverlayPalette.Brush(MediaColor.FromRgb(0x10, 0x14, 0x18), 0xB8),
            BorderBrush = OverlayPalette.Brush(actionColor, needsAction ? (byte)0xAA : (byte)0x33),
            BorderThickness = new Thickness(needsAction ? 2 : 1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(0, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            MaxWidth = 420,
        };
    }

    private void DrawOverlay(IReadOnlyList<Detection> detections, IReadOnlyList<TableState> tableStates)
    {
        if (_boxMode == BoxDisplayMode.Off)
        {
            ClearOverlayBoxes();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var byNumber = tableStates.ToDictionary(t => t.TableNumber);
        _backCanvas.Children.Clear();

        if (_boxMode is BoxDisplayMode.Minimal or BoxDisplayMode.All)
        {
            var source = _boxMode == BoxDisplayMode.Minimal
                ? detections.Where(d => d.IsMinimalOverlay())
                : detections;

            foreach (var detection in source)
            {
                var x = detection.X1 / dpi.DpiScaleX;
                var y = detection.Y1 / dpi.DpiScaleY;
                var width = Math.Max(2, (detection.X2 - detection.X1) / dpi.DpiScaleX);
                var height = Math.Max(2, (detection.Y2 - detection.Y1) / dpi.DpiScaleY);
                var color = ResolveBoxColor(detection, byNumber);

                AddCornerBrackets(_backCanvas, x, y, width, height, color);
                AddBoxLabel(_backCanvas, detection, x, y, color);
            }
        }

        // Pin action badges on tables (Advice Only also outlines the table in action color).
        var outlineTables = _boxMode == BoxDisplayMode.AdviceOnly;
        foreach (var table in tableStates.Where(t => t.HasHeroCards))
        {
            AddTableActionBadge(_backCanvas, table, dpi.DpiScaleX, dpi.DpiScaleY, outlineTables);
        }

        _backCanvas.Visibility = Visibility.Visible;
        _frontCanvas.Visibility = Visibility.Collapsed;
        (_frontCanvas, _backCanvas) = (_backCanvas, _frontCanvas);
        _frontCanvas.InvalidateVisual();
        UpdateLayout();
    }

    private static MediaColor ResolveBoxColor(Detection detection, IReadOnlyDictionary<int, TableState> byNumber)
    {
        if (detection.IsTable()
            && byNumber.TryGetValue(detection.TableNumber(), out var table)
            && table.Recommendation is { } advice
            && IsPlayAction(advice.Action))
        {
            return OverlayPalette.ForAdvice(advice.Action);
        }

        return OverlayPalette.ForDetection(detection);
    }

    private static bool IsPlayAction(RecommendedAction action) =>
        action is RecommendedAction.Raise
            or RecommendedAction.Call
            or RecommendedAction.Check
            or RecommendedAction.Fold;

    private static void AddTableActionBadge(
        Canvas canvas,
        TableState table,
        double dpiX,
        double dpiY,
        bool outlineTable)
    {
        var advice = table.Recommendation;
        if (advice is null || !IsPlayAction(advice.Action))
        {
            return;
        }

        var color = OverlayPalette.ForAdvice(advice.Action);
        var label = $"T{table.TableNumber}  {advice.ShortActionLabel()}";
        var x = table.Table.X1 / dpiX;
        var y = table.Table.Y1 / dpiY;
        var width = Math.Max(2, (table.Table.X2 - table.Table.X1) / dpiX);
        var height = Math.Max(2, (table.Table.Y2 - table.Table.Y1) / dpiY);

        if (outlineTable)
        {
            AddCornerBrackets(canvas, x, y, width, height, color);
        }

        var badge = new Border
        {
            Background = OverlayPalette.Brush(MediaColor.FromArgb(0x01, 0, 0, 0)),
            BorderBrush = OverlayPalette.Brush(color),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 3, 8, 4),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = OverlayPalette.Brush(color),
            },
        };

        Canvas.SetLeft(badge, x + 6);
        Canvas.SetTop(badge, Math.Max(0, y + 6));
        canvas.Children.Add(badge);
    }

    private static void AddCornerBrackets(
        Canvas canvas,
        double x,
        double y,
        double width,
        double height,
        MediaColor color)
    {
        var len = Math.Min(BracketLenMax, Math.Min(width, height) * 0.28);
        len = Math.Max(6, len);
        var stroke = OverlayPalette.Brush(color);
        var x2 = x + width;
        var y2 = y + height;

        void Seg(double x1, double y1, double xEnd, double yEnd)
        {
            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = xEnd,
                Y2 = yEnd,
                Stroke = stroke,
                StrokeThickness = StrokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                SnapsToDevicePixels = true,
            };
            canvas.Children.Add(line);
        }

        // Top-left
        Seg(x, y, x + len, y);
        Seg(x, y, x, y + len);
        // Top-right
        Seg(x2 - len, y, x2, y);
        Seg(x2, y, x2, y + len);
        // Bottom-left
        Seg(x, y2, x + len, y2);
        Seg(x, y2 - len, x, y2);
        // Bottom-right
        Seg(x2 - len, y2, x2, y2);
        Seg(x2, y2 - len, x2, y2);
    }

    private void AddBoxLabel(
        Canvas canvas,
        Detection detection,
        double x,
        double y,
        MediaColor color)
    {
        var text = _boxMode == BoxDisplayMode.Minimal
            ? OverlayPalette.ShortLabel(detection)
            : $"{OverlayPalette.ShortLabel(detection)} {detection.Confidence:0.00}";

        var label = new TextBlock
        {
            Text = text,
            Foreground = OverlayPalette.Brush(MediaColor.FromRgb(0x0A, 0x0C, 0x10)),
            Background = OverlayPalette.Brush(color, 0xCC),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(3, 0, 3, 1),
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, Math.Max(0, y - 13));
        canvas.Children.Add(label);
    }

    private static string FormatAmount(long? amount, string? raw)
    {
        if (amount is long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            return $"? ({raw})";
        }

        return "-";
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
