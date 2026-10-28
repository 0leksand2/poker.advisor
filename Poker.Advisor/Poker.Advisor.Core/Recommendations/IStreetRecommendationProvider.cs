using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Classes;

namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Street-specific hero advice. Add flop/turn/river providers later without changing callers.
/// </summary>
public interface IStreetRecommendationProvider
{
    PokerStreet Street { get; }

    bool IsImplemented { get; }

    HeroRecommendation Advise(TableState state);
}
