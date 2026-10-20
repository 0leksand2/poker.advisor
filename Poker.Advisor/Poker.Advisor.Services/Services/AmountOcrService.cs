using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Extensions;
using Poker.Advisor.Core.Models;

namespace Poker.Advisor.Services;

public sealed class AmountOcrService : IDisposable
{
    private static readonly Regex AmountRegex = new(
        @"(?<!\d)(\d{1,3}(?:[ \u00A0]?[.,]\d{3})+|\d+)(?:[.,]\d+)?(?!\d)",
        RegexOptions.Compiled);

    private readonly OcrEngine? _engine;
    private bool _disposed;

    public AmountOcrService()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("uk"));
    }

    public bool IsAvailable => _engine is not null;

    public async Task EnrichAsync(
        IReadOnlyList<TableState> tables,
        IReadOnlyList<Detection> detections,
        Bitmap screenshot,
        CancellationToken cancellationToken = default)
    {
        if (_engine is null || tables.Count == 0)
        {
            return;
        }

        var pots = detections.Where(d => d.Name.Equals("pot", StringComparison.OrdinalIgnoreCase)).ToList();
        var bets = detections.Where(d => d.Name.Equals("bet", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnrichPotAsync(table, pots, screenshot).ConfigureAwait(false);

            if (!table.HasHeroCards || table.PlayersClockwiseFromHero.Count == 0)
            {
                continue;
            }

            await EnrichBanksAsync(table, screenshot, cancellationToken).ConfigureAwait(false);
            await EnrichBetsAsync(table, bets, screenshot, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _disposed = true;

    private async Task EnrichPotAsync(TableState table, List<Detection> pots, Bitmap screenshot)
    {
        var pot = SelectMainPot(table, pots);
        if (pot is null)
        {
            table.PotAmount = null;
            table.PotRawText = null;
            return;
        }

        using var crop = CropRegion(screenshot, pot, pad: 6, minWidth: 320, minHeight: 48, scale: 3);
        var raw = await RecognizeAsync(crop).ConfigureAwait(false);
        table.PotRawText = raw;
        table.PotAmount = ParseAmount(raw, preferLargest: false);
    }

    private static Detection? SelectMainPot(TableState table, List<Detection> pots)
    {
        var maxDist = Math.Max(table.Table.Width(), table.Table.Height()) * 0.35;
        var candidates = pots
            .Select(p => (
                Pot: p,
                Inside: table.Table.CenterInside(p),
                Dist: table.Table.DistanceTo(p)))
            .Where(x => x.Inside || x.Dist <= maxDist)
            .Select(x => x.Pot)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var board = table.CommunityCardBoxes;
        candidates = candidates.Where(p => !IsTooCloseToSharedCards(p, board)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        // Keep pots in the horizontal center of the table (main pot UI).
        var midLeft = table.Table.X1 + table.Table.Width() * 0.22;
        var midRight = table.Table.X1 + table.Table.Width() * 0.78;
        var centered = candidates
            .Where(p => p.CenterX() >= midLeft && p.CenterX() <= midRight)
            .ToList();
        if (centered.Count > 0)
        {
            candidates = centered;
        }

        if (board.Count > 0)
        {
            var boardTop = board.Min(c => c.Y1);
            // Main pot sits just above the board — ignore pots that sit on/under the cards.
            var aboveBoard = candidates
                .Where(p => p.CenterY() < boardTop - 6 && p.Y2 <= boardTop + 12)
                .ToList();
            if (aboveBoard.Count > 0)
            {
                candidates = aboveBoard;
            }
        }

        // Hero / main pot is the lowest valid pot (closest to board/hero).
        // Anything above that (smaller Y) is ignored.
        var mainPot = candidates.OrderByDescending(p => p.CenterY()).First();
        var heroPotTop = mainPot.Y1 - Math.Max(18, table.Table.Height() * 0.03);
        return candidates
            .Where(p => p.CenterY() >= heroPotTop)
            .OrderByDescending(p => p.CenterY())
            .ThenByDescending(p => p.Confidence)
            .First();
    }

    private static bool IsTooCloseToSharedCards(Detection pot, IReadOnlyList<Detection> board)
    {
        if (board.Count == 0)
        {
            return false;
        }

        foreach (var card in board)
        {
            if (pot.DistanceTo(card) < 50)
            {
                return true;
            }

            var overlapsX = pot.X1 < card.X2 + 8 && pot.X2 > card.X1 - 8;
            var overlapsY = pot.Y1 < card.Y2 + 10 && pot.Y2 > card.Y1 - 10;
            if (overlapsX && overlapsY)
            {
                return true;
            }
        }

        return false;
    }

    private async Task EnrichBanksAsync(
        TableState table,
        Bitmap screenshot,
        CancellationToken cancellationToken)
    {
        foreach (var player in table.PlayersClockwiseFromHero)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // OCR strictly below the nameplate divider so nickname digits (sigma-2000) are excluded.
            var region = ResolveBankOcrRegion(player.BankRegion, screenshot);
            using var crop = CropRegion(screenshot, region, pad: 2, minWidth: 220, minHeight: 48, scale: 4);
            using var boosted = BoostForOcr(crop);
            var raw = await RecognizeAsync(boosted).ConfigureAwait(false);
            player.BankRawText = raw;
            player.BankAmount = ParseBankAmount(raw);
        }
    }

    private async Task EnrichBetsAsync(
        TableState table,
        List<Detection> allBets,
        Bitmap screenshot,
        CancellationToken cancellationToken)
    {
        foreach (var player in table.PlayersClockwiseFromHero)
        {
            player.BetAmount = null;
            player.BetRawText = null;
        }

        var players = table.PlayersClockwiseFromHero;
        if (players.Count == 0)
        {
            return;
        }

        var maxDist = Math.Max(table.Table.Width(), table.Table.Height()) * 0.28;
        var tableBets = allBets
            .Where(b => table.Table.CenterInside(b) || table.Table.DistanceTo(b) <= maxDist * 1.4)
            .ToList();

        // Exclusive nearest: each bet → one player, greedily by closest distance.
        var tableCx = table.Table.CenterX();
        var tableCy = table.Table.CenterY();
        var assignments = AssignBetsToClosestPlayers(tableBets, players, maxDist, tableCx, tableCy);
        var betTotals = new Dictionary<PlayerSeatState, (long Amount, string Raw)>();

        foreach (var (bet, owner) in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var crop = CropRegion(screenshot, bet, pad: 8, minWidth: 140, minHeight: 56, scale: 4);
            using var boosted = BoostForOcr(crop);
            var raw = await RecognizeAsync(boosted).ConfigureAwait(false);
            var amount = ParseBetAmount(raw);

            if (betTotals.TryGetValue(owner, out var existing))
            {
                // Same seat can get multiple chip piles; keep the larger street bet.
                var nextAmount = amount is long a
                    ? Math.Max(existing.Amount, a)
                    : existing.Amount;
                betTotals[owner] = (nextAmount, MergeRaw(existing.Raw, raw));
            }
            else
            {
                betTotals[owner] = (amount ?? 0, raw);
            }
        }

        foreach (var (player, value) in betTotals)
        {
            if (value.Amount > 0)
            {
                player.BetAmount = value.Amount;
            }

            player.BetRawText = string.IsNullOrWhiteSpace(value.Raw) ? null : value.Raw;
        }
    }

    /// <summary>
    /// Pairs each bet with the closest player (within maxDist). Closest pairs win first.
    /// </summary>
    internal static List<(Detection Bet, PlayerSeatState Player)> AssignBetsToClosestPlayers(
        IReadOnlyList<Detection> bets,
        IReadOnlyList<PlayerSeatState> players,
        double maxDist,
        double tableCenterX,
        double tableCenterY)
    {
        var pairs = new List<(Detection Bet, PlayerSeatState Player, double Dist)>();
        foreach (var bet in bets)
        {
            PlayerSeatState? bestPlayer = null;
            var bestDist = double.MaxValue;
            foreach (var player in players)
            {
                var dist = DistancePlayerToBet(player, bet, tableCenterX, tableCenterY);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPlayer = player;
                }
            }

            if (bestPlayer is not null && bestDist <= maxDist)
            {
                pairs.Add((bet, bestPlayer, bestDist));
            }
        }

        var assignedBets = new HashSet<Detection>();
        var result = new List<(Detection Bet, PlayerSeatState Player)>(pairs.Count);
        foreach (var pair in pairs.OrderBy(p => p.Dist))
        {
            if (!assignedBets.Add(pair.Bet))
            {
                continue;
            }

            result.Add((pair.Bet, pair.Player));
        }

        return result;
    }

    /// <summary>
    /// Distance from player to bet, with a bonus when the bet lies toward the table center.
    /// </summary>
    private static double DistancePlayerToBet(
        PlayerSeatState player,
        Detection bet,
        double tableCenterX,
        double tableCenterY)
    {
        var px = player.Detection.CenterX();
        var py = player.Detection.CenterY();
        var bx = bet.CenterX();
        var by = bet.CenterY();
        var euclidean = Math.Sqrt((px - bx) * (px - bx) + (py - by) * (py - by));

        var playerToCenter = Math.Sqrt(
            (px - tableCenterX) * (px - tableCenterX) + (py - tableCenterY) * (py - tableCenterY));
        var betToCenter = Math.Sqrt(
            (bx - tableCenterX) * (bx - tableCenterX) + (by - tableCenterY) * (by - tableCenterY));

        // Chip bets sit inward from the seat; penalize bets behind the player (farther from center).
        var inwardBonus = betToCenter < playerToCenter ? 0.85 : 1.15;
        return euclidean * inwardBonus;
    }

    internal static long? ParseBetAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Bet chips are a single amount; prefer the largest digit group OCR found.
        return ParseAmount(text, preferLargest: true);
    }

    private async Task<string> RecognizeAsync(Bitmap crop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is null)
        {
            return string.Empty;
        }

        using var stream = new InMemoryRandomAccessStream();
        await using (var netStream = crop.SaveToPngStream())
        {
            var buffer = new byte[netStream.Length];
            _ = await netStream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            await stream.WriteAsync(buffer.AsBuffer());
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
        var bgra = softwareBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            && softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
            ? softwareBitmap
            : SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        try
        {
            var result = await _engine.RecognizeAsync(bgra);
            return result.Text?.Trim() ?? string.Empty;
        }
        finally
        {
            if (!ReferenceEquals(bgra, softwareBitmap))
            {
                bgra.Dispose();
            }
        }
    }

    internal static long? ParseBankAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Prefer bottom stack lines. Skip nickname lines (letters + digits).
        var lineCandidates = new List<(long Value, bool HasThousandsSeparator, int LineIndex)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (LooksLikeNicknameWithDigits(line) || !LooksLikeStackAmountLine(line))
            {
                continue;
            }

            var value = ParseAmount(line, preferLargest: false);
            if (value is null)
            {
                continue;
            }

            lineCandidates.Add((value.Value, HasThousandsSeparator(line), i));
        }

        if (lineCandidates.Count > 0)
        {
            return lineCandidates
                .OrderByDescending(c => c.HasThousandsSeparator)
                .ThenByDescending(c => c.LineIndex)
                .ThenByDescending(c => c.Value)
                .First()
                .Value;
        }

        // Fallback tokens: prefer comma-formatted stacks; never take digits glued into nicknames.
        var tokenCandidates = new List<(long Value, bool HasThousandsSeparator, int Index)>();
        foreach (Match match in AmountRegex.Matches(text))
        {
            if (IsDigitRunInsideNickname(text, match))
            {
                continue;
            }

            var value = NormalizeAmountToken(match.Value);
            if (value is null)
            {
                continue;
            }

            tokenCandidates.Add((value.Value, HasThousandsSeparator(match.Value), match.Index));
        }

        if (tokenCandidates.Count == 0)
        {
            return null;
        }

        return tokenCandidates
            .OrderByDescending(c => c.HasThousandsSeparator)
            .ThenByDescending(c => c.Index)
            .ThenByDescending(c => c.Value)
            .First()
            .Value;
    }

    private static bool HasThousandsSeparator(string text) =>
        Regex.IsMatch(text, @"\d{1,3}([.,]\d{3})+");

    private static bool LooksLikeNicknameWithDigits(string line)
    {
        var hasLetter = line.Any(char.IsLetter);
        var hasDigit = line.Any(char.IsDigit);
        return hasLetter && hasDigit;
    }

    private static bool LooksLikeStackAmountLine(string line)
    {
        // Digits / separators / currency only — hyphens from nicknames are NOT allowed here.
        var cleaned = Regex.Replace(line, @"[\s\u00A0,.:$€₴]", string.Empty);
        return cleaned.Length > 0 && cleaned.All(char.IsDigit);
    }

    private static bool IsDigitRunInsideNickname(string text, Match match)
    {
        var start = match.Index;
        var end = match.Index + match.Length;

        // "sigma-2000", "CoolShark_958", "yaman2023"
        if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '-'))
        {
            // Digits after '-' / '_' only count as nickname if a letter appears before that connector.
            if (text[start - 1] is '-' or '_')
            {
                for (var i = start - 2; i >= 0; i--)
                {
                    if (char.IsLetter(text[i]))
                    {
                        return true;
                    }

                    if (char.IsWhiteSpace(text[i]))
                    {
                        break;
                    }
                }
            }
            else
            {
                return true;
            }
        }

        if (end < text.Length && (char.IsLetter(text[end]) || text[end] is '_' or '-'))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the grey divider under the nickname and returns only the stack row beneath it.
    /// </summary>
    private static Detection ResolveBankOcrRegion(Detection bankRegion, Bitmap screenshot)
    {
        var plate = NameplateRegion(bankRegion);
        using var plateCrop = CropRegion(screenshot, plate, pad: 0, minWidth: 1, minHeight: 1, scale: 1);
        var dividerLocalY = FindHorizontalDividerY(plateCrop);
        if (dividerLocalY < 0)
        {
            return StackAmountBand(bankRegion);
        }

        var w = plate.Width();
        var h = plate.Height();
        var top = Math.Min(plate.Y2 - 8, plate.Y1 + dividerLocalY + 2);
        var bottom = plate.Y2 - Math.Max(3, h * 0.06);
        var left = plate.X1 + w * 0.10;
        var right = plate.X2 - w * 0.10;
        if (bottom <= top + 4)
        {
            return StackAmountBand(bankRegion);
        }

        return new Detection(bankRegion.Name, bankRegion.Confidence, left, top, right, bottom);
    }

    private static Detection NameplateRegion(Detection box)
    {
        if (box.Name.Equals("hero_bank", StringComparison.OrdinalIgnoreCase))
        {
            return box;
        }

        // Player YOLO boxes include avatar/cards above the dark nameplate.
        var top = box.Y1 + box.Height() * 0.42;
        return new Detection(box.Name, box.Confidence, box.X1, top, box.X2, box.Y2);
    }

    private static int FindHorizontalDividerY(Bitmap plate)
    {
        var w = plate.Width;
        var h = plate.Height;
        if (w < 20 || h < 20)
        {
            return -1;
        }

        var yStart = Math.Max(1, (int)(h * 0.20));
        var yEnd = Math.Min(h - 2, (int)(h * 0.80));
        var xStart = (int)(w * 0.12);
        var xEnd = (int)(w * 0.88);
        var span = Math.Max(1, xEnd - xStart);

        var bestY = -1;
        var bestScore = 0.0;

        for (var y = yStart; y < yEnd; y++)
        {
            var linePixels = 0;
            var darkAbove = 0;
            var darkBelow = 0;
            for (var x = xStart; x < xEnd; x++)
            {
                var luma = Luma(plate.GetPixel(x, y));
                if (luma is >= 50 and <= 185)
                {
                    linePixels++;
                }

                if (Luma(plate.GetPixel(x, y - 1)) < 45)
                {
                    darkAbove++;
                }

                if (Luma(plate.GetPixel(x, y + 1)) < 45)
                {
                    darkBelow++;
                }
            }

            var lineRatio = linePixels / (double)span;
            var darkRatio = Math.Min(darkAbove, darkBelow) / (double)span;
            var score = lineRatio * 0.7 + darkRatio * 0.3;
            if (lineRatio >= 0.28 && darkRatio >= 0.20 && score > bestScore)
            {
                bestScore = score;
                bestY = y;
            }
        }

        return bestY;
    }

    private static int Luma(Color c) => (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);

    internal static long? ParseAmount(string? text, bool preferLargest)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var source = preferLargest ? text : (text.Split(':').LastOrDefault() ?? text);
        var matches = AmountRegex.Matches(source);
        if (matches.Count == 0 && !preferLargest)
        {
            matches = AmountRegex.Matches(text);
        }

        var values = matches
            .Select(m => NormalizeAmountToken(m.Value))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        if (values.Count == 0)
        {
            return null;
        }

        return preferLargest ? values.Max() : values[^1];
    }

    private static long? NormalizeAmountToken(string token)
    {
        var normalized = token
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\u00A0", string.Empty, StringComparison.Ordinal);

        if (Regex.IsMatch(normalized, @"^\d{1,3}([.,]\d{3})+$"))
        {
            normalized = normalized.Replace(",", string.Empty, StringComparison.Ordinal)
                .Replace(".", string.Empty, StringComparison.Ordinal);
        }
        else if (normalized.Contains(',') || normalized.Contains('.'))
        {
            var parts = normalized.Split(',', '.');
            if (parts.Length == 2 && parts[1].Length <= 2)
            {
                normalized = parts[0];
            }
            else
            {
                normalized = string.Concat(parts);
            }
        }

        return long.TryParse(normalized, out var value) ? value : null;
    }

    /// <summary>
    /// Crops the stack line under the player name divider, avoiding badges/flags/timer bars.
    /// </summary>
    private static Detection StackAmountBand(Detection box)
    {
        var w = box.Width();
        var h = box.Height();
        var isHeroBank = box.Name.Equals("hero_bank", StringComparison.OrdinalIgnoreCase);

        // Keep crop on the stack row under the name divider so nickname digits stay out.
        var topFraction = isHeroBank ? 0.70 : 0.66;
        var bottomTrim = isHeroBank ? 0.06 : 0.08;
        var sideInset = isHeroBank ? 0.10 : 0.12;

        var top = box.Y1 + h * topFraction;
        var bottom = box.Y2 - Math.Max(3, h * bottomTrim);
        var left = box.X1 + w * sideInset;
        var right = box.X2 - w * sideInset;

        if (bottom <= top + 6)
        {
            top = box.Y1 + h * 0.50;
            bottom = box.Y2 - 2;
        }

        if (right <= left + 8)
        {
            left = box.X1 + 2;
            right = box.X2 - 2;
        }

        return new Detection(box.Name, box.Confidence, left, top, right, bottom);
    }

    private static Bitmap BoostForOcr(Bitmap source)
    {
        // Brighten grey-on-black stack text for Windows OCR.
        var boosted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var c = source.GetPixel(x, y);
                var luma = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                // Stretch mid-greys upward so "48,077" becomes closer to white.
                var v = Math.Clamp((luma - 40) * 255 / 160, 0, 255);
                boosted.SetPixel(x, y, Color.FromArgb(255, v, v, v));
            }
        }

        return boosted;
    }

    private static Bitmap CropRegion(
        Bitmap screenshot,
        Detection region,
        int pad,
        int minWidth,
        int minHeight,
        int scale)
    {
        var x = Math.Clamp((int)Math.Floor(region.X1) - pad, 0, screenshot.Width - 1);
        var y = Math.Clamp((int)Math.Floor(region.Y1) - pad, 0, screenshot.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(region.X2) + pad, x + 1, screenshot.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(region.Y2) + pad, y + 1, screenshot.Height);
        var width = right - x;
        var height = bottom - y;

        var crop = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(crop))
        {
            graphics.DrawImage(
                screenshot,
                new Rectangle(0, 0, width, height),
                new Rectangle(x, y, width, height),
                GraphicsUnit.Pixel);
        }

        if (width < minWidth || height < minHeight)
        {
            var scaled = new Bitmap(width * scale, height * scale, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(crop, 0, 0, scaled.Width, scaled.Height);
            crop.Dispose();
            return scaled;
        }

        return crop;
    }

    private static string MergeRaw(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a))
        {
            return b;
        }

        if (string.IsNullOrWhiteSpace(b))
        {
            return a;
        }

        return a + " | " + b;
    }
}

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
