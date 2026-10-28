using System.Text.RegularExpressions;
using Poker.Advisor.Core.Enums;
using Poker.Advisor.Core.Classes;

namespace Poker.Advisor.Services;

/// <summary>
/// Tracks villains by nickname across frames: actions, VPIP/PFR, aggression habits.
/// </summary>
public sealed class PlayerTracker
{
    private readonly Dictionary<string, PlayerStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, TableHandState> _tables = new();
    /// <summary>Last readable nick per table+position (OCR flicker / unreadable frames).</summary>
    private readonly Dictionary<string, string> _lastNickByPosition = new(StringComparer.OrdinalIgnoreCase);

    public void Update(IReadOnlyList<TableState> tables)
    {
        foreach (var table in tables)
        {
            if (!table.HasHeroCards || table.PlayersClockwiseFromHero.Count == 0)
            {
                continue;
            }

            UpdateTable(table);
        }
    }

    private void UpdateTable(TableState table)
    {
        if (!_tables.TryGetValue(table.TableNumber, out var hand))
        {
            hand = new TableHandState();
            _tables[table.TableNumber] = hand;
        }

        // Resolve display identity before hand keys so anonymous seats use position.
        foreach (var seat in table.PlayersClockwiseFromHero.Where(p => !p.IsHero))
        {
            ApplyIdentity(seat, table.TableNumber);
        }

        var heroCards = string.Join(" ", table.HeroCards);
        var handKey = $"{table.TableNumber}|{heroCards}|{table.PlayerCount}";
        if (!string.Equals(hand.HandKey, handKey, StringComparison.Ordinal))
        {
            FinalizeHand(hand);
            hand.HandKey = handKey;
            hand.Street = table.Street;
            hand.Players.Clear();
            hand.BigBlind = InferBigBlind(table);
            foreach (var seat in table.PlayersClockwiseFromHero.Where(p => !p.IsHero))
            {
                var key = ResolveKey(seat, table.TableNumber);
                hand.Players[key] = new InHandPlayer
                {
                    Key = key,
                    Dealt = true,
                    Status = seat.Status,
                    Bet = seat.BetAmount ?? 0,
                };
            }
        }

        if (table.Street != hand.Street)
        {
            foreach (var p in hand.Players.Values)
            {
                p.StreetBetBaseline = p.Bet;
                p.CountedPostflopAggroThisStreet = false;
                p.CountedPostflopCallThisStreet = false;
            }

            hand.Street = table.Street;
        }

        hand.BigBlind ??= InferBigBlind(table);

        foreach (var seat in table.PlayersClockwiseFromHero)
        {
            if (seat.IsHero)
            {
                continue;
            }

            var seatKey = PositionKey(seat, table.TableNumber);
            var key = ResolveKey(seat, table.TableNumber);
            var stats = GetOrCreate(key, seat.Nickname);
            MergeIfNeeded(seatKey, key, stats);
            seat.Stats = stats;

            if (!hand.Players.TryGetValue(key, out var live))
            {
                // Prefer migrating in-hand state from position key when nick appears mid-hand.
                if (!string.Equals(seatKey, key, StringComparison.OrdinalIgnoreCase)
                    && hand.Players.Remove(seatKey, out var prior))
                {
                    prior.Key = key;
                    live = prior;
                }
                else
                {
                    live = new InHandPlayer
                    {
                        Key = key,
                        Dealt = true,
                        Status = seat.Status,
                        Bet = seat.BetAmount ?? 0,
                    };
                }

                hand.Players[key] = live;
            }

            ObserveAction(table, seat, live, stats, hand.BigBlind ?? 0);
            live.Status = seat.Status;
            live.Bet = seat.BetAmount ?? 0;
        }
    }

    /// <summary>
    /// If OCR nick is missing, keep last known nick for that seat or fall back to position (BTN, SB, …).
    /// </summary>
    private void ApplyIdentity(PlayerSeatState seat, int tableNumber)
    {
        var posKey = PositionKey(seat, tableNumber);
        if (IsReadableNickname(seat.Nickname))
        {
            _lastNickByPosition[posKey] = seat.Nickname!;
            return;
        }

        if (_lastNickByPosition.TryGetValue(posKey, out var remembered) && IsReadableNickname(remembered))
        {
            seat.Nickname = remembered;
            return;
        }

        // Unreadable — identity is table position.
        seat.Nickname = ShortPosition(seat.Position);
    }

    private void MergeIfNeeded(string seatKey, string key, PlayerStats target)
    {
        if (string.Equals(seatKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_stats.TryGetValue(seatKey, out var orphan) || ReferenceEquals(orphan, target))
        {
            return;
        }

        target.Hands += orphan.Hands;
        target.VpipHands += orphan.VpipHands;
        target.PfrHands += orphan.PfrHands;
        target.PostflopBetsRaises += orphan.PostflopBetsRaises;
        target.PostflopCalls += orphan.PostflopCalls;
        target.LastAction ??= orphan.LastAction;
        _stats.Remove(seatKey);
    }

    private void ObserveAction(
        TableState table,
        PlayerSeatState seat,
        InHandPlayer live,
        PlayerStats stats,
        long bigBlind)
    {
        var bet = seat.BetAmount ?? 0;
        var street = table.Street;

        if (live.Status != PlayerStatus.Folded && seat.Status == PlayerStatus.Folded)
        {
            live.Folded = true;
            stats.LastAction = "fold";
            return;
        }

        if (seat.Status == PlayerStatus.Folded)
        {
            return;
        }

        var betDelta = bet - live.Bet;
        var raiseThreshold = Math.Max(bigBlind > 0 ? (long)(bigBlind * 0.6) : 25, 25);

        if (betDelta >= raiseThreshold)
        {
            if (street == PokerStreet.Preflop)
            {
                if (!live.Voluntary)
                {
                    live.Voluntary = true;
                }

                var isBlindPost = IsBlindSized(bet, bigBlind) && betDelta <= bigBlind * 1.2;
                if (!isBlindPost && bet > bigBlind * 1.4 && !live.RaisedPreflop)
                {
                    live.RaisedPreflop = true;
                    stats.LastAction = "raise";
                }
                else if (!isBlindPost)
                {
                    stats.LastAction = "call";
                }
            }
            else
            {
                // Postflop bet/raise.
                if (!live.CountedPostflopAggroThisStreet || bet > live.StreetBetBaseline + raiseThreshold)
                {
                    stats.PostflopBetsRaises++;
                    live.CountedPostflopAggroThisStreet = true;
                    stats.LastAction = bet > live.StreetBetBaseline * 2 ? "raise" : "bet";
                }
            }
        }
        else if (street != PokerStreet.Preflop
                 && bet > 0
                 && betDelta >= 0
                 && bet <= live.StreetBetBaseline + raiseThreshold
                 && bet > live.Bet
                 && !live.CountedPostflopCallThisStreet)
        {
            // Matching a bet ≈ call.
            stats.PostflopCalls++;
            live.CountedPostflopCallThisStreet = true;
            stats.LastAction = "call";
        }
        else if (street != PokerStreet.Preflop && bet == 0 && live.Bet == 0)
        {
            stats.LastAction ??= "check";
        }
    }

    private void FinalizeHand(TableHandState hand)
    {
        if (string.IsNullOrEmpty(hand.HandKey))
        {
            return;
        }

        foreach (var live in hand.Players.Values.Where(p => p.Dealt))
        {
            if (!_stats.TryGetValue(live.Key, out var stats))
            {
                continue;
            }

            stats.Hands++;
            if (live.Voluntary || live.RaisedPreflop)
            {
                stats.VpipHands++;
            }

            if (live.RaisedPreflop)
            {
                stats.PfrHands++;
            }
        }
    }

    private PlayerStats GetOrCreate(string key, string? nickname)
    {
        if (_stats.TryGetValue(key, out var existing))
        {
            if (IsReadableNickname(nickname) && IsPositionLabel(existing.DisplayName))
            {
                existing.DisplayName = nickname!;
            }

            return existing;
        }

        var created = new PlayerStats
        {
            Key = key,
            DisplayName = IsReadableNickname(nickname) ? nickname! : key,
        };
        _stats[key] = created;
        return created;
    }

    private static string ResolveKey(PlayerSeatState seat, int tableNumber)
    {
        // Position abbreviations (BTN/SB/…) are placeholders — track by seat until a real nick exists.
        if (IsReadableNickname(seat.Nickname) && !IsPositionLabel(seat.Nickname))
        {
            return NormalizeKey(seat.Nickname!);
        }

        return PositionKey(seat, tableNumber);
    }

    private static string PositionKey(PlayerSeatState seat, int tableNumber)
    {
        var pos = Regex.Replace(seat.Position.Trim().ToLowerInvariant(), @"\s+", "_");
        if (string.IsNullOrWhiteSpace(pos))
        {
            pos = "unknown";
        }

        return $"t{tableNumber}_pos_{pos}";
    }

    private static string ShortPosition(string? position) => position?.Trim().ToLowerInvariant() switch
    {
        "button" => "BTN",
        "small blind" => "SB",
        "big blind" => "BB",
        "cut-off" or "cutoff" => "CO",
        "hijack" => "HJ",
        "utg" => "UTG",
        "utg+1" => "UTG+1",
        "lj" or "lojack" => "LJ",
        "mp" => "MP",
        _ => string.IsNullOrWhiteSpace(position) ? "SEAT" : position.Trim().ToUpperInvariant(),
    };

    private static bool IsReadableNickname(string? nickname) =>
        !string.IsNullOrWhiteSpace(nickname) && nickname.Trim().Length >= 2;

    private static bool IsPositionLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (name.Contains("_pos_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Trim().ToUpperInvariant() is "BTN" or "SB" or "BB" or "CO" or "HJ" or "UTG"
            or "UTG+1" or "LJ" or "MP" or "SEAT";
    }

    private static string NormalizeKey(string nickname)
    {
        var cleaned = Regex.Replace(nickname.Trim().ToLowerInvariant(), @"\s+", "");
        return cleaned.Length == 0 ? nickname : cleaned;
    }

    private static long? InferBigBlind(TableState table)
    {
        var bb = table.PlayersClockwiseFromHero
            .FirstOrDefault(p => p.Position.Equals("big blind", StringComparison.OrdinalIgnoreCase))
            ?.BetAmount;
        if (bb is > 0)
        {
            return bb;
        }

        var positive = table.PlayersClockwiseFromHero
            .Where(p => p.BetAmount is > 0)
            .Select(p => p.BetAmount!.Value)
            .OrderBy(v => v)
            .ToList();
        return positive.Count == 0 ? null : positive[0];
    }

    private static bool IsBlindSized(long bet, long bigBlind)
    {
        if (bigBlind <= 0)
        {
            return false;
        }

        return bet <= bigBlind * 1.15;
    }

    private sealed class TableHandState
    {
        public string? HandKey { get; set; }
        public PokerStreet Street { get; set; } = PokerStreet.Unknown;
        public long? BigBlind { get; set; }
        public Dictionary<string, InHandPlayer> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class InHandPlayer
    {
        public required string Key { get; set; }
        public bool Dealt { get; set; }
        public bool Voluntary { get; set; }
        public bool RaisedPreflop { get; set; }
        public bool Folded { get; set; }
        public bool CountedPostflopAggroThisStreet { get; set; }
        public bool CountedPostflopCallThisStreet { get; set; }
        public PlayerStatus Status { get; set; }
        public long Bet { get; set; }
        public long StreetBetBaseline { get; set; }
    }
}
