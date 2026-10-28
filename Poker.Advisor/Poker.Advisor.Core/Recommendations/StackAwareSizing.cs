using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Classes;

namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Effective-stack / SPR helpers so bet and raise sizes extract max from the shorter stack
/// and avoid suggesting amounts nobody can call.
/// </summary>
public static class StackAwareSizing
{
    public sealed record Context(
        long? HeroStack,
        long? VillainStack,
        long EffectiveStack,
        long? Pot,
        double? Spr,
        long HeroBet,
        long FacingBet,
        PlayerSeatState? Villain);

    /// <summary>
    /// Focus villain: largest bet (aggressor), else shortest stack among active opponents
    /// (best cover target for value).
    /// </summary>
    public static Context Analyze(TableState state)
    {
        var players = state.PlayersClockwiseFromHero;
        var hero = players.FirstOrDefault(p => p.IsHero);
        var heroBet = hero?.BetAmount ?? 0;
        var heroStack = hero?.BankAmount;

        var activeVillains = players
            .Where(p => !p.IsHero && p.Status != PlayerStatus.Folded)
            .ToList();

        PlayerSeatState? aggressor = null;
        long facing = 0;
        foreach (var v in activeVillains)
        {
            var bet = v.BetAmount ?? 0;
            if (bet > facing)
            {
                facing = bet;
                aggressor = v;
            }
        }

        var coverTarget = aggressor
            ?? activeVillains
                .Where(v => v.BankAmount is > 0)
                .OrderBy(v => v.BankAmount)
                .FirstOrDefault()
            ?? activeVillains.FirstOrDefault();

        var villainStack = coverTarget?.BankAmount;
        var effective = ComputeEffective(heroStack, villainStack);
        var pot = state.PotAmount;
        double? spr = pot is long p and > 0 && effective > 0
            ? effective / (double)p
            : null;

        return new Context(heroStack, villainStack, effective, pot, spr, heroBet, facing, coverTarget);
    }

    /// <summary>
    /// Cap a bet-into-pot size so hero can put it in and the primary villain can call it.
    /// </summary>
    public static long? CapBet(long? suggested, Context stacks)
    {
        if (suggested is not long raw || raw <= 0)
        {
            return suggested;
        }

        var capped = raw;
        if (stacks.HeroStack is long hs and >= 0)
        {
            capped = Math.Min(capped, Math.Max(1, hs));
        }

        if (stacks.VillainStack is long vs and >= 0)
        {
            // Villain can only put in their remaining stack (plus they may already have a bet in).
            capped = Math.Min(capped, Math.Max(1, vs));
        }

        return capped;
    }

    /// <summary>
    /// Cap a raise-to total: additional chips cannot exceed hero stack; prefer covering villain.
    /// </summary>
    public static long? CapRaiseTo(long? suggestedRaiseTo, Context stacks)
    {
        if (suggestedRaiseTo is not long to || to <= 0)
        {
            return suggestedRaiseTo;
        }

        var capped = to;

        if (stacks.HeroStack is long hs and >= 0)
        {
            var maxTo = stacks.HeroBet + Math.Max(0, hs);
            capped = Math.Min(capped, Math.Max(stacks.FacingBet + 1, maxTo));
        }

        if (stacks.VillainStack is long vs and >= 0)
        {
            // Max villain can match this street: their current bet + remaining stack.
            var villainMaxTo = stacks.FacingBet + Math.Max(0, vs);
            if (villainMaxTo > stacks.FacingBet)
            {
                capped = Math.Min(capped, villainMaxTo);
            }
        }

        // Never suggest a raise-to below facing (invalid).
        if (stacks.FacingBet > 0 && capped <= stacks.FacingBet)
        {
            return stacks.HeroStack is long heroRem
                ? stacks.HeroBet + Math.Max(0, heroRem)
                : stacks.FacingBet * 2;
        }

        return capped;
    }

    /// <summary>
    /// When SPR is low and we want value, shove the effective stack instead of a tiny pot %.
    /// </summary>
    public static long? PreferCommitBet(long? suggested, Context stacks, bool strongValue)
    {
        if (!strongValue || stacks.EffectiveStack <= 0)
        {
            return CapBet(suggested, stacks);
        }

        if (stacks.Spr is double spr && spr <= 2.5)
        {
            return CapBet(stacks.EffectiveStack, stacks);
        }

        // If a pot-sized bet would leave villain with a tiny leftover, just cover them.
        if (suggested is long raw
            && stacks.VillainStack is long vs
            && vs > 0
            && raw >= vs * 0.75)
        {
            return CapBet(vs, stacks);
        }

        return CapBet(suggested, stacks);
    }

    public static long? PreferCommitRaiseTo(long? suggested, Context stacks, bool strongValue)
    {
        if (!strongValue || stacks.EffectiveStack <= 0)
        {
            return CapRaiseTo(suggested, stacks);
        }

        if (stacks.Spr is double spr && spr <= 2.5)
        {
            var shoveTo = stacks.HeroBet + stacks.EffectiveStack;
            return CapRaiseTo(shoveTo, stacks);
        }

        if (suggested is long to
            && stacks.VillainStack is long vs
            && vs > 0)
        {
            var coverTo = stacks.FacingBet + vs;
            var add = Math.Max(0, to - stacks.HeroBet);
            if (add >= vs * 0.75)
            {
                return CapRaiseTo(coverTo, stacks);
            }
        }

        return CapRaiseTo(suggested, stacks);
    }

    /// <summary>
    /// True when calling would commit a large share of hero's stack (avoid with marginal hands).
    /// </summary>
    public static bool CallIsCommitting(Context stacks, long callAmount, double threshold = 0.40)
    {
        if (callAmount <= 0 || stacks.HeroStack is not long hs || hs <= 0)
        {
            return false;
        }

        return callAmount >= hs * threshold;
    }

    /// <summary>
    /// Short-stack open/3-bet: if effective ≤ ~15bb, prefer shoving remaining stack.
    /// </summary>
    public static long? PreferShortStackShove(long? suggestedRaiseTo, Context stacks, long? bigBlind)
    {
        if (stacks.EffectiveStack <= 0 || bigBlind is not long bb || bb <= 0)
        {
            return CapRaiseTo(suggestedRaiseTo, stacks);
        }

        var effBb = stacks.EffectiveStack / (double)bb;
        if (effBb <= 15)
        {
            return CapRaiseTo(stacks.HeroBet + stacks.EffectiveStack, stacks);
        }

        return CapRaiseTo(suggestedRaiseTo, stacks);
    }

    private static long ComputeEffective(long? heroStack, long? villainStack)
    {
        if (heroStack is long h && villainStack is long v)
        {
            return Math.Max(0, Math.Min(h, v));
        }

        if (heroStack is long onlyHero)
        {
            return Math.Max(0, onlyHero);
        }

        if (villainStack is long onlyVillain)
        {
            return Math.Max(0, onlyVillain);
        }

        return 0;
    }
}
