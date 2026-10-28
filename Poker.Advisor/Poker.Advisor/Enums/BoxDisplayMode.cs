namespace Poker.Advisor.Enums;

internal enum BoxDisplayMode
{
    /// <summary>No detection boxes (advice panel still updates).</summary>
    Off,

    /// <summary>No boxes — advice panel only.</summary>
    AdviceOnly,

    /// <summary>Tables, cards, players — brackets, short labels, no conf.</summary>
    Minimal,

    /// <summary>All detections — brackets + soft fill + name + conf.</summary>
    All,
}
