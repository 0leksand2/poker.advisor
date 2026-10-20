namespace Poker.Advisor.Core.Extensions;

public static class PokerPositions
{
    /// <summary>
    /// Position labels clockwise starting at the button.
    /// </summary>
    public static IReadOnlyList<string> ForPlayerCount(int playerCount)
    {
        if (playerCount <= 0)
        {
            return Array.Empty<string>();
        }

        if (playerCount == 1)
        {
            return new[] { "button" };
        }

        // Heads-up: button is also the small blind.
        if (playerCount == 2)
        {
            return new[] { "button", "big blind" };
        }

        var names = new string[playerCount];
        names[0] = "button";
        names[1] = "small blind";
        names[2] = "big blind";

        if (playerCount == 3)
        {
            return names;
        }

        names[^1] = "cut-off";
        if (playerCount >= 5)
        {
            names[^2] = "hijack";
        }

        var firstMiddle = 3;
        var lastMiddleExclusive = playerCount >= 5 ? playerCount - 2 : playerCount - 1;
        var middleCount = lastMiddleExclusive - firstMiddle;

        for (var i = 0; i < middleCount; i++)
        {
            names[firstMiddle + i] = middleCount switch
            {
                1 => "utg",
                2 => i == 0 ? "utg" : "utg+1",
                3 => i switch
                {
                    0 => "utg",
                    1 => "utg+1",
                    _ => "lj",
                },
                _ => i switch
                {
                    0 => "utg",
                    1 => "utg+1",
                    2 => "mp",
                    3 => "lj",
                    _ => $"mp+{i - 2}",
                },
            };
        }

        return names;
    }
}
