using Poker.Advisor.Core.Enums;

namespace Poker.Advisor.Core.Classes;

public sealed class PlayerSeatState
{
    public required Detection Detection { get; init; }

    /// <summary>
    /// Region used to OCR the stack/bank (YOLO bank box, player title, or under hero cards).
    /// </summary>
    public required Detection BankRegion { get; set; }

    public required PlayerStatus Status { get; init; }
    public required string Position { get; init; }
    public double Angle { get; init; }
    public bool IsHero { get; init; }
    public bool IsDealer { get; init; }

    public long? BankAmount { get; set; }
    public string? BankRawText { get; set; }
    public long? BetAmount { get; set; }
    public string? BetRawText { get; set; }

    public string? Nickname { get; set; }
    public string? NicknameRawText { get; set; }
    public PlayerStats? Stats { get; set; }
}
