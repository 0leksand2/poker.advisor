using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Models;

namespace Poker.Advisor.Core.Recommendations.Postflop;

/// <summary>
/// Opponent action snapshot for the current street (from OCR bets + fold status).
/// </summary>
public sealed class PostflopSituation
{
    public required int ActiveOpponents { get; init; }
    public required long FacingBet { get; init; }
    public required long HeroBet { get; init; }
    public required long CallAmount { get; init; }
    public required long? Pot { get; init; }
    public required bool FacingAggression { get; init; }
    public required double BetPotRatio { get; init; }
    public PlayerSeatState? Aggressor { get; init; }

    public static PostflopSituation FromTable(TableState state)
    {
        var players = state.PlayersClockwiseFromHero;
        var hero = players.FirstOrDefault(p => p.IsHero);
        var heroBet = hero?.BetAmount ?? 0;

        var activeOpponents = players.Count(p => !p.IsHero && p.Status != PlayerStatus.Folded);

        PlayerSeatState? aggressor = null;
        long facing = 0;
        foreach (var player in players.Where(p => !p.IsHero && p.Status != PlayerStatus.Folded))
        {
            var bet = player.BetAmount ?? 0;
            if (bet > facing)
            {
                facing = bet;
                aggressor = player;
            }
        }

        var call = Math.Max(0, facing - heroBet);
        var pot = state.PotAmount;
        var ratio = pot is long p and > 0 ? call / (double)p : 0;
        var facingAggression = call > 0;

        return new PostflopSituation
        {
            ActiveOpponents = activeOpponents,
            FacingBet = facing,
            HeroBet = heroBet,
            CallAmount = call,
            Pot = pot,
            FacingAggression = facingAggression,
            BetPotRatio = ratio,
            Aggressor = aggressor,
        };
    }

    public long? SuggestBetSize(bool large = false)
    {
        if (Pot is long pot and > 0)
        {
            var fraction = large ? 0.75 : 0.50;
            return Math.Max(1, (long)Math.Round(pot * fraction));
        }

        if (FacingBet > 0)
        {
            return Math.Max(FacingBet * 2, FacingBet + Math.Max(1, FacingBet / 2));
        }

        return null;
    }

    public long? SuggestRaiseTo()
    {
        if (FacingBet <= 0)
        {
            return SuggestBetSize();
        }

        // Standard raise ~3x bet, or pot-ish.
        var byFacing = (long)Math.Round(FacingBet * 3.0);
        if (Pot is long pot and > 0)
        {
            var byPot = FacingBet + pot;
            return Math.Max(byFacing, byPot);
        }

        return byFacing;
    }
}
