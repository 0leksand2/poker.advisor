using Poker.Advisor.Core.Enums;

namespace Poker.Advisor.Core.Classes;

public sealed record PanelSpan(string Text, PanelTone Tone = PanelTone.Normal);

public sealed class PanelLine
{
    public PanelLine(string text, PanelTone tone = PanelTone.Normal)
    {
        Spans = [new PanelSpan(text, tone)];
    }

    public PanelLine(params PanelSpan[] spans)
    {
        Spans = spans.Length == 0 ? [new PanelSpan(string.Empty)] : spans;
    }

    public IReadOnlyList<PanelSpan> Spans { get; }

    public string Text => string.Concat(Spans.Select(s => s.Text));
}
