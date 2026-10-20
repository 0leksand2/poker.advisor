using Poker.Advisor.Core.Models;

namespace Poker.Advisor.Core.Extensions;

public static class DetectionExtensions
{
    public static double CenterX(this Detection d) => (d.X1 + d.X2) * 0.5;

    public static double CenterY(this Detection d) => (d.Y1 + d.Y2) * 0.5;

    public static double Width(this Detection d) => Math.Max(0, d.X2 - d.X1);

    public static double Height(this Detection d) => Math.Max(0, d.Y2 - d.Y1);

    public static bool ContainsPoint(this Detection d, double x, double y) =>
        x >= d.X1 && x <= d.X2 && y >= d.Y1 && y <= d.Y2;

    public static bool CenterInside(this Detection outer, Detection inner) =>
        outer.ContainsPoint(inner.CenterX(), inner.CenterY());

    public static double DistanceTo(this Detection a, Detection b)
    {
        var dx = a.CenterX() - b.CenterX();
        var dy = a.CenterY() - b.CenterY();
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static bool IsTable(this Detection d) =>
        d.Name.Equals("table", StringComparison.OrdinalIgnoreCase)
        || d.Name.StartsWith("table_", StringComparison.OrdinalIgnoreCase);

    public static bool IsActivePlayer(this Detection d) =>
        d.Name.Equals("active_player", StringComparison.OrdinalIgnoreCase);

    public static bool IsFoldPlayer(this Detection d) =>
        d.Name.Equals("fold_player", StringComparison.OrdinalIgnoreCase);

    public static bool IsPlayer(this Detection d) => d.IsActivePlayer() || d.IsFoldPlayer();

    public static bool IsDealer(this Detection d) =>
        d.Name.Equals("dealer", StringComparison.OrdinalIgnoreCase);

    public static bool IsCard(this Detection d)
    {
        if (d.Name.Length != 2)
        {
            return false;
        }

        var rank = char.ToUpperInvariant(d.Name[0]);
        var suit = char.ToUpperInvariant(d.Name[1]);
        return "23456789TJQKA".Contains(rank) && "CDHS".Contains(suit);
    }

    public static int TableNumber(this Detection d)
    {
        if (!d.IsTable())
        {
            return 0;
        }

        if (d.Name.StartsWith("table_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(d.Name.AsSpan(6), out var n))
        {
            return n;
        }

        return 1;
    }
}
