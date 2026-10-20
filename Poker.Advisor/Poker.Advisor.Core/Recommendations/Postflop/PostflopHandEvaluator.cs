namespace Poker.Advisor.Core.Recommendations.Postflop;

/// <summary>
/// Evaluates hero hole cards + board into a made-hand class and draw flags.
/// </summary>
public static class PostflopHandEvaluator
{
    public static bool TryAssess(
        IReadOnlyList<string> heroCards,
        IReadOnlyList<string> boardCards,
        out PostflopHandAssessment assessment)
    {
        assessment = null!;
        if (heroCards.Count != 2
            || boardCards.Count is < 3 or > 5
            || !PokerCard.TryParseMany(heroCards, out var hole)
            || !PokerCard.TryParseMany(boardCards, out var board))
        {
            return false;
        }

        var all = hole.Concat(board).ToList();
        var best = BestFiveCardHand(all);
        var made = ClassifyMadeHand(best, hole, board);
        var draws = board.Count < 5
            ? DetectDraws(hole, board)
            : (FlushDraw: false, Oesd: false, Gutshot: false, BackdoorFlush: false);

        var category = MergeCategory(made.Category, draws);
        var label = BuildLabel(made.Label, draws);

        assessment = new PostflopHandAssessment
        {
            Category = category,
            Label = label,
            AbsoluteRank = best.Score,
            HasFlushDraw = draws.FlushDraw,
            HasOpenEndedStraightDraw = draws.Oesd,
            HasGutshot = draws.Gutshot,
            HasBackdoorFlushDraw = draws.BackdoorFlush,
        };
        return true;
    }

    private static HandCategory MergeCategory(
        HandCategory made,
        (bool FlushDraw, bool Oesd, bool Gutshot, bool BackdoorFlush) draws)
    {
        if (made >= HandCategory.TopPairGood)
        {
            return made;
        }

        if (draws.FlushDraw && draws.Oesd)
        {
            return HandCategory.StrongDraw;
        }

        if (draws.FlushDraw || draws.Oesd)
        {
            return made >= HandCategory.MiddlePair ? made : HandCategory.StrongDraw;
        }

        if (draws.Gutshot || draws.BackdoorFlush)
        {
            return made > HandCategory.WeakDraw ? made : HandCategory.WeakDraw;
        }

        return made;
    }

    private static string BuildLabel(
        string madeLabel,
        (bool FlushDraw, bool Oesd, bool Gutshot, bool BackdoorFlush) draws)
    {
        var extras = new List<string>();
        if (draws.FlushDraw)
        {
            extras.Add("FD");
        }

        if (draws.Oesd)
        {
            extras.Add("OESD");
        }
        else if (draws.Gutshot)
        {
            extras.Add("gutshot");
        }

        if (draws.BackdoorFlush && !draws.FlushDraw)
        {
            extras.Add("bdfd");
        }

        return extras.Count == 0 ? madeLabel : $"{madeLabel}+{string.Join("+", extras)}";
    }

    private static (HandCategory Category, string Label) ClassifyMadeHand(
        FiveCardHand best,
        List<PokerCard> hole,
        List<PokerCard> board)
    {
        var boardHigh = board.Max(c => c.Rank);
        var holeRanks = hole.Select(c => c.Rank).OrderByDescending(r => r).ToArray();
        var pairedHole = holeRanks[0] == holeRanks[1];

        return best.Category switch
        {
            FiveCategory.StraightFlush => (HandCategory.Monster, "straight flush"),
            FiveCategory.Quads => (HandCategory.Monster, "quads"),
            FiveCategory.FullHouse => (HandCategory.Monster, "full house"),
            FiveCategory.Flush => (HandCategory.Monster, "flush"),
            FiveCategory.Straight => (HandCategory.Monster, "straight"),
            FiveCategory.Trips => (HandCategory.TwoPairOrSet, pairedHole && holeRanks[0] != boardHigh ? "set" : "trips"),
            FiveCategory.TwoPair => (HandCategory.TwoPairOrSet, "two pair"),
            FiveCategory.Pair => ClassifyOnePair(best, hole, board, boardHigh, pairedHole, holeRanks),
            _ => ClassifyHighCard(hole, board, boardHigh),
        };
    }

    private static (HandCategory Category, string Label) ClassifyOnePair(
        FiveCardHand best,
        List<PokerCard> hole,
        List<PokerCard> board,
        int boardHigh,
        bool pairedHole,
        int[] holeRanks)
    {
        var pairRank = best.PrimaryRank;
        var boardRanks = board.Select(c => c.Rank).OrderByDescending(r => r).ToArray();

        if (pairedHole)
        {
            if (pairRank > boardHigh)
            {
                return (HandCategory.TopPairGood, "overpair");
            }

            // Underpair vs board
            if (boardRanks.Length >= 2 && pairRank >= boardRanks[1])
            {
                return (HandCategory.MiddlePair, "underpair");
            }

            return (HandCategory.WeakPair, "underpair");
        }

        // Pair uses one hole card.
        var kicker = hole.Select(c => c.Rank).Where(r => r != pairRank).DefaultIfEmpty(-1).Max();
        if (pairRank == boardHigh)
        {
            // Good kicker: Q+ or close to board high (T+).
            if (kicker >= 10 || kicker >= 8 && kicker >= boardHigh - 2)
            {
                return (HandCategory.TopPairGood, "top pair good kicker");
            }

            return (HandCategory.TopPairWeak, "top pair weak kicker");
        }

        if (boardRanks.Length >= 2 && pairRank == boardRanks[1])
        {
            return (HandCategory.MiddlePair, "middle pair");
        }

        if (board.Any(c => c.Rank == pairRank))
        {
            return (HandCategory.WeakPair, "bottom pair");
        }

        // Pocket pair already handled; leftover pair on board only → no hole contribution
        if (!hole.Any(c => c.Rank == pairRank))
        {
            return ClassifyHighCard(hole, board, boardHigh);
        }

        return (HandCategory.WeakPair, "pair");
    }

    private static (HandCategory Category, string Label) ClassifyHighCard(
        List<PokerCard> hole,
        List<PokerCard> board,
        int boardHigh)
    {
        var overs = hole.Count(c => c.Rank > boardHigh);
        if (overs == 2 && hole.Max(c => c.Rank) >= 12)
        {
            return (HandCategory.WeakShowdown, "ace-high overs");
        }

        if (overs >= 1 && hole.Max(c => c.Rank) >= 12)
        {
            return (HandCategory.WeakShowdown, "ace high");
        }

        if (overs == 2)
        {
            return (HandCategory.WeakShowdown, "overcards");
        }

        return (HandCategory.Junk, "air");
    }

    private static (bool FlushDraw, bool Oesd, bool Gutshot, bool BackdoorFlush) DetectDraws(
        List<PokerCard> hole,
        List<PokerCard> board)
    {
        var all = hole.Concat(board).ToList();
        var flushDraw = false;
        var backdoor = false;

        foreach (var suit in new[] { 'C', 'D', 'H', 'S' })
        {
            var total = all.Count(c => c.Suit == suit);
            var holeSuited = hole.Count(c => c.Suit == suit);
            if (total == 4 && holeSuited >= 1)
            {
                flushDraw = true;
            }
            else if (board.Count == 3 && total == 3 && holeSuited >= 1)
            {
                backdoor = true;
            }
        }

        var (oesd, gutshot) = DetectStraightDraws(hole, board);
        return (flushDraw, oesd, gutshot, backdoor);
    }

    private static (bool Oesd, bool Gutshot) DetectStraightDraws(List<PokerCard> hole, List<PokerCard> board)
    {
        // Count unique ranks; wheel ace as 12 and also -1 virtual for A-5.
        var ranks = hole.Concat(board).Select(c => c.Rank).Distinct().OrderBy(r => r).ToList();
        if (ranks.Contains(12))
        {
            ranks.Insert(0, -1); // ace low
        }

        var outsToStraight = new HashSet<int>();
        for (var target = 0; target <= 12; target++)
        {
            if (ranks.Contains(target))
            {
                continue;
            }

            var with = ranks.Append(target).Distinct().OrderBy(r => r).ToList();
            if (HasStraightInRanks(with))
            {
                // Only count if at least one hole card is used in some 5-straight window — approximate:
                // require that hole contributes a rank in the completed straight neighborhood.
                outsToStraight.Add(target == -1 ? 12 : target);
            }
        }

        // Count card outs (up to 4 per completing rank), only when hole cards are needed.
        var seen = hole.Concat(board).ToList();
        var usefulOuts = 0;
        foreach (var outRank in outsToStraight)
        {
            if (!HoleHelpsStraight(hole, board, outRank))
            {
                continue;
            }

            var remaining = 4 - seen.Count(c => c.Rank == outRank);
            if (remaining > 0)
            {
                usefulOuts += remaining;
            }
        }

        // OESD ≈ 8 outs; gutshot ≈ 4.
        if (usefulOuts >= 8)
        {
            return (true, false);
        }

        if (usefulOuts >= 4)
        {
            return (false, true);
        }

        return (false, false);
    }

    private static bool HoleHelpsStraight(List<PokerCard> hole, List<PokerCard> board, int outRank)
    {
        var boardOnly = board.Select(c => c.Rank).Append(outRank).Distinct().ToList();
        if (boardOnly.Contains(12))
        {
            boardOnly.Add(-1);
        }

        // If board+out already makes a straight, hole is not needed.
        if (HasStraightInRanks(boardOnly.OrderBy(r => r).ToList()))
        {
            return false;
        }

        var withHole = hole.Select(c => c.Rank).Concat(boardOnly).Distinct().OrderBy(r => r).ToList();
        return HasStraightInRanks(withHole);
    }

    private static bool HasStraightInRanks(List<int> sortedUnique)
    {
        if (sortedUnique.Count < 5)
        {
            return false;
        }

        for (var i = 0; i <= sortedUnique.Count - 5; i++)
        {
            var ok = true;
            for (var j = 1; j < 5; j++)
            {
                if (sortedUnique[i + j] != sortedUnique[i] + j)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }

    private static FiveCardHand BestFiveCardHand(List<PokerCard> cards)
    {
        FiveCardHand? best = null;
        foreach (var combo in Combinations(cards, 5))
        {
            var hand = EvaluateFive(combo);
            if (best is null || hand.Score > best.Score)
            {
                best = hand;
            }
        }

        return best ?? EvaluateFive(cards.Take(5).ToList());
    }

    private static IEnumerable<List<PokerCard>> Combinations(List<PokerCard> cards, int k)
    {
        var n = cards.Count;
        if (k > n)
        {
            yield break;
        }

        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return idx.Select(i => cards[i]).ToList();
            var i = k - 1;
            while (i >= 0 && idx[i] == i + n - k)
            {
                i--;
            }

            if (i < 0)
            {
                yield break;
            }

            idx[i]++;
            for (var j = i + 1; j < k; j++)
            {
                idx[j] = idx[j - 1] + 1;
            }
        }
    }

    private static FiveCardHand EvaluateFive(List<PokerCard> cards)
    {
        var sorted = cards.OrderByDescending(c => c.Rank).ToList();
        var ranks = sorted.Select(c => c.Rank).ToArray();
        var isFlush = cards.All(c => c.Suit == cards[0].Suit);
        var isStraight = IsStraight(ranks, out var topStraight);

        var groups = ranks.GroupBy(r => r)
            .Select(g => (Rank: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.Rank)
            .ToList();

        if (isStraight && isFlush)
        {
            return new FiveCardHand(FiveCategory.StraightFlush, topStraight, Pack(8, ranks));
        }

        if (groups[0].Count == 4)
        {
            var kicker = groups.First(g => g.Count == 1).Rank;
            return new FiveCardHand(FiveCategory.Quads, groups[0].Rank, Pack(7, groups[0].Rank, kicker));
        }

        if (groups[0].Count == 3 && groups[1].Count == 2)
        {
            return new FiveCardHand(FiveCategory.FullHouse, groups[0].Rank, Pack(6, groups[0].Rank, groups[1].Rank));
        }

        if (isFlush)
        {
            return new FiveCardHand(FiveCategory.Flush, ranks[0], Pack(5, ranks));
        }

        if (isStraight)
        {
            return new FiveCardHand(FiveCategory.Straight, topStraight, Pack(4, topStraight));
        }

        if (groups[0].Count == 3)
        {
            var kickers = groups.Where(g => g.Count == 1).Select(g => g.Rank).OrderByDescending(r => r).ToArray();
            return new FiveCardHand(
                FiveCategory.Trips,
                groups[0].Rank,
                Pack(3, new[] { groups[0].Rank }.Concat(kickers).ToArray()));
        }

        if (groups[0].Count == 2 && groups[1].Count == 2)
        {
            var highPair = Math.Max(groups[0].Rank, groups[1].Rank);
            var lowPair = Math.Min(groups[0].Rank, groups[1].Rank);
            var kicker = groups.First(g => g.Count == 1).Rank;
            return new FiveCardHand(FiveCategory.TwoPair, highPair, Pack(2, highPair, lowPair, kicker));
        }

        if (groups[0].Count == 2)
        {
            var kickers = groups.Where(g => g.Count == 1).Select(g => g.Rank).OrderByDescending(r => r).ToArray();
            return new FiveCardHand(
                FiveCategory.Pair,
                groups[0].Rank,
                Pack(1, new[] { groups[0].Rank }.Concat(kickers).ToArray()));
        }

        return new FiveCardHand(FiveCategory.HighCard, ranks[0], Pack(0, ranks));
    }

    private static bool IsStraight(int[] ranksDesc, out int top)
    {
        var uniq = ranksDesc.Distinct().OrderByDescending(r => r).ToList();
        top = 0;
        if (uniq.Count < 5)
        {
            // allow duplicates in five-card set only if not straight
        }

        // Normal windows on unique ranks from this five-card hand
        var u = ranksDesc.Distinct().OrderBy(r => r).ToList();
        if (u.Contains(12))
        {
            // wheel
            var wheel = new[] { 12, 0, 1, 2, 3 };
            if (wheel.All(u.Contains))
            {
                top = 3; // 5-high
                return true;
            }
        }

        for (var i = 0; i <= u.Count - 5; i++)
        {
            if (u[i + 4] == u[i] + 4)
            {
                top = u[i + 4];
                return true;
            }
        }

        // Exactly 5 cards: check sorted unique length
        if (u.Count == 5 && u[4] == u[0] + 4)
        {
            top = u[4];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Fixed-width score: category in high bits, then up to 5 ranks.
    /// Avoids variable-length shifting where a pair could outrank trips.
    /// </summary>
    private static int Pack(int category, params int[] ranks)
    {
        var score = (category & 0xF) << 20;
        var padded = ranks.Concat(Enumerable.Repeat(0, 5)).Take(5).ToArray();
        for (var i = 0; i < 5; i++)
        {
            score |= (padded[i] & 0xF) << (16 - 4 * i);
        }

        return score;
    }

    private enum FiveCategory
    {
        HighCard,
        Pair,
        TwoPair,
        Trips,
        Straight,
        Flush,
        FullHouse,
        Quads,
        StraightFlush,
    }

    private sealed class FiveCardHand(FiveCategory category, int primaryRank, int score)
    {
        public FiveCategory Category { get; } = category;
        public int PrimaryRank { get; } = primaryRank;
        public int Score { get; } = score;
    }
}
