using System.Globalization;
using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Classes;

namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Preflop advice from position + hole cards using published 6-max opening charts.
/// Raise advice includes a size based on the largest bet facing hero.
/// </summary>
public sealed class PreflopRecommendationProvider : IStreetRecommendationProvider
{
    public PokerStreet Street => PokerStreet.Preflop;

    public bool IsImplemented => true;

    public HeroRecommendation Advise(TableState state)
    {
        if (!state.HasHeroCards || state.HeroCards.Count != 2)
        {
            return Wait("need both hole cards");
        }

        if (!StartingHand.TryParse(state.HeroCards, out var hand))
        {
            return Wait("could not read hole cards");
        }

        var chartKey = PreflopOpeningCharts.ResolveChartKey(state.HeroPosition);
        if (chartKey is null || !PreflopOpeningCharts.TryGetChart(chartKey, out var chart))
        {
            return new HeroRecommendation
            {
                Street = Street,
                Action = RecommendedAction.Wait,
                Hand = hand.Notation,
                Position = state.HeroPosition,
                Summary = $"{hand.Notation} · unknown position",
                Detail = "Map hero seat to a standard position before advising.",
            };
        }

        var sizing = PreflopBetSizing.Analyze(state);
        var inRange = PreflopOpeningCharts.Contains(chartKey, hand.Notation);

        if (chart.IsDefend)
        {
            if (!inRange)
            {
                return new HeroRecommendation
                {
                    Street = Street,
                    Action = RecommendedAction.Fold,
                    Hand = hand.Notation,
                    Position = state.HeroPosition,
                    FacingBet = sizing.FacingBet > 0 ? sizing.FacingBet : null,
                    CallAmount = sizing.CallAmount > 0 ? sizing.CallAmount : null,
                    Summary = $"{hand.Notation} from {chart.Label} · outside defend range",
                    Detail = "BB chart assumes facing a button open (Poker Skill).",
                };
            }

            // Facing a shove / large commit: don't auto-call chart hands if price is awful.
            if (StackAwareSizing.CallIsCommitting(sizing.Stacks, sizing.CallAmount, threshold: 0.35)
                && sizing.BigBlind is long bb
                && sizing.CallAmount > bb * 8)
            {
                return new HeroRecommendation
                {
                    Street = Street,
                    Action = RecommendedAction.Fold,
                    Hand = hand.Notation,
                    Position = state.HeroPosition,
                    FacingBet = sizing.FacingBet > 0 ? sizing.FacingBet : null,
                    CallAmount = sizing.CallAmount > 0 ? sizing.CallAmount : null,
                    Summary = $"{hand.Notation} from {chart.Label} · fold vs commit",
                    Detail = "Defend chart is for opens; folding vs large commit with non-premium.",
                };
            }

            return new HeroRecommendation
            {
                Street = Street,
                Action = RecommendedAction.Call,
                Hand = hand.Notation,
                Position = state.HeroPosition,
                FacingBet = sizing.FacingBet > 0 ? sizing.FacingBet : null,
                CallAmount = sizing.CallAmount > 0 ? sizing.CallAmount : null,
                Summary = BuildCallSummary(hand.Notation, chart.Label, chart.Percent, sizing),
                Detail = "BB defend chart (Poker Skill) vs a button-sized open.",
            };
        }

        if (!inRange)
        {
            return new HeroRecommendation
            {
                Street = Street,
                Action = RecommendedAction.Fold,
                Hand = hand.Notation,
                Position = state.HeroPosition,
                FacingBet = sizing.FacingBet > 0 ? sizing.FacingBet : null,
                CallAmount = sizing.CallAmount > 0 ? sizing.CallAmount : null,
                Summary = $"{hand.Notation} from {chart.Label} · outside open range",
                Detail = "Raise-first-in chart for an unopened pot (Poker Skill 6-max ~100bb).",
            };
        }

        var raiseTo = PreflopBetSizing.SuggestRaiseTo(sizing, chartKey);
        var covering = raiseTo is long rt
            && sizing.Stacks.EffectiveStack > 0
            && rt >= sizing.HeroBet + sizing.Stacks.EffectiveStack * 0.9;
        return new HeroRecommendation
        {
            Street = Street,
            Action = RecommendedAction.Raise,
            Hand = hand.Notation,
            Position = state.HeroPosition,
            FacingBet = sizing.FacingBet > 0 ? sizing.FacingBet : null,
            CallAmount = sizing.CallAmount > 0 ? sizing.CallAmount : null,
            RaiseToAmount = raiseTo,
            Summary = BuildRaiseSummary(hand.Notation, chart.Label, chart.Percent, sizing, raiseTo, covering),
            Detail = covering
                ? "Short/effective stack shove — max pressure vs cover."
                : sizing.PotLooksUnopened
                    ? "Open-raise size from blinds / limps ahead (stack-capped)."
                    : "Re-raise size ~3x facing (stack-capped).",
        };
    }

    private static string BuildRaiseSummary(
        string hand,
        string label,
        double percent,
        PreflopBetSizing.Context sizing,
        long? raiseTo,
        bool covering)
    {
        var parts = new List<string>
        {
            $"{hand} from {label}",
            sizing.PotLooksUnopened ? $"open ~{percent:0.#}%" : $"3-bet · chart ~{percent:0.#}%",
        };

        if (sizing.FacingBet > 0)
        {
            parts.Add($"face {Format(sizing.FacingBet)}");
        }

        if (raiseTo is long to)
        {
            parts.Add(covering ? $"shove {Format(to)}" : $"to {Format(to)}");
        }

        return string.Join(" · ", parts);
    }

    private static string BuildCallSummary(
        string hand,
        string label,
        double percent,
        PreflopBetSizing.Context sizing)
    {
        var parts = new List<string>
        {
            $"{hand} from {label}",
            $"defend ~{percent:0.#}%",
        };

        if (sizing.CallAmount > 0)
        {
            parts.Add($"call {Format(sizing.CallAmount)}");
        }
        else if (sizing.FacingBet > 0)
        {
            parts.Add($"face {Format(sizing.FacingBet)}");
        }

        return string.Join(" · ", parts);
    }

    private static string Format(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static HeroRecommendation Wait(string reason) =>
        new()
        {
            Street = PokerStreet.Preflop,
            Action = RecommendedAction.Wait,
            Summary = reason,
        };
}
