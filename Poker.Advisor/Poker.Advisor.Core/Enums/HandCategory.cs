namespace Poker.Advisor.Core.Enums;

/// <summary>
/// Coarse postflop hand classes used for action heuristics.
/// </summary>
public enum HandCategory
{
    Junk = 0,
    WeakShowdown,   // ace-high / weak overcards
    WeakDraw,       // gutshot / backdoor
    StrongDraw,     // flush draw / OESD (+ combo draws)
    WeakPair,       // bottom / underpair
    MiddlePair,
    TopPairWeak,    // top pair weak kicker
    TopPairGood,    // top pair good kicker / overpair
    TwoPairOrSet,   // two pair, trips, set
    Monster,        // straight+, full house, quads, straight flush
}
