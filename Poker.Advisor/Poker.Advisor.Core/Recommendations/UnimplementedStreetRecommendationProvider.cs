using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Models;

namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Placeholder for flop / turn / river. Replace with real providers later.
/// </summary>
public sealed class UnimplementedStreetRecommendationProvider : IStreetRecommendationProvider
{
    public UnimplementedStreetRecommendationProvider(PokerStreet street)
    {
        if (street is PokerStreet.Preflop or PokerStreet.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(street), street, "Use a postflop street.");
        }

        Street = street;
    }

    public PokerStreet Street { get; }

    public bool IsImplemented => false;

    public HeroRecommendation Advise(TableState state) =>
        new()
        {
            Street = Street,
            Action = RecommendedAction.Wait,
            Hand = state.HeroCards.Count == 2 && StartingHand.TryParse(state.HeroCards, out var hand)
                ? hand.Notation
                : null,
            Position = state.HeroPosition,
            Summary = $"{Street.ToString().ToLowerInvariant()} advice not implemented yet",
            Detail = "Extension point reserved for future street logic.",
        };
}
