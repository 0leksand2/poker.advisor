using Poker.Advisor.Core.Enums;

namespace Poker.Advisor.Core.Recommendations.Postflop;

public sealed class PostflopHandAssessment
{
    public required HandCategory Category { get; init; }
    public required string Label { get; init; }
    public required int AbsoluteRank { get; init; }
    public bool HasFlushDraw { get; init; }
    public bool HasOpenEndedStraightDraw { get; init; }
    public bool HasGutshot { get; init; }
    public bool HasBackdoorFlushDraw { get; init; }
    public int ActiveOpponents { get; init; }

    public bool IsStrongMade => Category >= HandCategory.TopPairGood;
    public bool IsPlayableDraw => HasFlushDraw || HasOpenEndedStraightDraw || Category == HandCategory.StrongDraw;
}
