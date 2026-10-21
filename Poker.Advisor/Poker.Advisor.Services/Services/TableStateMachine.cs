using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Extensions;
using Poker.Advisor.Core.Models;

namespace Poker.Advisor.Services;

/// <summary>
/// Builds per-table poker state from YOLO detections.
/// If the hero has no hole cards, further table math is skipped for that table.
/// </summary>
public sealed class TableStateMachine
{
    public IReadOnlyList<TableState> Update(IReadOnlyList<Detection> detections)
    {
        var tables = detections
            .Where(d => d.IsTable())
            .OrderBy(d => d.TableNumber())
            .ThenByDescending(d => d.Width() * d.Height())
            .ToList();

        if (tables.Count == 0)
        {
            return Array.Empty<TableState>();
        }

        // Deduplicate overlapping table boxes: keep largest per number / cluster.
        tables = DeduplicateTables(tables);

        var states = new List<TableState>(tables.Count);
        foreach (var table in tables)
        {
            states.Add(BuildTableState(table, detections));
        }

        return states;
    }

    private TableState BuildTableState(Detection table, IReadOnlyList<Detection> detections)
    {
        var tableNumber = table.TableNumber();
        var inside = detections.Where(d => !d.IsTable() && table.CenterInside(d)).ToList();

        var heroCards = FindHeroCards(table, inside);
        if (heroCards.Count == 0)
        {
            return new TableState
            {
                TableNumber = tableNumber,
                Table = table,
                HasHeroCards = false,
                SummaryLine = "waiting for cards",
            };
        }

        var communityCards = FindCommunityCards(table, inside, heroCards);
        var street = PokerStreetExtensions.FromCommunityCount(communityCards.Count);

        var detectedPlayers = inside.Where(d => d.IsPlayer()).ToList();
        var dealer = inside.FirstOrDefault(d => d.IsDealer())
            ?? detections
                .Where(d => d.IsDealer())
                .OrderBy(d => table.DistanceTo(d))
                .FirstOrDefault();

        // Hero sits at the hole-card cluster. GG often has no active_player box there,
        // so only reuse a detection if it is near the cards; otherwise add a synthetic seat.
        var heroCx = heroCards.Average(c => c.CenterX());
        var heroCy = heroCards.Average(c => c.CenterY());
        var heroMatchRadius = Math.Min(table.Width(), table.Height()) * 0.22;
        var matchedHero = detectedPlayers
            .Select(p => (Player: p, Dist: Distance(p.CenterX(), p.CenterY(), heroCx, heroCy)))
            .Where(x => x.Dist <= heroMatchRadius)
            .OrderBy(x => x.Dist)
            .Select(x => x.Player)
            .FirstOrDefault();

        var heroDetection = matchedHero ?? CreateSyntheticHero(heroCards);
        // Always OCR hero stack from the nameplate under hole cards — never from a
        // matched active_player box (GG often has no hero seat box, and when it does
        // the box is cards/avatar, not the stack row).
        var heroBankRegion = CreateHeroBankRegion(table, heroCards);
        var otherPlayers = matchedHero is null
            ? detectedPlayers
            : detectedPlayers.Where(p => !ReferenceEquals(p, matchedHero)).ToList();
        var players = otherPlayers
            .Select(p => (Detection: p, BankRegion: p))
            .Append((Detection: heroDetection, BankRegion: heroBankRegion))
            .ToList();

        var tableCx = table.CenterX();
        var tableCy = table.CenterY();
        var ordered = players
            .Select(p => new
            {
                p.Detection,
                p.BankRegion,
                Angle = Math.Atan2(p.Detection.CenterY() - tableCy, p.Detection.CenterX() - tableCx),
            })
            .OrderBy(p => p.Angle)
            .ToList();

        // Button = player closest to dealer chip; fallback to first seat.
        var buttonIndex = 0;
        if (dealer is not null)
        {
            buttonIndex = ordered
                .Select((p, i) => (i, dist: Distance(p.Detection.CenterX(), p.Detection.CenterY(), dealer.CenterX(), dealer.CenterY())))
                .OrderBy(x => x.dist)
                .First()
                .i;
        }

        var positionNames = PokerPositions.ForPlayerCount(ordered.Count);
        var seats = new PlayerSeatState[ordered.Count];
        for (var offset = 0; offset < ordered.Count; offset++)
        {
            var idx = (buttonIndex + offset) % ordered.Count;
            var item = ordered[idx];
            seats[offset] = new PlayerSeatState
            {
                Detection = item.Detection,
                BankRegion = item.BankRegion,
                Status = item.Detection.IsFoldPlayer() ? PlayerStatus.Folded : PlayerStatus.Active,
                Position = positionNames[offset],
                Angle = item.Angle,
                IsDealer = offset == 0,
            };
        }

        var heroSeatIndex = seats
            .Select((s, i) => (i, dist: Distance(s.Detection.CenterX(), s.Detection.CenterY(), heroCx, heroCy)))
            .OrderBy(x => x.dist)
            .First()
            .i;

        var rotated = new PlayerSeatState[seats.Length];
        for (var i = 0; i < seats.Length; i++)
        {
            var seat = seats[(heroSeatIndex + i) % seats.Length];
            rotated[i] = new PlayerSeatState
            {
                Detection = seat.Detection,
                BankRegion = seat.BankRegion,
                Status = seat.Status,
                Position = seat.Position,
                Angle = seat.Angle,
                IsDealer = seat.IsDealer,
                IsHero = i == 0,
            };
        }

        return new TableState
        {
            TableNumber = tableNumber,
            Table = table,
            HasHeroCards = true,
            HeroCards = heroCards.Select(c => c.Name).ToArray(),
            CommunityCards = communityCards.Select(c => c.Name).ToArray(),
            CommunityCardBoxes = communityCards,
            Street = street,
            PlayerCount = rotated.Length,
            PlayersClockwiseFromHero = rotated,
            HeroPosition = rotated[0].Position,
            SummaryLine =
                $"{street.ToDisplay()} · {rotated.Length} players · dealer={rotated.First(p => p.IsDealer).Position}",
        };
    }

    private static Detection CreateSyntheticHero(IReadOnlyList<Detection> heroCards)
    {
        return new Detection(
            "active_player",
            1.0,
            heroCards.Min(c => c.X1),
            heroCards.Min(c => c.Y1),
            heroCards.Max(c => c.X2),
            heroCards.Max(c => c.Y2));
    }

    private static Detection CreateHeroBankRegion(Detection table, IReadOnlyList<Detection> heroCards)
    {
        var cardLeft = heroCards.Min(c => c.X1);
        var cardRight = heroCards.Max(c => c.X2);
        var cardBottom = heroCards.Max(c => c.Y2);
        var cardWidth = Math.Max(1, cardRight - cardLeft);

        // GG nameplate sits centered under the hole cards (nickname + stack).
        // Reach a bit into the card bottoms and extend downward for the stack row.
        var padX = Math.Max(56, cardWidth * 1.05);
        var x1 = Math.Max(table.X1, cardLeft - padX);
        var x2 = Math.Min(table.X2, cardRight + padX);
        var y1 = Math.Min(table.Y2 - 24, cardBottom - Math.Max(12, cardWidth * 0.08));
        var y2 = Math.Min(table.Y2, y1 + 140);
        if (y2 <= y1 + 20)
        {
            y1 = Math.Max(table.Y1, table.Y2 - 140);
            y2 = table.Y2;
        }

        return new Detection("hero_bank", 1.0, x1, y1, x2, y2);
    }

    private static List<Detection> FindHeroCards(Detection table, List<Detection> inside)
    {
        var heroTop = table.Y1 + table.Height() * 0.68;
        return inside
            .Where(d => d.IsCard() && d.CenterY() >= heroTop)
            .OrderByDescending(d => d.CenterY())
            .ThenBy(d => d.CenterX())
            .Take(2)
            .OrderBy(d => d.CenterX())
            .ToList();
    }

    private static List<Detection> FindCommunityCards(
        Detection table,
        List<Detection> inside,
        List<Detection> heroCards)
    {
        var heroIds = heroCards.ToHashSet();
        var midLeft = table.X1 + table.Width() * 0.20;
        var midRight = table.X1 + table.Width() * 0.80;
        var midTop = table.Y1 + table.Height() * 0.30;
        var midBottom = table.Y1 + table.Height() * 0.62;

        var cards = inside
            .Where(d =>
                d.IsCard()
                && !heroIds.Contains(d)
                && d.CenterX() >= midLeft
                && d.CenterX() <= midRight
                && d.CenterY() >= midTop
                && d.CenterY() <= midBottom)
            .OrderBy(d => d.CenterX())
            .ToList();

        // Keep at most 5 board cards (left-to-right).
        if (cards.Count > 5)
        {
            cards = cards.Take(5).ToList();
        }

        return cards;
    }

    private static List<Detection> DeduplicateTables(List<Detection> tables)
    {
        // Keep Python's table_N labels intact. Only collapse duplicate boxes that
        // share the same number (never drop table_2 because it overlaps table_1).
        var kept = new List<Detection>();
        foreach (var table in tables.OrderByDescending(t => t.Width() * t.Height()))
        {
            var duplicateNumber = kept.Any(k => k.TableNumber() == table.TableNumber());
            if (!duplicateNumber)
            {
                kept.Add(table);
            }
        }

        return kept.OrderBy(t => t.TableNumber()).ToList();
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
