namespace Poker.Advisor.Core.Enums;

public enum PanelTone
{
    Normal,
    Fold,
    AdviceRaise,
    AdviceCall,
    AdviceFold,
    AdviceCheck,
    /// <summary>Most aggressive (LAG / aggro).</summary>
    StyleAggroMax,
    /// <summary>Aggressive but tighter (TAG).</summary>
    StyleAggroHigh,
    /// <summary>Mild / passive.</summary>
    StyleAggroLow,
    /// <summary>Least aggressive (nit / station).</summary>
    StyleAggroMin,
}
