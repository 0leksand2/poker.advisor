using Poker.Advisor.Core.Enums;

namespace Poker.Advisor.Core.Classes;

/// <summary>
/// Session HUD-style stats for a tracked nickname.
/// </summary>
public sealed class PlayerStats
{
    public required string Key { get; init; }
    public required string DisplayName { get; set; }
    public int Hands { get; set; }
    public int VpipHands { get; set; }
    public int PfrHands { get; set; }
    public int PostflopBetsRaises { get; set; }
    public int PostflopCalls { get; set; }
    public string? LastAction { get; set; }

    public double Vpip => Hands <= 0 ? 0 : 100.0 * VpipHands / Hands;
    public double Pfr => Hands <= 0 ? 0 : 100.0 * PfrHands / Hands;

    /// <summary>Aggression factor ≈ (bets+raises) / calls. Null when no postflop calls yet.</summary>
    public double? AggressionFactor =>
        PostflopCalls <= 0
            ? PostflopBetsRaises > 0 ? 99.0 : null
            : (double)PostflopBetsRaises / PostflopCalls;

    public PlayerStyle Style
    {
        get
        {
            if (Hands < 3)
            {
                return PlayerStyle.Unknown;
            }

            var loose = Vpip >= 38;
            var tight = Vpip <= 18;
            var af = AggressionFactor ?? (Pfr >= 18 ? 3.0 : 1.0);
            var agg = af >= 2.2 || Pfr >= 22;
            var passive = af < 1.15 && Pfr <= 12;

            if (loose && agg)
            {
                return PlayerStyle.LooseAggressive;
            }

            if (loose && passive)
            {
                return PlayerStyle.CallingStation;
            }

            if (tight && agg)
            {
                return PlayerStyle.TightAggressive;
            }

            if (tight && passive)
            {
                return PlayerStyle.TightPassive;
            }

            return agg ? PlayerStyle.Aggressive : PlayerStyle.Passive;
        }
    }

    public string ShortHud
    {
        get
        {
            var af = AggressionFactor is double a
                ? (a >= 50 ? "AF∞" : $"AF{a:0.0}")
                : "AF-";
            return $"VPIP {Vpip:0}/{Pfr:0} {af} n={Hands}";
        }
    }

    public string StyleLabel => Style switch
    {
        PlayerStyle.LooseAggressive => "LAG",
        PlayerStyle.CallingStation => "station",
        PlayerStyle.TightAggressive => "TAG",
        PlayerStyle.TightPassive => "nit",
        PlayerStyle.Aggressive => "aggro",
        PlayerStyle.Passive => "passive",
        _ => "?",
    };

    /// <summary>Panel color for style: red = most aggressive, green = least.</summary>
    public PanelTone StyleTone => Style switch
    {
        PlayerStyle.LooseAggressive => PanelTone.StyleAggroMax,
        PlayerStyle.Aggressive => PanelTone.StyleAggroMax,
        PlayerStyle.TightAggressive => PanelTone.StyleAggroHigh,
        PlayerStyle.Passive => PanelTone.StyleAggroLow,
        PlayerStyle.CallingStation => PanelTone.StyleAggroMin,
        PlayerStyle.TightPassive => PanelTone.StyleAggroMin,
        _ => PanelTone.Normal,
    };
}
