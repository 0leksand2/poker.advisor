using Poker.Advisor.Core.Enums;

namespace Poker.Advisor.Core.Models;

public sealed class PlayerSeatState
{
    public required Detection Detection { get; init; }

    /// <summary>
    /// Region used to OCR the stack/bank (bottom of player title, or under hero cards).
    /// </summary>
    public required Detection BankRegion { get; init; }

    public required PlayerStatus Status { get; init; }
    public required string Position { get; init; }
    public double Angle { get; init; }
    public bool IsHero { get; init; }
    public bool IsDealer { get; init; }

    public long? BankAmount { get; set; }
    public string? BankRawText { get; set; }
    public long? BetAmount { get; set; }
    public string? BetRawText { get; set; }
}
