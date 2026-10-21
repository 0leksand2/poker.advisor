using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Models;
using Poker.Advisor.Core.Recommendations.Postflop;

namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Adjusts chart/heuristic advice using tracked villain habits (VPIP/PFR/aggression).
/// </summary>
public static class PlayerStyleAdvisor
{
    public static HeroRecommendation Adjust(HeroRecommendation advice, TableState state)
    {
        var aggressor = state.PlayersClockwiseFromHero
            .Where(p => !p.IsHero && p.Status != PlayerStatus.Folded)
            .OrderByDescending(p => p.BetAmount ?? 0)
            .FirstOrDefault();

        var villain = aggressor?.Stats
            ?? state.PlayersClockwiseFromHero
                .Where(p => !p.IsHero && p.Status != PlayerStatus.Folded && p.Stats is not null)
                .Select(p => p.Stats!)
                .FirstOrDefault();

        if (villain is null || villain.Hands < 3 || villain.Style == PlayerStyle.Unknown)
        {
            return advice;
        }

        var spot = PostflopSituation.FromTable(state);
        var tag = $"{villain.DisplayName} {villain.StyleLabel}";

        // Facing LAG/maniac pressure → fold marginal continues.
        if (spot.FacingAggression
            && villain.Style is PlayerStyle.LooseAggressive or PlayerStyle.Aggressive
            && advice.Action is RecommendedAction.Call or RecommendedAction.Raise
            && state.Street != PokerStreet.Preflop
            && IsMarginalContinue(advice))
        {
            return Clone(advice, RecommendedAction.Fold, raiseTo: null, note: $"fold vs {tag} pressure");
        }

        if (state.Street == PokerStreet.Preflop
            && spot.FacingAggression
            && advice.Action == RecommendedAction.Call
            && villain.Style is PlayerStyle.LooseAggressive
            && villain.Pfr >= 28)
        {
            return WithNote(advice, $"wide opener {tag}");
        }

        // Stations / passive: thin value; peel cheap; respect nit bets.
        if (villain.Style is PlayerStyle.CallingStation or PlayerStyle.Passive or PlayerStyle.TightPassive)
        {
            if (!spot.FacingAggression
                && advice.Action == RecommendedAction.Check
                && state.Street != PokerStreet.Preflop)
            {
                var size = spot.SuggestBetSize(large: villain.Style == PlayerStyle.CallingStation);
                if (size is long betTo)
                {
                    return new HeroRecommendation
                    {
                        Street = advice.Street,
                        Action = RecommendedAction.Raise,
                        Hand = advice.Hand,
                        Position = advice.Position,
                        FacingBet = null,
                        RaiseToAmount = betTo,
                        Summary = WithSummary(advice.Summary, $"value vs {tag} · bet {betTo:N0}"),
                        Detail = $"Thin value vs {villain.StyleLabel} ({villain.ShortHud}).",
                    };
                }
            }

            if (spot.FacingAggression
                && advice.Action == RecommendedAction.Fold
                && villain.Style is PlayerStyle.TightPassive or PlayerStyle.Passive)
            {
                return WithNote(advice, $"nit bet {tag}");
            }

            if (spot.FacingAggression
                && advice.Action == RecommendedAction.Fold
                && villain.Style == PlayerStyle.CallingStation
                && spot.BetPotRatio <= 0.45)
            {
                return Clone(
                    advice,
                    RecommendedAction.Call,
                    raiseTo: null,
                    call: spot.CallAmount > 0 ? spot.CallAmount : advice.CallAmount,
                    note: $"peel vs {tag}");
            }
        }

        // TAG/nit open: prefer flat over light 3-bet.
        if (state.Street == PokerStreet.Preflop
            && spot.FacingAggression
            && villain.Style is PlayerStyle.TightAggressive or PlayerStyle.TightPassive
            && advice.Action == RecommendedAction.Raise)
        {
            return Clone(
                advice,
                RecommendedAction.Call,
                raiseTo: null,
                call: spot.CallAmount > 0 ? spot.CallAmount : advice.CallAmount,
                note: $"vs {tag} · flat");
        }

        if (advice.Action is not RecommendedAction.Wait and not RecommendedAction.Unknown)
        {
            return WithNote(advice, tag);
        }

        return advice;
    }

    private static bool IsMarginalContinue(HeroRecommendation advice)
    {
        var s = advice.Summary ?? string.Empty;
        return s.Contains("weak", StringComparison.OrdinalIgnoreCase)
            || s.Contains("middle pair", StringComparison.OrdinalIgnoreCase)
            || s.Contains("peel", StringComparison.OrdinalIgnoreCase)
            || s.Contains("float", StringComparison.OrdinalIgnoreCase)
            || s.Contains("thin", StringComparison.OrdinalIgnoreCase);
    }

    private static HeroRecommendation WithNote(HeroRecommendation advice, string note) =>
        Clone(advice, advice.Action, advice.RaiseToAmount, advice.CallAmount, note);

    private static HeroRecommendation Clone(
        HeroRecommendation advice,
        RecommendedAction action,
        long? raiseTo,
        long? call = null,
        string? note = null)
    {
        return new HeroRecommendation
        {
            Street = advice.Street,
            Action = action,
            Hand = advice.Hand,
            Position = advice.Position,
            FacingBet = advice.FacingBet,
            CallAmount = call ?? advice.CallAmount,
            RaiseToAmount = raiseTo,
            Summary = note is null ? advice.Summary : WithSummary(advice.Summary, note),
            Detail = advice.Detail,
        };
    }

    private static string WithSummary(string summary, string note)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return note;
        }

        return summary.Contains(note, StringComparison.OrdinalIgnoreCase)
            ? summary
            : $"{summary} · {note}";
    }
}
