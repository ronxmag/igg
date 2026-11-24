using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

/// <summary>
/// ARENA build: player-versus-player admission for the Duel Arena.
/// </summary>
/// <remarks>
/// IGG "Duel Arena" (igg-acid=372), PvP rules:
/// "2. Adventurers in the combat area are allowed to attack one another regardless
/// of faction. 3. ... adventurers from the same faction are allowed to team up.
/// Players who have teamed up will not be able to cause damage to teammates.
/// 4. There are no time, spell or mode limits in the combat area."
/// <para>
/// Every player-versus-player admission goes through
/// <see cref="EvaluatePlayerPvpAdmission"/>. With <c>GODSWAR_DUEL_ARENA_PVP</c> off,
/// or for any pair that is not both inside the arena combat area, it returns
/// exactly what <see cref="PvpWorldAuthorityCatalog.EvaluateOpposingFaction"/>
/// returns, so every other map keeps its default-deny faction rule. Inside the
/// combat area two players who are not in the same party receive a
/// <see cref="PvpEntitlementKind.MutualDuel"/> grant (the duel entitlement the
/// catalog reserved, never issued before this build) and are then evaluated by
/// the same <see cref="PvpCombatEligibilityPolicy"/> as faction PvP.
/// </para>
/// </remarks>
internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<int, (int Wins, int Losses)>
        _duelArenaRecords = new();
    private readonly ConcurrentDictionary<int, int> _playerKillStreaks = new();

    internal static bool IsOpposingFactionPair(
        GameCharacter attacker,
        GameCharacter target) =>
        attacker.Camp is GameDefaults.SpartaCamp or GameDefaults.AthensCamp &&
        target.Camp is GameDefaults.SpartaCamp or GameDefaults.AthensCamp &&
        attacker.Camp != target.Camp;

    internal static bool IsDuelArenaCombatPair(
        GameCharacter attacker,
        GameCharacter target) =>
        ArenaFeatureGates.DuelArenaPvp &&
        DuelArenaCombatArea.Contains(
            attacker.CurrentMap,
            attacker.PositionX,
            attacker.PositionZ) &&
        DuelArenaCombatArea.Contains(
            target.CurrentMap,
            target.PositionX,
            target.PositionZ);

    internal byte? LocalPlayerPkMode(GameCharacter? character) =>
        character is not null && IsHostilePvpModeRequired(character)
            ? (byte)1
            : null;

    private bool IsHostilePvpModeRequired(GameCharacter character)
    {
        if (IsDuelArenaCombatPair(character, character))
        {
            return true;
        }

        return character.Camp is GameDefaults.SpartaCamp or GameDefaults.AthensCamp &&
            _gameplayCatalogs.PvpWorldAuthority.IsUnsafePvpMap(character.CurrentMap) &&
            !Godswar.Server.Application.North.NorthNpcSafeZone.Contains(
                character.CurrentMap,
                character.PositionX,
                character.PositionZ);
    }

    private bool IsPeaceProtectionBlockingPlayerPvp(
        GameCharacter attacker,
        GameCharacter target,
        DateTimeOffset now)
    {
        if (attacker.PeaceUntil <= now && target.PeaceUntil <= now)
        {
            return false;
        }

        if (IsDuelArenaCombatPair(attacker, target))
        {
            return false;
        }

        // Peace items do not turn a legitimate opposing-faction PvP-map
        // matchup into a protected target. Map safety and faction are decided
        // by the normal authority; same-faction and safe-zone pairs stay denied.
        return !_gameplayCatalogs.PvpWorldAuthority
            .EvaluateOpposingFaction(attacker, target, now).Allowed;
    }
    internal PvpEligibilityResult EvaluatePlayerPvpAdmission(
        GameCharacter attacker,
        GameCharacter target,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);
        if (IsPeaceProtectionBlockingPlayerPvp(attacker, target, now))
        {
            return PvpEligibilityResult.Denied(PvpEligibilityFailure.SafeZone);
        }

        if (!ArenaFeatureGates.DuelArenaPvp ||
            !DuelArenaCombatArea.Contains(
                attacker.CurrentMap,
                attacker.PositionX,
                attacker.PositionZ) ||
            !DuelArenaCombatArea.Contains(
                target.CurrentMap,
                target.PositionX,
                target.PositionZ))
        {
            return _gameplayCatalogs.PvpWorldAuthority
                .EvaluateOpposingFaction(attacker, target, now);
        }

        bool sameParty;
        lock (_gate)
        {
            sameParty =
                _partiesByCharacter.TryGetValue(attacker.Id, out var first) &&
                _partiesByCharacter.TryGetValue(target.Id, out var second) &&
                ReferenceEquals(first, second);
        }

        return EvaluateDuelArenaPair(attacker, target, now, sameParty);
    }

    /// <summary>The arena rule itself, without registry state (checked directly).</summary>
    internal static PvpEligibilityResult EvaluateDuelArenaPair(
        GameCharacter attacker,
        GameCharacter target,
        DateTimeOffset now,
        bool sameParty)
    {
        var attackerParticipant = new PvpCombatParticipant(
            attacker.Id,
            attacker.CurrentMap,
            attacker.CurrentHp > 0,
            IsInSafeZone: false,
            attacker.Camp);
        var targetParticipant = new PvpCombatParticipant(
            target.Id,
            target.CurrentMap,
            target.CurrentHp > 0,
            IsInSafeZone: false,
            target.Camp);
        var entitlement = sameParty
            ? (PvpCombatEntitlement?)null
            : new PvpCombatEntitlement(
                CreateDuelArenaEntitlementId(
                    attacker.Id,
                    target.Id,
                    attacker.CurrentMap),
                PvpEntitlementKind.MutualDuel,
                attacker.Id,
                target.Id,
                attacker.CurrentMap,
                now.AddSeconds(-1),
                now.AddSeconds(5),
                attacker.Camp,
                target.Camp);
        return PvpCombatEligibilityPolicy.Evaluate(
            attackerParticipant,
            targetParticipant,
            entitlement,
            now);
    }

    /// <summary>
    /// GENERATED win/loss handling (the IGG page names no reward): every player on
    /// the arena map is told who defeated whom, with each duellist's tally since the
    /// server started. Nothing is paid and nothing is taken; faction kill credit and
    /// quest player kills are untouched (quest player kills already ignore
    /// same-faction victims and only count on map 68).
    /// </summary>
    private async Task AnnouncePlayerKillStreaksAsync(
        PvpBasicAttackDecision decision,
        CancellationToken cancellationToken)
    {
        if (!decision.Accepted ||
            decision.Eligibility.EntitlementKind is not (
                PvpEntitlementKind.MutualDuel or PvpEntitlementKind.OpposingFaction) ||
            decision.Attacker is not { } attacker ||
            decision.Target is not { } target) return;

        var deaths = new List<(GameSessionContext Winner, GameSessionContext Loser)>();
        if (decision.TargetKilled) deaths.Add((attacker, target));
        if (decision.AttackerKilled) deaths.Add((target, attacker));
        foreach (var commit in decision.ElementalDamageCommits.Where(static item => item.Killed))
            deaths.Add((commit.Source, commit.Target));

        var seenVictims = new HashSet<int>();
        foreach (var (winner, loser) in deaths)
        {
            if (!seenVictims.Add(loser.CharacterId)) continue;
            var streak = _playerKillStreaks.AddOrUpdate(
                winner.CharacterId, 1, static (_, current) => checked(current + 1));
            var ended = _playerKillStreaks.TryRemove(loser.CharacterId, out var prior) ? prior : 0;
            if (TryGetDotaKillStreakTier(streak, out _))
                await BroadcastToAllSessionsAsync(
                    PacketBuilder.DotaKillStreakAnnouncement(
                        SafeDotaName(winner.CharacterName), $"S#{DotaStreakMessageId(streak)}#{streak}"),
                    cancellationToken,
                    "PvpKillStreakAnnouncement");
            if (TryGetDotaKillStreakTier(ended, out _))
                await BroadcastToAllSessionsAsync(
                    PacketBuilder.DotaKillStreakAnnouncement(
                        SafeDotaName(winner.CharacterName),
                        $"E#{SafeDotaName(loser.CharacterName)}#{DotaStreakMessageId(ended)}#{ended}"),
                    cancellationToken,
                    "PvpKillStreakEndedAnnouncement");
        }
    }

    private static string SafeDotaName(string name) =>
        new(name.Take(32).Select(static c => c is >= ' ' and <= '~' and not '#' ? c : '?').ToArray());

    internal static string DotaStreakMessageId(int streak) => streak switch
    {
        0 => "1000", 2 => "1001", 3 => "1002", 4 => "1003",
        6 => "1004", 7 => "1005", 8 => "1006", 9 => "1007",
		10 => "1008", 11 => "1009", 12 => "1010", 13 => "1011",
		>= 14 => "1012", _ => string.Empty
    };

    internal static bool TryGetDotaKillStreakTier(int streak, out string tier)
    {
        tier = streak switch
        {
            2 => "Double Kill", 3 => "Triple Kill", 4 => "Ultra Kill",
            6 => "Rampage", 7 => "Killing Spree", 8 => "Dominating",
            9 => "Mega Kill", 10 => "Unstoppable", 11 => "Wicked Sick",
			12 => "Monster Kill", 13 => "Godlike", >= 14 => "Beyond Godlike", 
			_ => string.Empty
        };
        return tier.Length > 0;
    }

    private async Task AnnounceDuelArenaDefeatsAsync(
        PvpBasicAttackDecision decision,
        CancellationToken cancellationToken)
    {
        if (!decision.Accepted ||
            decision.Eligibility.EntitlementKind != PvpEntitlementKind.MutualDuel ||
            decision.Attacker is not { } attacker ||
            decision.Target is not { } target)
        {
            return;
        }

        var defeats = new List<(GameSessionContext Winner, GameSessionContext Loser)>();
        if (decision.TargetKilled)
        {
            defeats.Add((attacker, target));
        }
        if (decision.AttackerKilled)
        {
            defeats.Add((target, attacker));
        }

        foreach (var (winner, loser) in defeats)
        {
            var won = _duelArenaRecords.AddOrUpdate(
                winner.CharacterId, (1, 0), static (_, value) => (value.Wins + 1, value.Losses));
            var lost = _duelArenaRecords.AddOrUpdate(
                loser.CharacterId, (0, 1), static (_, value) => (value.Wins, value.Losses + 1));
            var line = DuelArenaDefeatLine(
                winner.CharacterName, won, loser.CharacterName, lost);
            Console.WriteLine(
                $"[duel-arena] defeat winner={winner.CharacterName} loser={loser.CharacterName} " +
                $"record={won.Wins}-{won.Losses}/{lost.Wins}-{lost.Losses}");
            foreach (var recipient in GetWorldInstanceSessions(attacker.WorldInstanceId))
            {
                if (recipient.MapId != DuelArenaCombatArea.MapId)
                {
                    continue;
                }
                try
                {
                    await recipient.Session.SendAsync(
                        PacketBuilder.ServerNote(line),
                        cancellationToken,
                        "DuelArenaDefeat");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine($"[duel-arena] defeat notice failed: {error.Message}");
                }
            }
        }
    }

    internal static string DuelArenaDefeatLine(
        string winner,
        (int Wins, int Losses) winnerRecord,
        string loser,
        (int Wins, int Losses) loserRecord)
    {
        static string Ascii(string value) =>
            new(value.Select(static c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        return $"Duel Arena: {Ascii(winner)} ({winnerRecord.Wins}W {winnerRecord.Losses}L) " +
            $"defeated {Ascii(loser)} ({loserRecord.Wins}W {loserRecord.Losses}L).";
    }

    private static Guid CreateDuelArenaEntitlementId(
        int attackerId,
        int targetId,
        byte mapId)
    {
        Span<byte> source = stackalloc byte[13];
        BinaryPrimitives.WriteInt32LittleEndian(source, attackerId);
        BinaryPrimitives.WriteInt32LittleEndian(source[4..], targetId);
        source[8] = mapId;
        BinaryPrimitives.WriteUInt32LittleEndian(
            source[9..],
            0x4C455544); // "DUEL"
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(source, digest);
        return new Guid(digest[..16]);
    }
}
