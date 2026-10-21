using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Models;

namespace Poker.Advisor.Core.Recommendations;

/// <summary>
/// Dispatches hero advice by street (preflop charts + postflop hand/action heuristics).
/// </summary>
public sealed class HeroRecommendationService
{
    private readonly IReadOnlyDictionary<PokerStreet, IStreetRecommendationProvider> _providers;

    public HeroRecommendationService(IEnumerable<IStreetRecommendationProvider>? providers = null)
    {
        var list = providers?.ToList() ?? DefaultProviders();
        _providers = list.ToDictionary(p => p.Street);
    }

    public static HeroRecommendationService CreateDefault() => new();

    public HeroRecommendation Advise(TableState state)
    {
        if (!state.HasHeroCards)
        {
            return new HeroRecommendation
            {
                Street = state.Street,
                Action = RecommendedAction.Wait,
                Summary = "waiting for cards",
            };
        }

        if (state.Street == PokerStreet.Unknown)
        {
            return new HeroRecommendation
            {
                Street = PokerStreet.Unknown,
                Action = RecommendedAction.Wait,
                Summary = "street unknown",
            };
        }

        if (!_providers.TryGetValue(state.Street, out var provider))
        {
            return new HeroRecommendation
            {
                Street = state.Street,
                Action = RecommendedAction.Wait,
                Summary = $"no advisor for {state.Street.ToString().ToLowerInvariant()}",
            };
        }

        return provider.Advise(state);
    }

    public void Apply(IReadOnlyList<TableState> tables)
    {
        foreach (var table in tables)
        {
            var baseAdvice = Advise(table);
            table.Recommendation = PlayerStyleAdvisor.Adjust(baseAdvice, table);
        }
    }

    private static List<IStreetRecommendationProvider> DefaultProviders() =>
    [
        new PreflopRecommendationProvider(),
        new Postflop.PostflopRecommendationProvider(PokerStreet.Flop),
        new Postflop.PostflopRecommendationProvider(PokerStreet.Turn),
        new Postflop.PostflopRecommendationProvider(PokerStreet.River),
    ];
}
