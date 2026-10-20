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

    public IEnumerable<PanelLine> FormatPanelLines()
    {
        yield return new PanelLine($"table_{TableNumber}:");
        yield return new PanelLine($"pot: {FormatAmount(PotAmount, PotRawText)}");

        if (!HasHeroCards)
        {
            yield return new PanelLine("waiting for cards");
            yield break;
        }

        yield return new PanelLine($"street: {Street.ToDisplay()}");

        var hero = PlayersClockwiseFromHero.FirstOrDefault();
        var heroCards = HeroCards.Count == 0 ? "?" : string.Join(" ", HeroCards);
        yield return new PanelLine(
            $"hero: {heroCards} - {HeroPosition ?? "?"} | bank: {FormatAmount(hero?.BankAmount, hero?.BankRawText)} | bet: {FormatAmount(hero?.BetAmount, hero?.BetRawText)}");

        foreach (var player in PlayersClockwiseFromHero.Skip(1))
        {
            var isFold = player.Status == PlayerStatus.Folded;
            var kind = isFold ? "fold_player" : "active_player";
            yield return new PanelLine(
                $"{kind} - {player.Position} | bank: {FormatAmount(player.BankAmount, player.BankRawText)} | bet: {FormatAmount(player.BetAmount, player.BetRawText)}",
                IsFold: isFold);
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
