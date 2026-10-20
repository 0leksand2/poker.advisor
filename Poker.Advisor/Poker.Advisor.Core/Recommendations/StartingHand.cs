namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Canonical two-card starting hand (AA, AKs, AKo).
/// </summary>
public readonly record struct StartingHand(string Notation)
{
    private static readonly string Ranks = "23456789TJQKA";

    public static bool TryParse(IReadOnlyList<string> cardLabels, out StartingHand hand)
    {
        hand = default;
        if (cardLabels.Count != 2)
        {
            return false;
        }

        if (!TryParseCard(cardLabels[0], out var r1, out var s1)
            || !TryParseCard(cardLabels[1], out var r2, out var s2))
        {
            return false;
        }

        if (r1 == r2)
        {
            hand = new StartingHand($"{r1}{r2}");
            return true;
        }

        var high = RankIndex(r1) >= RankIndex(r2) ? r1 : r2;
        var low = high == r1 ? r2 : r1;
        var suited = s1 == s2 ? 's' : 'o';
        hand = new StartingHand($"{high}{low}{suited}");
        return true;
    }

    public static int RankIndex(char rank) => Ranks.IndexOf(char.ToUpperInvariant(rank));

    private static bool TryParseCard(string label, out char rank, out char suit)
    {
        rank = default;
        suit = default;
        if (string.IsNullOrWhiteSpace(label) || label.Length != 2)
        {
            return false;
        }

        rank = char.ToUpperInvariant(label[0]);
        suit = char.ToUpperInvariant(label[1]);
        return Ranks.Contains(rank) && "CDHS".Contains(suit);
    }
}
