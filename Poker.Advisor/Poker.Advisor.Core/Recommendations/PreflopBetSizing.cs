using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Classes;

namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Infers facing bet / big blind and suggests raise-to amounts from seat bets.
/// </summary>
public static class PreflopBetSizing
{
    public sealed record Context(
        long? BigBlind,
        long FacingBet,
        long HeroBet,
        long CallAmount,
        bool PotLooksUnopened,
        int BlindSizedBets,
        PlayerSeatState? FacingPlayer,
        StackAwareSizing.Context Stacks);

    public static Context Analyze(TableState state)
    {
        var players = state.PlayersClockwiseFromHero;
        var hero = players.FirstOrDefault(p => p.IsHero);
        var heroBet = hero?.BetAmount ?? 0;

        PlayerSeatState? facingPlayer = null;
        long facing = 0;
        foreach (var player in players.Where(p => !p.IsHero && p.Status != PlayerStatus.Folded))
        {
            var bet = player.BetAmount ?? 0;
            if (bet > facing)
            {
                facing = bet;
                facingPlayer = player;
            }
        }

        var bigBlind = InferBigBlind(players, facing);
        var callAmount = Math.Max(0, facing - heroBet);
        var blindSized = CountBlindSizedBets(players, bigBlind);
        var potLooksUnopened = IsUnopened(facing, bigBlind);
        var stacks = StackAwareSizing.Analyze(state);

        return new Context(
            bigBlind,
            facing,
            heroBet,
            callAmount,
            potLooksUnopened,
            blindSized,
            facingPlayer,
            stacks);
    }

    public static long? SuggestRaiseTo(Context ctx, string? heroPositionChartKey)
    {
        long? raw;
        if (ctx.BigBlind is long bb and > 0 && ctx.PotLooksUnopened)
        {
            // Standard open: 2.5bb; SB open a bit larger (~3bb).
            var openBb = string.Equals(heroPositionChartKey, "SB", StringComparison.OrdinalIgnoreCase)
                ? 3.0
                : 2.5;
            var openTo = (long)Math.Round(bb * openBb);

            // Isolation vs limp(s): size up off the big blind.
            if (ctx.BlindSizedBets >= 3)
            {
                // SB + BB + at least one limp.
                var limpers = ctx.BlindSizedBets - 2;
                raw = (long)Math.Round(bb * (3.5 + 0.5 * Math.Max(0, limpers - 1)));
            }
            else
            {
                raw = Math.Max(openTo, ctx.FacingBet + bb);
            }
        }
        else if (ctx.FacingBet > 0)
        {
            // 3-bet / re-raise: about 3x the bet facing hero.
            var threeBet = (long)Math.Round(ctx.FacingBet * 3.0);
            var minUp = ctx.FacingBet + (ctx.BigBlind ?? Math.Max(1, ctx.FacingBet / 2));
            raw = Math.Max(threeBet, minUp);
        }
        else
        {
            raw = ctx.BigBlind is long blind and > 0
                ? (long)Math.Round(blind * 2.5)
                : null;
        }

        return StackAwareSizing.PreferShortStackShove(raw, ctx.Stacks, ctx.BigBlind);
    }

    private static long? InferBigBlind(IReadOnlyList<PlayerSeatState> players, long facingBet)
    {
        var bbSeat = players.FirstOrDefault(p =>
            p.Position.Equals("big blind", StringComparison.OrdinalIgnoreCase));
        if (bbSeat?.BetAmount is long bbBet and > 0)
        {
            return bbBet;
        }

        var sbSeat = players.FirstOrDefault(p =>
            p.Position.Equals("small blind", StringComparison.OrdinalIgnoreCase));
        if (sbSeat?.BetAmount is long sbBet and > 0)
        {
            return sbBet * 2;
        }

        var positive = players
            .Where(p => p.BetAmount is > 0)
            .Select(p => p.BetAmount!.Value)
            .OrderBy(v => v)
            .ToList();

        if (positive.Count == 0)
        {
            return null;
        }

        var smallest = positive[0];
        if (positive.Any(v => v == smallest * 2))
        {
            return smallest * 2;
        }

        if (facingBet > 0 && facingBet <= smallest * 3)
        {
            return smallest;
        }

        return smallest;
    }

    private static bool IsUnopened(long facing, long? bigBlind)
    {
        if (facing <= 0)
        {
            return true;
        }

        if (bigBlind is long bb and > 0)
        {
            return facing <= bb * 1.15;
        }

        return false;
    }

    private static int CountBlindSizedBets(IReadOnlyList<PlayerSeatState> players, long? bigBlind)
    {
        if (bigBlind is not long bb || bb <= 0)
        {
            return 0;
        }

        return players.Count(p =>
            p.Status != PlayerStatus.Folded
            && p.BetAmount is long bet
            && bet > 0
            && bet <= bb * 1.15);
    }
}
