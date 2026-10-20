namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// 6-max ~100bb open (RFI) / BB defend ranges adapted from Poker Skill opening charts
/// (https://www.pokerskill.com/charts/opening-ranges/).
/// </summary>
public static class PreflopOpeningCharts
{
    public sealed record Chart(string Key, string Label, double Percent, string Range, bool IsDefend);

    // Raise-first-in charts (BB uses defend-vs-BTN as a practical default).
    private static readonly IReadOnlyDictionary<string, Chart> Charts =
        new Dictionary<string, Chart>(StringComparer.OrdinalIgnoreCase)
        {
            ["UTG"] = Build("UTG", "under the gun", 14.3, "22+, ATs+, A5s-A2s, KTs+, QTs+, JTs, T9s, 98s, AJo+, KQo", defend: false),
            ["MP"] = Build("MP", "middle position", 14.8, "55+, A9s+, A5s-A2s, KTs+, QTs+, JTs, T9s, ATo+, KJo+", defend: false),
            ["HJ"] = Build("HJ", "hijack", 18.9, "22+, A2s+, K9s+, Q9s+, J9s+, T8s+, 98s, 87s, ATo+, KJo+", defend: false),
            ["CO"] = Build("CO", "cutoff", 25.5, "22+, A2s+, K5s+, Q8s+, J8s+, T8s+, 97s+, 87s, 76s, 65s, 54s, A8o+, KTo+, QJo", defend: false),
            ["BTN"] = Build("BTN", "button", 43.3, "22+, A2s+, K2s+, Q4s+, J6s+, T6s+, 95s+, 85s+, 75s+, 64s+, 54s, A2o+, K8o+, Q9o+, J9o+, T9o, 98o", defend: false),
            ["SB"] = Build("SB", "small blind", 33.0, "22+, A2s+, K4s+, Q6s+, J7s+, T7s+, 96s+, 86s+, 76s, 65s, 54s, A5o+, K9o+, QTo+, JTo", defend: false),
            ["BB"] = Build("BB", "big blind (vs BTN open)", 52.3, "22+, A2s+, K2s+, Q2s+, J4s+, T6s+, 95s+, 85s+, 74s+, 64s+, 53s+, A2o+, K5o+, Q8o+, J8o+, T8o+, 97o+, 87o", defend: true),
        };

    private static readonly IReadOnlyDictionary<string, HashSet<string>> Expanded =
        Charts.ToDictionary(
            kv => kv.Key,
            kv => PreflopRangeParser.Parse(kv.Value.Range),
            StringComparer.OrdinalIgnoreCase);

    public static bool TryGetChart(string chartKey, out Chart chart) =>
        Charts.TryGetValue(chartKey, out chart!);

    public static bool Contains(string chartKey, string handNotation) =>
        Expanded.TryGetValue(chartKey, out var set) && set.Contains(handNotation);

    /// <summary>
    /// Maps UI / TableState position labels onto a chart key.
    /// </summary>
    public static string? ResolveChartKey(string? position)
    {
        if (string.IsNullOrWhiteSpace(position))
        {
            return null;
        }

        var p = position.Trim().ToLowerInvariant();
        return p switch
        {
            "button" or "btn" or "bu" => "BTN",
            "small blind" or "sb" => "SB",
            "big blind" or "bb" => "BB",
            "cut-off" or "cutoff" or "co" => "CO",
            "hijack" or "hj" => "HJ",
            "lj" or "lojack" or "lo-jack" => "HJ",
            "utg" => "UTG",
            "utg+1" or "mp" or "middle" or "middle position" => "MP",
            _ when p.StartsWith("mp+", StringComparison.Ordinal) => "MP",
            _ => null,
        };
    }

    private static Chart Build(string key, string label, double percent, string range, bool defend) =>
        new(key, label, percent, range, defend);
}
