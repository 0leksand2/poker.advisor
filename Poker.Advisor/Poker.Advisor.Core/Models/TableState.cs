using System.Globalization;
using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Extensions;

namespace Poker.Advisor.Core.Models;

public sealed class TableState
{
    public required int TableNumber { get; init; }
    public required Detection Table { get; init; }
    public bool HasHeroCards { get; init; }
    public IReadOnlyList<string> HeroCards { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CommunityCards { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Detection> CommunityCardBoxes { get; init; } = Array.Empty<Detection>();
    public PokerStreet Street { get; init; } = PokerStreet.Unknown;
    public int PlayerCount { get; init; }
    public IReadOnlyList<PlayerSeatState> PlayersClockwiseFromHero { get; init; } = Array.Empty<PlayerSeatState>();
    public string? HeroPosition { get; init; }
    public string SummaryLine { get; init; } = string.Empty;
    public long? PotAmount { get; set; }
    public string? PotRawText { get; set; }
    public HeroRecommendation? Recommendation { get; set; }

    public IEnumerable<PanelLine> FormatPanelLines()
    {
        yield return new PanelLine($"table_{TableNumber}:");

        if (!HasHeroCards)
        {
            yield return new PanelLine("waiting for cards");
            yield break;
        }

        // 1) short advice  2) extended advice  3) pot  4) hero bank | cards
        if (Recommendation is not null)
        {
            yield return new PanelLine(Recommendation.ToShortPanelText(), Recommendation.Tone);
            if (!string.IsNullOrWhiteSpace(Recommendation.Summary))
            {
                yield return new PanelLine(Recommendation.Summary, Recommendation.Tone);
            }
        }

        yield return new PanelLine($"pot: {FormatAmount(PotAmount, PotRawText)}");

        var hero = PlayersClockwiseFromHero.FirstOrDefault();
        var heroCards = HeroCards.Count == 0 ? "?" : string.Join(" ", HeroCards);
        yield return new PanelLine(
            $"hero: bank {FormatAmount(hero?.BankAmount, hero?.BankRawText)} | {heroCards}");

        foreach (var player in PlayersClockwiseFromHero.Skip(1))
        {
            var isFold = player.Status == PlayerStatus.Folded;
            var restTone = isFold ? PanelTone.Fold : PanelTone.Normal;
            var nick = string.IsNullOrWhiteSpace(player.Nickname)
                ? (player.Position.Length == 0 ? "?" : player.Position)
                : player.Nickname;
            var style = player.Stats is { Hands: > 0 } s ? s.StyleLabel : "?";
            var styleTone = isFold
                ? PanelTone.Fold
                : player.Stats is { Hands: > 0 } st ? st.StyleTone : PanelTone.Normal;
            var bank = FormatAmount(player.BankAmount, player.BankRawText);
            var bet = FormatAmount(player.BetAmount, player.BetRawText);
            yield return new PanelLine(
                new PanelSpan($"{nick}: ", restTone),
                new PanelSpan(style, styleTone),
                new PanelSpan($" | {bank} | {bet}", restTone));
        }
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
}
