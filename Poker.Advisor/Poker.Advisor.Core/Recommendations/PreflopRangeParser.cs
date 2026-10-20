namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Expands compact poker range strings (e.g. "22+, ATs+, AJo+") into hand notations.
/// </summary>
public static class PreflopRangeParser
{
    private static readonly string Ranks = "23456789TJQKA";

    public static HashSet<string> Parse(string range)
    {
        var hands = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in range.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            ExpandToken(token, hands);
        }

        return hands;
    }

    private static void ExpandToken(string token, HashSet<string> hands)
    {
        // Pair plus: 22+
        if (token.Length == 3 && token[0] == token[1] && token[2] == '+')
        {
            var start = StartingHand.RankIndex(token[0]);
            if (start < 0)
            {
                return;
            }

            for (var i = start; i < Ranks.Length; i++)
            {
                var r = Ranks[i];
                hands.Add($"{r}{r}");
            }

            return;
        }

        // Pair: 99
        if (token.Length == 2 && token[0] == token[1])
        {
            hands.Add($"{char.ToUpperInvariant(token[0])}{char.ToUpperInvariant(token[1])}");
            return;
        }

        // Range of suited/offsuit: A5s-A2s or KTs-K9s
        var dash = token.IndexOf('-');
        if (dash > 0)
        {
            var left = token[..dash];
            var right = token[(dash + 1)..];
            if (TryParseNonPair(left, out var h1, out var l1, out var t1)
                && TryParseNonPair(right, out var h2, out var l2, out var t2)
                && h1 == h2
                && t1 == t2)
            {
                var from = StartingHand.RankIndex(l1);
                var to = StartingHand.RankIndex(l2);
                if (from < 0 || to < 0)
                {
                    return;
                }

                var lo = Math.Min(from, to);
                var hi = Math.Max(from, to);
                for (var i = lo; i <= hi; i++)
                {
                    hands.Add($"{h1}{Ranks[i]}{t1}");
                }
            }

            return;
        }

        // Plus non-pair: ATs+, AJo+, KQo+
        if (token.EndsWith('+')
            && TryParseNonPair(token[..^1], out var high, out var low, out var type))
        {
            var start = StartingHand.RankIndex(low);
            var highIdx = StartingHand.RankIndex(high);
            if (start < 0 || highIdx < 0)
            {
                return;
            }

            for (var i = start; i < highIdx; i++)
            {
                hands.Add($"{high}{Ranks[i]}{type}");
            }

            return;
        }

        // Single non-pair: AKs, T9o
        if (TryParseNonPair(token, out var h, out var l, out var t))
        {
            hands.Add($"{h}{l}{t}");
        }
    }

    private static bool TryParseNonPair(string token, out char high, out char low, out char type)
    {
        high = default;
        low = default;
        type = default;
        if (token.Length != 3)
        {
            return false;
        }

        high = char.ToUpperInvariant(token[0]);
        low = char.ToUpperInvariant(token[1]);
        type = char.ToLowerInvariant(token[2]);
        if (StartingHand.RankIndex(high) < 0
            || StartingHand.RankIndex(low) < 0
            || (type != 's' && type != 'o')
            || StartingHand.RankIndex(high) <= StartingHand.RankIndex(low))
        {
            return false;
        }

        return true;
    }
}
