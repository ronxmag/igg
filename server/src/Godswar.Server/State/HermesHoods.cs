using System.Collections.Concurrent;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.State;

/// <summary>
/// ITEMS-HERMES (F-56): Hermes Hood (4130, "HideCamp1"). Client data: ItemBaseAttribute.xml Skill="4521"
/// UndoSkillID="4520" ExpiredTime="0,0,0,0,30,0" (30 minutes); Magic.ini [4521] "道具:改变阵营" (item: change camp)
/// ScriptID=ChangeCamp1, [4520] "道具:恢复阵营" (restore camp) ScriptID=ChangeCamp0; EquipDescription.dat: "Wear it to
/// become a Green Name for 30 minutes. Players will mistake you for their companion."
/// The client never runs a ChangeCamp script itself (the exe has no such string): the camp a viewer sees comes from
/// the server's remote-player frames, 0x2725 spawn byte 53 and 0x27B6 status byte 62. While a hood is active every
/// OTHER player is sent their OWN camp in those two bytes, so the wearer reads as a companion (green name). The
/// wearer's own client and every server-side rule (PvP, guards, faction maps, chat) keep the real camp.
/// Kept in memory and in public.hermes_hood_state (runtime DDL), reloaded at start-up.
/// </summary>
internal static class HermesHoods
{
    public const int ItemId = 4130;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30); // ExpiredTime "0,0,0,0,30,0"
    private static readonly ConcurrentDictionary<int, DateTimeOffset> Hoods = new();

    public static void Set(int characterId, DateTimeOffset until) => Hoods[characterId] = until;

    /// <summary>F-156: ends a hood early (wearer attacked another player). True when one was active.</summary>
    public static bool Clear(int characterId) =>
        Hoods.TryRemove(characterId, out var until) && DateTimeOffset.UtcNow < until;

    public static bool TryGetExpiry(int characterId, out DateTimeOffset until)
    {
        if (Hoods.TryGetValue(characterId, out until) && DateTimeOffset.UtcNow < until)
        {
            return true;
        }

        Hoods.TryRemove(characterId, out _);
        return false;
    }

    public static bool IsActive(int characterId) =>
        !Hoods.IsEmpty &&
        Finalise2Rates.On(Finalise2RateKeys.HermesHood) &&
        TryGetExpiry(characterId, out _);

    /// <summary>The camp byte shown to a viewer, including arena hostility and Hermes Hood projection.</summary>
    public static byte ShownCamp(GameCharacter character, byte? viewerCamp)
    {
        if (viewerCamp is not (GameDefaults.SpartaCamp or GameDefaults.AthensCamp))
        {
            return character.Camp;
        }

        // Matching factions must look hostile in the Duel Arena so the client
        // allows target selection. This changes presentation only; server PvP
        // admission still enforces party protection and combat-area boundaries.
        if (ArenaFeatureGates.DuelArenaPvp &&
            DuelArenaCombatArea.Contains(
                character.CurrentMap,
                character.PositionX,
                character.PositionZ) &&
            character.Camp == viewerCamp.Value)
        {
            return viewerCamp.Value == GameDefaults.SpartaCamp
                ? GameDefaults.AthensCamp
                : GameDefaults.SpartaCamp;
        }

        return IsActive(character.Id)
            ? viewerCamp.Value
            : character.Camp;
    }

    public static bool RequiresRecipientCampProjection(GameCharacter character) =>
        IsActive(character.Id) ||
        (ArenaFeatureGates.DuelArenaPvp &&
         DuelArenaCombatArea.Contains(
             character.CurrentMap,
             character.PositionX,
             character.PositionZ));
}
