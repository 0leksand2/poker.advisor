using System.Globalization;
using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Classes;

namespace Poker.Advisor.Core.Recommendations.Postflop;

/// <summary>
/// Flop / turn / river advice from hero hand strength + opponent bets / multiway pressure.
/// Heuristic rules inspired by common postflop categories (value / draws / junk).
/// </summary>
public sealed class PostflopRecommendationProvider : IStreetRecommendationProvider
{
    public PostflopRecommendationProvider(PokerStreet street)
    {
        if (street is not (PokerStreet.Flop or PokerStreet.Turn or PokerStreet.River))
        {
            throw new ArgumentOutOfRangeException(nameof(street), street, "Postflop street required.");
        }

        Street = street;
    }

    public PokerStreet Street { get; }

    public bool IsImplemented => true;

    public HeroRecommendation Advise(TableState state)
    {
        if (!state.HasHeroCards || state.HeroCards.Count != 2)
        {
            return Wait("need both hole cards");
        }

        if (state.CommunityCards.Count < 3)
        {
            return Wait("need board cards");
        }

        if (!StartingHand.TryParse(state.HeroCards, out var starting)
            || !PostflopHandEvaluator.TryAssess(state.HeroCards, state.CommunityCards, out var hand))
        {
            return Wait("could not read hand/board");
        }

        var spot = PostflopSituation.FromTable(state);
        hand = new PostflopHandAssessment
        {
            Category = hand.Category,
            Label = hand.Label,
            AbsoluteRank = hand.AbsoluteRank,
            HasFlushDraw = hand.HasFlushDraw,
            HasOpenEndedStraightDraw = hand.HasOpenEndedStraightDraw,
            HasGutshot = hand.HasGutshot,
            HasBackdoorFlushDraw = hand.HasBackdoorFlushDraw,
            ActiveOpponents = spot.ActiveOpponents,
        };

        return spot.FacingAggression
            ? AdviseFacingBet(starting.Notation, hand, spot)
            : AdviseCheckedTo(starting.Notation, hand, spot);
    }

    private HeroRecommendation AdviseFacingBet(
        string starting,
        PostflopHandAssessment hand,
        PostflopSituation spot)
    {
        var multiway = spot.ActiveOpponents >= 2;
        var heavy = spot.BetPotRatio >= 0.75;
        var street = Street;

        var committing = StackAwareSizing.CallIsCommitting(spot.Stacks, spot.CallAmount);

        // Monsters / two pair+ : raise for value (call huge multiway river bets).
        if (hand.Category >= HandCategory.TwoPairOrSet)
        {
            if (street == PokerStreet.River && heavy && multiway && hand.Category < HandCategory.Monster)
            {
                return Call(starting, hand, spot, "strong made · call heavy multiway");
            }

            return Raise(starting, hand, spot, "value raise", strongValue: true);
        }

        // Top pair / overpair
        if (hand.Category == HandCategory.TopPairGood)
        {
            if (heavy && multiway)
            {
                return Call(starting, hand, spot, "top pair · call vs pressure");
            }

            if (heavy && street == PokerStreet.River)
            {
                return Call(starting, hand, spot, "top pair · river call");
            }

            // Deep / committing: don't raise-thin for stacks; just call.
            if (committing && street == PokerStreet.River)
            {
                return Call(starting, hand, spot, "top pair · call commit");
            }

            if (!multiway && street != PokerStreet.River && spot.BetPotRatio <= 0.55 && !committing)
            {
                return Raise(starting, hand, spot, "top pair · raise thin");
            }

            return Call(starting, hand, spot, "top pair · call");
        }

        if (hand.Category == HandCategory.TopPairWeak)
        {
            if (heavy || committing || (multiway && street == PokerStreet.River))
            {
                return Fold(starting, hand, spot, "weak top pair · fold to pressure");
            }

            return Call(starting, hand, spot, "weak top pair · call");
        }

        // Draws (flop/turn only)
        if (street != PokerStreet.River && hand.IsPlayableDraw)
        {
            var combo = hand.HasFlushDraw && hand.HasOpenEndedStraightDraw;
            if (combo && !heavy && !committing)
            {
                return Raise(starting, hand, spot, "combo draw · semi-bluff");
            }

            if ((spot.BetPotRatio <= 0.70 || hand.HasFlushDraw) && !committing)
            {
                return Call(starting, hand, spot, "draw · call");
            }

            if (committing && !combo)
            {
                return Fold(starting, hand, spot, "draw · too committing");
            }

            return Fold(starting, hand, spot, "draw · too expensive");
        }

        if (hand.Category is HandCategory.MiddlePair or HandCategory.WeakPair)
        {
            if (heavy || committing || multiway || street == PokerStreet.River)
            {
                return Fold(starting, hand, spot, "weak pair · fold");
            }

            if (spot.BetPotRatio <= 0.40)
            {
                return Call(starting, hand, spot, "pair · peel cheap");
            }

            return Fold(starting, hand, spot, "pair · fold");
        }

        if (hand.Category is HandCategory.WeakShowdown or HandCategory.WeakDraw)
        {
            if (!heavy && !committing && street == PokerStreet.Flop && spot.BetPotRatio <= 0.35 && spot.ActiveOpponents == 1)
            {
                return Call(starting, hand, spot, "float / weak equity");
            }

            return Fold(starting, hand, spot, "no showdown value");
        }

        return Fold(starting, hand, spot, "air · fold");
    }

    private HeroRecommendation AdviseCheckedTo(
        string starting,
        PostflopHandAssessment hand,
        PostflopSituation spot)
    {
        var multiway = spot.ActiveOpponents >= 2;

        if (hand.Category >= HandCategory.TwoPairOrSet)
        {
            return Bet(
                starting,
                hand,
                spot,
                "value bet",
                large: multiway || Street == PokerStreet.River,
                strongValue: true);
        }

        if (hand.Category == HandCategory.TopPairGood)
        {
            return Bet(starting, hand, spot, "value / protect", large: multiway, strongValue: true);
        }

        if (hand.Category == HandCategory.TopPairWeak)
        {
            if (multiway)
            {
                return Check(starting, hand, spot, "weak top pair multiway · check");
            }

            return Bet(starting, hand, spot, "thin value", large: false);
        }

        if (Street != PokerStreet.River && hand.IsPlayableDraw)
        {
            if (spot.ActiveOpponents <= 2)
            {
                return Bet(starting, hand, spot, "semi-bluff", large: false);
            }

            return Check(starting, hand, spot, "draw multiway · check");
        }

        if (hand.Category is HandCategory.MiddlePair)
        {
            if (Street == PokerStreet.River && !multiway)
            {
                return Bet(starting, hand, spot, "thin river value", large: false);
            }

            return Check(starting, hand, spot, "middle pair · check");
        }

        if (hand.Category is HandCategory.WeakShowdown && Street == PokerStreet.Flop && !multiway)
        {
            return Bet(starting, hand, spot, "c-bet air/overcards", large: false);
        }

        return Check(starting, hand, spot, "give up / pot control");
    }

    private HeroRecommendation Bet(
        string starting,
        PostflopHandAssessment hand,
        PostflopSituation spot,
        string why,
        bool large,
        bool strongValue = false)
    {
        var size = spot.SuggestBetSize(large, strongValue);
        return new HeroRecommendation
        {
            Street = Street,
            Action = RecommendedAction.Raise,
            Hand = starting,
            RaiseToAmount = size,
            FacingBet = null,
            CallAmount = null,
            Summary = FormatSummary(starting, hand, spot, why, size, isBet: true),
            Detail = "Postflop value/semi-bluff bet from hand class + board + stacks.",
        };
    }

    private HeroRecommendation Raise(
        string starting,
        PostflopHandAssessment hand,
        PostflopSituation spot,
        string why,
        bool strongValue = false)
    {
        var to = spot.SuggestRaiseTo(strongValue);
        return new HeroRecommendation
        {
            Street = Street,
            Action = RecommendedAction.Raise,
            Hand = starting,
            RaiseToAmount = to,
            FacingBet = spot.FacingBet > 0 ? spot.FacingBet : null,
            CallAmount = spot.CallAmount > 0 ? spot.CallAmount : null,
            Summary = FormatSummary(starting, hand, spot, why, to, isBet: false),
            Detail = "Postflop raise vs opponent bet (stack-capped).",
        };
    }

    private HeroRecommendation Call(
        string starting,
        PostflopHandAssessment hand,
        PostflopSituation spot,
        string why)
    {
        return new HeroRecommendation
        {
            Street = Street,
            Action = RecommendedAction.Call,
            Hand = starting,
            FacingBet = spot.FacingBet > 0 ? spot.FacingBet : null,
            CallAmount = spot.CallAmount > 0 ? spot.CallAmount : null,
            Summary = FormatSummary(starting, hand, spot, why, size: null, isBet: false),
            Detail = "Postflop call from pot odds / hand class.",
        };
    }

    private HeroRecommendation Check(
        string starting,
        PostflopHandAssessment hand,
        PostflopSituation spot,
        string why)
    {
        return new HeroRecommendation
        {
            Street = Street,
            Action = RecommendedAction.Check,
            Hand = starting,
            Summary = FormatSummary(starting, hand, spot, why, size: null, isBet: false),
            Detail = "No bet facing — check.",
        };
    }

    private HeroRecommendation Fold(
        string starting,
        PostflopHandAssessment hand,
        PostflopSituation spot,
        string why)
    {
        return new HeroRecommendation
        {
            Street = Street,
            Action = RecommendedAction.Fold,
            Hand = starting,
            FacingBet = spot.FacingBet > 0 ? spot.FacingBet : null,
            CallAmount = spot.CallAmount > 0 ? spot.CallAmount : null,
            Summary = FormatSummary(starting, hand, spot, why, size: null, isBet: false),
            Detail = "Fold to aggression.",
        };
    }

    private static string FormatSummary(
        string starting,
        PostflopHandAssessment hand,
        PostflopSituation spot,
        string why,
        long? size,
        bool isBet)
    {
        var parts = new List<string>
        {
            $"{starting} · {hand.Label}",
            why,
        };

        if (spot.ActiveOpponents > 0)
        {
            parts.Add($"{spot.ActiveOpponents} vill");
        }

        if (spot.FacingAggression && spot.CallAmount > 0)
        {
            parts.Add($"face {Format(spot.FacingBet)}");
        }

        if (size is long s)
        {
            parts.Add(isBet ? $"bet {Format(s)}" : $"to {Format(s)}");
        }

        if (spot.Stacks.EffectiveStack > 0
            && size is long sized
            && sized >= spot.Stacks.EffectiveStack * 0.9)
        {
            parts.Add("cover");
        }
        else if (spot.Stacks.Spr is double spr)
        {
            parts.Add($"SPR {spr:0.#}");
        }

        return string.Join(" · ", parts);
    }

    private static string Format(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private HeroRecommendation Wait(string reason) =>
        new()
        {
            Street = Street,
            Action = RecommendedAction.Wait,
            Summary = reason,
        };
}
