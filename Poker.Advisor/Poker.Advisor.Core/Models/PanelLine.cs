using Poker.Advisor.Core.Enums;

namespace Poker.Advisor.Core.Models;

public sealed record PanelLine(string Text, PanelTone Tone = PanelTone.Normal);
