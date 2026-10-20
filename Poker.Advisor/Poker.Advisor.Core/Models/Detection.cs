namespace Poker.Advisor.Core.Models;

public sealed record Detection(
    string Name,
    double Confidence,
    double X1,
    double Y1,
    double X2,
    double Y2);
