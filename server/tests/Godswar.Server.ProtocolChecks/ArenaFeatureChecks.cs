using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// ARENA build: Duel Arena PvP admission, Parnassus entry rules and the AFK
/// auto-revival command. Also the regression guard for the shared PvP call sites:
/// with the gate off, and for every pair outside the arena combat floor with it on,
/// the new admission must equal the stock faction rule exactly.
/// </summary>
internal static class ArenaFeatureChecks
{
    public const string CheckName =
        "Arena build: duel arena PvP, Parnassus entry and AFK revival keep stock behaviour when off";

    public static Task RunAsync()
    {
        try
        {
            CheckCombatArea();
            CheckAdmission();
            CheckProtectionBypass();
            CheckArenaCampProjection();
            CheckParnassus();
            CheckAfkRevivalCommand();
        }
        finally
        {
            ArenaFeatureGates.Override = null;
        }
        return Task.CompletedTask;
    }

    private static void CheckCombatArea()
    {
        foreach (var (x, z) in DuelArenaEntryDestinations.All)
        {
            Check.True(DuelArenaCombatArea.Contains(57, x, z), $"Gatekeeper entry point {x},{z} is on the combat floor");
        }
        Check.True(DuelArenaCombatArea.Contains(57, -48f, 40f), "the Ward's floor is the combat floor");
        foreach (var npc in DuelArenaCapturedLayout.Npcs.Where(static npc => npc.NpcKey != DuelArenaCapturedLayout.WardNpcKey))
        {
            Check.True(!DuelArenaCombatArea.Contains(57, npc.X, npc.Z), $"{npc.NpcKey} stands in the waiting area");
        }
        Check.True(!DuelArenaCombatArea.Contains(57, -104f, 96f), "transporter arrival is the waiting area");
        Check.True(!DuelArenaCombatArea.Contains(13, -5f, 32f) && !DuelArenaCombatArea.Contains(57, float.NaN, 0f),
            "other maps and bad positions are never the combat floor");
        Check.Equal((byte)57, DuelArenaCombatArea.ReviveLanding.MapId, "arena revive stays in the arena");
        Check.True(!DuelArenaCombatArea.Contains(57, DuelArenaCombatArea.ReviveLanding.X, DuelArenaCombatArea.ReviveLanding.Z),
            "arena revive lands in the waiting area");
    }

    private static void CheckAdmission()
    {
        var registry = new GameSessionRegistry(gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        var catalog = GameplayContentTestFixtures.Runtime.PvpWorldAuthority;
        var now = DateTimeOffset.Parse("2026-11-03T10:00:00Z");
        Check.True(catalog.IsSafeZone(57), "fixture: map 57 is a safe zone for the stock rule");

        var pairs = new List<(GameCharacter A, GameCharacter B, string Name)>();
        foreach (var (mapA, xa, za, mapB, xb, zb, label) in new (byte, float, float, byte, float, float, string)[]
                 {
                     (57, -5, 24, 57, -5, 28, "arena floor"),
                     (57, -104, 96, 57, -104, 92, "arena waiting area"),
                     (57, -5, 24, 57, -104, 92, "floor to waiting area"),
                     (13, 100, -100, 13, 102, -100, "faction map 13"),
                     (68, 0, 0, 68, 2, 0, "Parnassus"),
                     (1, 165, -97, 1, 166, -97, "Athens city"),
                     (13, -5, 24, 57, -5, 24, "different maps")
                 })
        {
            foreach (var (campA, campB) in new (byte, byte)[] { (0, 0), (0, 1), (1, 1), (1, 0) })
            {
                pairs.Add((Player(100, mapA, xa, za, campA), Player(200, mapB, xb, zb, campB),
                    $"{label} camps {campA}/{campB}"));
            }
        }

        ArenaFeatureGates.Override = false;
        foreach (var (a, b, name) in pairs)
        {
            Check.Equal(catalog.EvaluateOpposingFaction(a, b, now), registry.EvaluatePlayerPvpAdmission(a, b, now),
                $"gate off: stock admission unchanged ({name})");
        }

        ArenaFeatureGates.Override = true;
        foreach (var (a, b, name) in pairs)
        {
            var arena = DuelArenaCombatArea.Contains(a.CurrentMap, a.PositionX, a.PositionZ) &&
                DuelArenaCombatArea.Contains(b.CurrentMap, b.PositionX, b.PositionZ);
            var result = registry.EvaluatePlayerPvpAdmission(a, b, now);
            if (!arena)
            {
                Check.Equal(catalog.EvaluateOpposingFaction(a, b, now), result,
                    $"gate on, outside the arena floor: stock admission unchanged ({name})");
                continue;
            }

            Check.True(result.Allowed && result.EntitlementKind == PvpEntitlementKind.MutualDuel &&
                result.Admits(a.Id, b.Id, 57), $"gate on: arena floor admits any two players ({name})");
        }

        var first = Player(100, 57, -5, 24, 1);
        var second = Player(200, 57, -5, 28, 1);
        var party = GameSessionRegistry.EvaluateDuelArenaPair(first, second, now, sameParty: true);
        Check.True(!party.Allowed && party.Failure == PvpEligibilityFailure.MissingEntitlement,
            "teammates cannot damage each other (IGG rule 3)");
        Check.True(!GameSessionRegistry.EvaluateDuelArenaPair(first, first, now, sameParty: false).Allowed,
            "no self target");
        second.CurrentHp = 0;
        Check.True(!GameSessionRegistry.EvaluateDuelArenaPair(first, second, now, sameParty: false).Allowed,
            "no dead target");

        var line = GameSessionRegistry.DuelArenaDefeatLine("Rigtest", (1, 0), "Réb", (0, 1));
        Check.Equal("Duel Arena: Rigtest (1W 0L) defeated R?b (0W 1L).", line, "defeat notice text");
        Check.Equal(260, PacketBuilder.ServerNote(line).Length, "defeat notice is a native server note");
        Check.True(PacketBuilder.ServerNote(GameSessionRegistry.DuelArenaDefeatLine(
            new string('x', 32), (999, 999), new string('y', 32), (999, 999))).Length == 260,
            "longest names still fit a server note");
        var dotaTiers = new[]
        {
            (2, "Double Kill"), (3, "Triple Kill"), (4, "Ultra Kill"),
            (6, "Rampage"), (7, "Killing Spree"), (8, "Dominating"),
            (9, "Mega Kill"), (10, "Unstoppable"), (11, "Wicked Sick"),
			(12, "Monster Kill"), (13, "Godlike"), (14, "Beyond Godlike")
        };
        foreach (var (streak, tier) in dotaTiers)
        {
            Check.True(GameSessionRegistry.TryGetDotaKillStreakTier(streak, out var actualTier) &&
                actualTier == tier, $"Dota kill-streak tier at {streak}");
        }
        Check.True(!GameSessionRegistry.TryGetDotaKillStreakTier(0, out _) &&
            !GameSessionRegistry.TryGetDotaKillStreakTier(2, out _),
            "no Dota announcer tier before three kills");
        var streakNote = PacketBuilder.DotaKillStreakAnnouncement("Rigtest", "S#1002#5");
        Check.Equal(137, streakNote.Length, "Lua streak announcement uses native PythonNote frame");
        Check.Equal((ushort)10038,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(streakNote.AsSpan(2, 2)),
            "Lua streak announcement uses PythonNote opcode");
        Check.Equal(PacketBuilder.DotaKillStreakNoteType,
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(streakNote.AsSpan(4, 4)),
            "Lua streak announcement dispatches the dedicated message type");
        Check.Equal((byte)0, streakNote[8], "Lua streak announcement is centered, not personal");
        Check.Equal("Rigtest", System.Text.Encoding.ASCII.GetString(streakNote, 9, 7).TrimEnd('\0'),
            "Lua announcer receives the credited killer name");
        Check.Equal("S#1002#5", System.Text.Encoding.ASCII.GetString(streakNote, 73, 8).TrimEnd('\0'),
            "Lua announcer receives the mapped Mega Kill message ID and count");
        Check.Equal("1001", GameSessionRegistry.DotaStreakMessageId(2),
            "Dota Double Kill uses the requested STREAK000 key");
        Check.Equal("1011", GameSessionRegistry.DotaStreakMessageId(13),
            "Beyond Godlike uses the final mapped streak key");
        Check.Equal("1012", GameSessionRegistry.DotaStreakMessageId(14),
            "Beyond Godlike uses the final Lua label key");
        var shutdownNote = PacketBuilder.DotaKillStreakAnnouncement("Winner", "E#Rigtest#1000#3");
        Check.Equal("E#Rigtest#1000#3",
            System.Text.Encoding.ASCII.GetString(shutdownNote, 73, 16).TrimEnd('\0'),
            "shutdown note carries loser name and mapped tier ID");
        Check.Equal("1002", GameSessionRegistry.DotaStreakMessageId(5),
            "Mega Kill maps to STREAK002");
    }

    private static void CheckProtectionBypass()
    {
        var registry = new GameSessionRegistry(gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        var now = DateTimeOffset.UtcNow;
        var protectedArenaPlayer = Player(400, 57, -5, 24, GameDefaults.SpartaCamp);
        var attacker = Player(401, 57, -5, 28, GameDefaults.SpartaCamp);
        protectedArenaPlayer.PeaceUntil = now.AddMinutes(5);

        ArenaFeatureGates.Override = true;
        var arena = registry.EvaluatePvpBasicAttack(attacker, protectedArenaPlayer, now);
        Check.True(arena.Allowed && arena.EntitlementKind == PvpEntitlementKind.MutualDuel,
            "active peace protection does not block Duel Arena basic attacks");

        var opposingFactionProtected = Player(402, 57, -5, 32, GameDefaults.AthensCamp);
        opposingFactionProtected.PeaceUntil = now.AddMinutes(5);
        var opposingDuel = registry.EvaluatePvpBasicAttack(attacker, opposingFactionProtected, now);
        Check.True(opposingDuel.Allowed && opposingDuel.EntitlementKind == PvpEntitlementKind.MutualDuel,
            "active peace protection does not block opposing-faction Duel Arena attacks");
        Check.True(registry.TrainingDummySpawnPkMode(opposingFactionProtected) == 1,
            "opposing-faction protected arena player gets hostile PK mode");
        var opposingSpawn = PacketBuilder.PlayerWorldSpawn(
            opposingFactionProtected,
            0x715u,
            [new ClientStatusEffect(650, 300)],
            pkMode: registry.TrainingDummySpawnPkMode(opposingFactionProtected),
            viewerCamp: GameDefaults.SpartaCamp);
        var opposingStatus = PacketBuilder.RemotePlayerStatusUpdate(
            opposingFactionProtected,
            0x715u,
            Godswar.Server.State.ClientStatusAggregate.Empty,
            pkMode: registry.TrainingDummySpawnPkMode(opposingFactionProtected),
            viewerCamp: GameDefaults.SpartaCamp);
        Check.Equal((byte)1, opposingSpawn[0x50],
            "opposing-faction protected spawn does not carry protected PK mode");
        Check.Equal((byte)1, opposingStatus[232],
            "opposing-faction protected status does not carry protected PK mode");
        Check.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(opposingSpawn.AsSpan(178, 2)),
            "opposing-faction protected spawn omits Harmony status");

        protectedArenaPlayer.CurrentMap = 13;
        protectedArenaPlayer.PositionX = 100;
        protectedArenaPlayer.PositionZ = -100;
        attacker.CurrentMap = 13;
        attacker.PositionX = 102;
        attacker.PositionZ = -100;
        var outside = registry.EvaluatePvpBasicAttack(attacker, protectedArenaPlayer, now);
        Check.True(!outside.Allowed && outside.Failure == PvpEligibilityFailure.SafeZone,
            "active peace protection still blocks basic attacks outside the Duel Arena");

        var opposingOutsideAttacker = Player(403, 13, 102, -100, GameDefaults.AthensCamp);
        var opposingOutsideProtected = Player(404, 13, 100, -100, GameDefaults.SpartaCamp);
        opposingOutsideProtected.PeaceUntil = now.AddMinutes(5);
        var opposingOutside = registry.EvaluatePlayerPvpAdmission(
            opposingOutsideAttacker,
            opposingOutsideProtected,
            now);
        Check.True(opposingOutside.Allowed &&
            opposingOutside.EntitlementKind == PvpEntitlementKind.OpposingFaction,
            "active peace protection does not block opposing-faction PvP on an unsafe map outside the Duel Arena");
        Check.True(registry.TrainingDummySpawnPkMode(opposingOutsideProtected) == 1,
            "peace-protected opposing-faction target is projected hostile on an unsafe PvP map");
        Check.Equal((byte)1, PacketBuilder.PlayerWorldSpawn(
                opposingOutsideProtected,
                0x719u,
                pkMode: registry.TrainingDummySpawnPkMode(opposingOutsideProtected),
                viewerCamp: GameDefaults.AthensCamp)[0x50],
            "opposing-faction PvP-map spawn is attackable despite peace protection");
        Check.Equal((byte)1, PacketBuilder.RemotePlayerStatusUpdate(
                opposingOutsideProtected,
                0x719u,
                Godswar.Server.State.ClientStatusAggregate.Empty,
                pkMode: registry.TrainingDummySpawnPkMode(opposingOutsideProtected),
                viewerCamp: GameDefaults.AthensCamp)[232],
            "opposing-faction PvP-map status remains attackable");
        Check.True(registry.LocalPlayerPkMode(opposingOutsideAttacker) == 1,
            "local opposing-faction attacker is projected hostile on an unsafe PvP map");
        Check.Equal((byte)1, PacketBuilder.PlayerWorldSpawn(
                opposingOutsideProtected,
                0x71Bu,
                pkMode: registry.TrainingDummySpawnPkMode(opposingOutsideProtected),
                viewerCamp: GameDefaults.AthensCamp)[0x50],
            "peace-protected target on map 13 is sent with hostile PK mode");
        var activePeaceProjection = PacketBuilder.RemotePlayerStatusUpdate(
            opposingOutsideProtected,
            0x71Au,
            Godswar.Server.State.ClientStatusAggregate.Empty,
            pkMode: registry.TrainingDummySpawnPkMode(opposingOutsideProtected),
            viewerCamp: GameDefaults.AthensCamp);
        Check.Equal((byte)1, activePeaceProjection[232],
            "active peace target remains hostile to opposing-faction viewers on PvP maps");
        var outsideOpposingStatus = PacketBuilder.PlayerStatusEffects(
            opposingOutsideProtected,
            0x716u,
            [new ClientStatusEffect(650, 300), new ClientStatusEffect(500, 300)],
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                outsideOpposingStatus.AsSpan(8, 4)),
            "unsafe-map opposing-faction status omits the client-blocking peace icon");
        opposingOutsideProtected.PeaceUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        var activePeaceOutsideStatus = PacketBuilder.PlayerStatusEffects(
            opposingOutsideProtected,
            0x718u,
            [new ClientStatusEffect(650, 300), new ClientStatusEffect(500, 300)],
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                activePeaceOutsideStatus.AsSpan(8, 4)),
            "active peace status is hidden on the unsafe PvP map");

        var opposingOutsideBasic = registry.EvaluatePvpBasicAttack(
            opposingOutsideAttacker,
            opposingOutsideProtected,
            now);
        Check.True(opposingOutsideBasic.Allowed &&
            opposingOutsideBasic.EntitlementKind == PvpEntitlementKind.OpposingFaction,
            "basic attacks also allow opposing-faction PvP outside the Arena when map authority allows it");

        var safeMapProtected = Player(405, 1, 165, -97, GameDefaults.SpartaCamp);
        safeMapProtected.PeaceUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        Check.True(registry.TrainingDummySpawnPkMode(safeMapProtected) is null,
            "peace-protected target in a safe map keeps protected PK projection");
        Check.True(registry.LocalPlayerPkMode(safeMapProtected) is null,
            "local player in a safe map keeps protected PK mode");
        Check.Equal((byte)5, PacketBuilder.PlayerWorldSpawn(
                safeMapProtected,
                0x71Cu,
                pkMode: registry.TrainingDummySpawnPkMode(safeMapProtected),
                viewerCamp: GameDefaults.AthensCamp)[0x50],
            "peace-protected target in capital remains protected in spawn mode");
        var safeMapStatus = PacketBuilder.PlayerStatusEffects(
            safeMapProtected,
            0x717u,
            [new ClientStatusEffect(650, 300), new ClientStatusEffect(500, 300)],
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                safeMapStatus.AsSpan(8, 4)),
            "safe-map target keeps Harmony protection visible");

        var protectedOutside = new[]
        {
            new ClientStatusEffect(650, 300),
            new ClientStatusEffect(500, 300)
        };
        var outsidePacket = PacketBuilder.PlayerStatusEffects(
            opposingOutsideProtected,
            0x714u,
            protectedOutside,
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(outsidePacket.AsSpan(8, 4)),
            "PvP-map target suppresses Harmony protection in the client view");
    }

    private static void CheckArenaCampProjection()
    {
        var sameFactionArenaPlayer = Player(300, 57, -5, 24, GameDefaults.SpartaCamp);
        var opposingFactionArenaPlayer = Player(301, 57, -5, 28, GameDefaults.AthensCamp);
        var sameFactionOutsideArena = Player(302, 57, -104, 96, GameDefaults.SpartaCamp);

        ArenaFeatureGates.Override = false;
        Check.Equal(GameDefaults.SpartaCamp,
            HermesHoods.ShownCamp(sameFactionArenaPlayer, GameDefaults.SpartaCamp),
            "gate off: same-faction camp projection stays unchanged");

        var registry = new GameSessionRegistry(gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        ArenaFeatureGates.Override = true;
        Check.True(registry.TrainingDummySpawnPkMode(sameFactionArenaPlayer) == 1,
            "arena player is projected in hostile PK mode");
        Check.True(registry.TrainingDummySpawnPkMode(sameFactionOutsideArena) is null,
            "outside-arena player retains the normal protected PK mode");
        var localArenaStatus = PacketBuilder.PlayerStatusUpdate(
            sameFactionArenaPlayer,
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal((byte)1, localArenaStatus[232],
            "local attacker status uses hostile PK mode in the arena");
        var opposingFactionLocalStatus = PacketBuilder.PlayerStatusUpdate(
            opposingFactionArenaPlayer,
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal((byte)1, opposingFactionLocalStatus[232],
            "opposing-faction local status uses hostile PK mode in the arena");
        var outsideLocalStatus = PacketBuilder.PlayerStatusUpdate(
            sameFactionOutsideArena,
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal((byte)5, outsideLocalStatus[232],
            "local status retains protected PK mode outside the arena");
        ArenaFeatureGates.Override = false;
        Check.True(registry.TrainingDummySpawnPkMode(sameFactionArenaPlayer) is null,
            "arena remote PK-mode projection is disabled with the arena gate");
        var disabledLocalStatus = PacketBuilder.PlayerStatusUpdate(
            sameFactionArenaPlayer,
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal((byte)5, disabledLocalStatus[232],
            "arena local PK-mode projection is disabled with the arena gate");
        ArenaFeatureGates.Override = true;
        Check.Equal(GameDefaults.AthensCamp,
            HermesHoods.ShownCamp(sameFactionArenaPlayer, GameDefaults.SpartaCamp),
            "same-faction arena opponent is presented as hostile");
        Check.Equal(GameDefaults.AthensCamp,
            HermesHoods.ShownCamp(opposingFactionArenaPlayer, GameDefaults.SpartaCamp),
            "opposing-faction arena opponent keeps hostile camp presentation");
        Check.Equal(GameDefaults.SpartaCamp,
            HermesHoods.ShownCamp(sameFactionOutsideArena, GameDefaults.SpartaCamp),
            "same-faction player outside the combat area keeps normal presentation");

        var protectedStatus = new[]
        {
            new ClientStatusEffect(650, 300),
            new ClientStatusEffect(651, 300),
            new ClientStatusEffect(652, 300),
            new ClientStatusEffect(500, 300)
        };
        var spawn = PacketBuilder.PlayerWorldSpawn(
            sameFactionArenaPlayer,
            0x712u,
            protectedStatus,
            pkMode: registry.TrainingDummySpawnPkMode(sameFactionArenaPlayer),
            viewerCamp: GameDefaults.SpartaCamp);
        var status = PacketBuilder.PlayerStatusEffects(
            sameFactionArenaPlayer,
            0x712u,
            protectedStatus,
            Godswar.Server.State.ClientStatusAggregate.Empty);
        var remoteMode = PacketBuilder.RemotePlayerStatusUpdate(
            sameFactionArenaPlayer,
            0x712u,
            Godswar.Server.State.ClientStatusAggregate.Empty,
            pkMode: registry.TrainingDummySpawnPkMode(sameFactionArenaPlayer),
            viewerCamp: GameDefaults.SpartaCamp);
        Check.Equal(GameDefaults.AthensCamp, spawn[53],
            "arena hostility reaches the remote player spawn frame");
        Check.Equal(GameDefaults.AthensCamp, PacketBuilder.RemotePlayerStatusUpdate(
                sameFactionArenaPlayer,
                0x712u,
                Godswar.Server.State.ClientStatusAggregate.Empty,
                pkMode: registry.TrainingDummySpawnPkMode(sameFactionArenaPlayer),
                viewerCamp: GameDefaults.SpartaCamp)[62],
            "arena hostility reaches the remote player status frame");
        Check.Equal((byte)1, spawn[0x50],
            "arena spawn projects hostile PK mode");
        Check.Equal((byte)1, remoteMode[232],
            "arena status frame projects hostile PK mode");
        Check.Equal((ushort)1, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(spawn.AsSpan(178, 2)),
            "arena spawn hides only client peace statuses");
        Check.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8, 4)),
            "arena status update hides only client peace statuses");

        var outsideStatus = PacketBuilder.PlayerStatusEffects(
            sameFactionOutsideArena,
            0x713u,
            protectedStatus,
            Godswar.Server.State.ClientStatusAggregate.Empty);
        Check.Equal(4u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(outsideStatus.AsSpan(8, 4)),
            "peace statuses remain visible outside the combat area");
    }
    private static void CheckParnassus()
    {
        var published = BattlefieldTransporterProtocol.AthensInitialMenuSubIds;
        ArenaFeatureGates.Override = false;
        Check.True(GameClientHandler.BattlefieldTransporterMenu(published).SequenceEqual(published),
            "gate off: the transporter menu is the published one");
        ArenaFeatureGates.Override = true;
        var menu = GameClientHandler.BattlefieldTransporterMenu(published);
        Check.True(menu.SequenceEqual(published.Append(1002)), "gate on: Parnassus (1002) appended once");
        Check.True(ParnassusProtocol.WithEntryButton(menu).SequenceEqual(menu), "never appended twice");

        var root = Enumerable.Repeat(-1, 18).ToArray();
        Check.True(ParnassusProtocol.IsEntryClick(1, 1002, root), "root click on 1002");
        Check.True(!ParnassusProtocol.IsEntryClick(1, 1001, root) && !ParnassusProtocol.IsEntryClick(2, 1002, root) &&
            !ParnassusProtocol.IsEntryClick(1, 1002, root.Take(17).ToArray()) &&
            !ParnassusProtocol.IsEntryClick(1, 1002, [1002, .. root.Skip(1)]), "other clicks are not Parnassus");
        foreach (var (time, open) in new (string, bool)[]
                 {
                     ("11:59", false), ("12:00", true), ("17:59", true), ("18:00", false), ("18:59", false),
                     ("19:00", true), ("23:54", true), ("23:55", false), ("00:30", false)
                 })
        {
            Check.Equal(open, ParnassusProtocol.IsOpen(TimeOnly.Parse(time)), $"IGG access period at {time}");
        }
        Check.Equal((220f, -211f), ParnassusProtocol.Arrival(0), "Sparta arrives at its Main City Teleporter point");
        Check.Equal((-212f, 223f), ParnassusProtocol.Arrival(1), "Athens arrives at its Main City Teleporter point");
        Check.True(ParnassusProtocol.IsReturnTransporter(68, "Parnassus_003") &&
            ParnassusProtocol.IsReturnTransporter(68, "Parnassus_004") &&
            !ParnassusProtocol.IsReturnTransporter(1, "Parnassus_003") &&
            !ParnassusProtocol.IsReturnTransporter(68, "Parnassus_001"), "only the two map-68 transporters");
        // Review fix: the return transporters answer with the switch off too (no stranding on rollback).
        foreach (var gate in new[] { false, true })
        {
            ArenaFeatureGates.Override = gate;
            Check.True(GameClientHandler.ServesParnassusReturn(68, "Parnassus_003") &&
                GameClientHandler.ServesParnassusReturn(68, "Parnassus_004"),
                $"gate {(gate ? "on" : "off")}: Parnassus return transporters still answer on map 68");
            Check.True(!GameClientHandler.ServesParnassusReturn(1, "Parnassus_003") &&
                !GameClientHandler.ServesParnassusReturn(0, "Parnassus_004") &&
                !GameClientHandler.ServesParnassusReturn(68, "Parnassus_001") &&
                !GameClientHandler.ServesParnassusReturn(1, "BattlefieldTransporter"),
                $"gate {(gate ? "on" : "off")}: no other NPC is taken over");
        }
        ArenaFeatureGates.Override = true;
        Check.Equal(1005, ParnassusProtocol.ReturnButton("Parnassus_003"), "Athens return button");
        Check.Equal(1006, ParnassusProtocol.ReturnButton("Parnassus_004"), "Sparta return button");
        Check.True(BattlefieldTransporterProtocol.AthensInitialMenuSubIds.All(static id => id != 1002) &&
            BattlefieldTransporterProtocol.SpartaInitialMenuSubIds.All(static id => id != 1002),
            "the hashed published menus are unchanged");
    }

    private static void CheckAfkRevivalCommand()
    {
        var (off, offReply) = GameClientHandler.ParseAfkRevivalCommand([], null, 10);
        Check.True(off is null && offReply.Contains("OFF", StringComparison.Ordinal) &&
            offReply.Contains("10 Gold", StringComparison.Ordinal), "status when off names the guide price");
        var (on, _) = GameClientHandler.ParseAfkRevivalCommand(["on"], null, 10);
        Check.True(on is { DelaySeconds: 10, Limit: 0 }, "default: 10 s, no limit");
        var (custom, reply) = GameClientHandler.ParseAfkRevivalCommand(["on", "30", "3"], null, 10);
        Check.True(custom is { DelaySeconds: 30, Limit: 3 } && reply.Contains("at most 3", StringComparison.Ordinal),
            "delay and limit");
        var (bad, badReply) = GameClientHandler.ParseAfkRevivalCommand(["on", "2"], custom, 10);
        Check.True(ReferenceEquals(bad, custom) && badReply.Contains("5-300", StringComparison.Ordinal),
            "a bad delay keeps the old setting");
        var (stopped, _) = GameClientHandler.ParseAfkRevivalCommand(["off"], custom, 10);
        Check.True(stopped is null, "off");
        foreach (var r in new[] { offReply, reply, badReply })
        {
            Check.Equal(260, PacketBuilder.ServerNote(r).Length, "replies are native server notes");
        }
        ArenaFeatureGates.Override = null;
        Check.Equal(ArenaFeatureGates.GuideAfkRevivalGold, 10, "IGG price");
    }

    private static GameCharacter Player(int id, byte map, float x, float z, byte camp) =>
        new()
        {
            Id = id,
            AccountId = id,
            Name = $"Player{id}",
            CurrentMap = map,
            PositionX = x,
            PositionZ = z,
            Camp = camp,
            Level = 120,
            CurrentHp = 10_000,
            MaxHp = 10_000
        };
}
