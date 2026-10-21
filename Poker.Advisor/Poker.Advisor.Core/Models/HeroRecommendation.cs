using System.Globalization;
using Poker.Advisor.Core.Enums;

namespace Poker.Advisor.Core.Models;

public sealed class HeroRecommendation
{
    public required PokerStreet Street { get; init; }
    public required RecommendedAction Action { get; init; }
    public string? Hand { get; init; }
    public string? Position { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public long? FacingBet { get; init; }
    public long? CallAmount { get; init; }
    public long? RaiseToAmount { get; init; }

    public PanelTone Tone => Action switch
    {
        RecommendedAction.Raise => PanelTone.AdviceRaise,
        RecommendedAction.Call => PanelTone.AdviceCall,
        RecommendedAction.Fold => PanelTone.AdviceFold,
        RecommendedAction.Check => PanelTone.AdviceCheck,
        _ => PanelTone.Normal,
    };

    /// <summary>Short panel line: advice: FOLD / CALL 750 / BET 1,250 / RAISE 3,750.</summary>
    public string ToShortPanelText() => $"advice: {ShortActionLabel()}";

    public string ToPanelText()
    {
        var action = ShortActionLabel();
        if (string.IsNullOrWhiteSpace(Summary))
        {
            return $"advice: {action}";
        }

        return $"advice: {action} — {Summary}";
    }

    public string ShortActionLabel() => Action switch
    {
        RecommendedAction.Fold => "FOLD",
        RecommendedAction.Raise => FormatRaiseOrBetLabel(),
        RecommendedAction.Call => FormatCallLabel(),
        RecommendedAction.Check => "CHECK",
        RecommendedAction.Wait => "WAIT",
        _ => "?",
    };

    private string FormatRaiseOrBetLabel()
    {
        // No bet facing → this is a bet into the pot.
        var isBet = FacingBet is null or 0;
        if (RaiseToAmount is long to)
        {
            return isBet ? $"BET {Format(to)}" : $"RAISE {Format(to)}";
        }

        return isBet ? "BET" : "RAISE";
    }

    private string FormatCallLabel()
    {
        if (CallAmount is long call and > 0)
        {
            return $"CALL {Format(call)}";
        }

        return "CALL";
    }

    private static string Format(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);
}
