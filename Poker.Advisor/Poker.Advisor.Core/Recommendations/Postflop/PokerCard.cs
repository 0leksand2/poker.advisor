namespace Poker.Advisor.Core.Recommendations.Postflop;

public readonly record struct PokerCard(int Rank, char Suit) : IComparable<PokerCard>
{
    private const string Ranks = "23456789TJQKA";

    public char RankChar => Ranks[Rank];

    public string Label => $"{RankChar}{Suit}";

    public static bool TryParse(string? label, out PokerCard card)
    {
        card = default;
        if (string.IsNullOrWhiteSpace(label) || label.Length != 2)
        {
            return false;
        }

        var rank = char.ToUpperInvariant(label[0]);
        var suit = char.ToUpperInvariant(label[1]);
        var idx = Ranks.IndexOf(rank);
        if (idx < 0 || !"CDHS".Contains(suit))
        {
            return false;
        }

        card = new PokerCard(idx, suit);
        return true;
    }

    public static bool TryParseMany(IReadOnlyList<string> labels, out List<PokerCard> cards)
    {
        cards = new List<PokerCard>(labels.Count);
        foreach (var label in labels)
        {
            if (!TryParse(label, out var card))
            {
                cards = [];
                return false;
            }

            cards.Add(card);
        }

        return true;
    }

    public int CompareTo(PokerCard other) => Rank.CompareTo(other.Rank);

    public override string ToString() => Label;
}
