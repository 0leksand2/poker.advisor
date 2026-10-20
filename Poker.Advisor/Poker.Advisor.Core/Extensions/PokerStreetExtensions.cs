using Poker.Advisor.Core.Enums;

namespace Poker.Advisor.Core.Extensions;

public static class PokerStreetExtensions
{
    public static PokerStreet FromCommunityCount(int count) => count switch
    {
        0 => PokerStreet.Preflop,
        3 => PokerStreet.Flop,
        4 => PokerStreet.Turn,
        5 => PokerStreet.River,
        _ => PokerStreet.Unknown,
    };

    public static string ToDisplay(this PokerStreet street) => street switch
    {
        PokerStreet.Preflop => "pre-flop",
        PokerStreet.Flop => "flop",
        PokerStreet.Turn => "turn",
        PokerStreet.River => "river",
        _ => "unknown",
    };
}
