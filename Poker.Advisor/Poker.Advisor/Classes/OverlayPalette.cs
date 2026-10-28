using System.Windows.Media;
using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Extensions;
using Poker.Advisor.Core.Classes;
using Color = System.Windows.Media.Color;

namespace Poker.Advisor.Classes;

/// <summary>Stable overlay colors shared by boxes and the advice panel.</summary>
internal static class OverlayPalette
{
    public static readonly Color Table = Color.FromRgb(0x4C, 0xB3, 0xFF);
    public static readonly Color ActivePlayer = Color.FromRgb(0xFF, 0xC2, 0x4D);
    public static readonly Color FoldPlayer = Color.FromRgb(0xFF, 0x6B, 0x6B);
    public static readonly Color Card = Color.FromRgb(0x7C, 0xFF, 0xB2);
    public static readonly Color Dealer = Color.FromRgb(0xFF, 0x9F, 0x43);
    public static readonly Color Nickname = Color.FromRgb(0xC7, 0x92, 0xFF);
    public static readonly Color Bank = Color.FromRgb(0x5A, 0xD9, 0x8A);
    public static readonly Color PotBet = Color.FromRgb(0x5E, 0xD6, 0xD6);
    public static readonly Color ActionBtn = Color.FromRgb(0xFF, 0x7A, 0xB8);
    public static readonly Color PotPct = Color.FromRgb(0xB8, 0x9C, 0xFF);
    public static readonly Color Fallback = Color.FromRgb(0x9A, 0xE6, 0x8C);

    public static readonly Color AdviceRaise = Color.FromRgb(0x39, 0xFF, 0x14);
    public static readonly Color AdviceCall = Color.FromRgb(0x34, 0xD3, 0x99);
    public static readonly Color AdviceFold = Color.FromRgb(0xFF, 0x5A, 0x5F);
    public static readonly Color AdviceCheck = Color.FromRgb(0xFF, 0xD6, 0x0A);
    public static readonly Color TextPrimary = Color.FromRgb(0xF2, 0xF4, 0xF8);
    public static readonly Color TextMuted = Color.FromRgb(0xA8, 0xB0, 0xBC);
    public static readonly Color TextFold = Color.FromRgb(0x8A, 0x90, 0x98);

    public static Color ForDetection(Detection d)
    {
        if (d.IsTable())
        {
            return Table;
        }

        if (d.IsActivePlayer())
        {
            return ActivePlayer;
        }

        if (d.IsFoldPlayer())
        {
            return FoldPlayer;
        }

        if (d.IsCard())
        {
            return Card;
        }

        if (d.IsDealer())
        {
            return Dealer;
        }

        if (d.IsNickname())
        {
            return Nickname;
        }

        if (d.IsBank())
        {
            return Bank;
        }

        if (d.IsPot() || d.IsBet())
        {
            return PotBet;
        }

        if (d.IsActionButton())
        {
            return ActionBtn;
        }

        if (d.IsPotPercentButton())
        {
            return PotPct;
        }

        return Fallback;
    }

    public static Color ForAdvice(RecommendedAction action) => action switch
    {
        RecommendedAction.Raise => AdviceRaise,
        RecommendedAction.Call => AdviceCall,
        RecommendedAction.Fold => AdviceFold,
        RecommendedAction.Check => AdviceCheck,
        _ => TextMuted,
    };

    public static Color ForTone(PanelTone tone) => tone switch
    {
        PanelTone.Fold => TextFold,
        PanelTone.AdviceRaise => AdviceRaise,
        PanelTone.AdviceCall => AdviceCall,
        PanelTone.AdviceFold => AdviceFold,
        PanelTone.AdviceCheck => AdviceCheck,
        PanelTone.StyleAggroMax => Color.FromRgb(0xFF, 0x4D, 0x4D),
        PanelTone.StyleAggroHigh => Color.FromRgb(0xFF, 0x9F, 0x43),
        PanelTone.StyleAggroLow => Color.FromRgb(0x9A, 0xE6, 0x4F),
        PanelTone.StyleAggroMin => AdviceCall,
        _ => TextPrimary,
    };

    public static SolidColorBrush Brush(Color color, byte? alpha = null)
    {
        var c = alpha is byte a ? Color.FromArgb(a, color.R, color.G, color.B) : color;
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public static string ShortLabel(Detection d)
    {
        if (d.IsTable())
        {
            var n = d.TableNumber();
            return n > 0 ? $"T{n}" : "T";
        }

        if (d.IsActivePlayer())
        {
            return "villain";
        }

        if (d.IsFoldPlayer())
        {
            return "fold";
        }

        if (d.IsCard())
        {
            return d.Name.ToUpperInvariant();
        }

        return d.Name;
    }
}
