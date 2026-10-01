// Tiny holder of "a retry is currently launching" state. Set by
// RetryRunner before kicking off the new-run pipeline; cleared once
// we've finished navigating to the target node. Harmony patches that
// need to alter standard new-run behavior check these flags.
//
// The flags must be cleared in a finally on the launch side —
// leaving them set across a run would corrupt later new runs the
// player starts from the menu.
namespace Retry;

public static class RetryContext
{
    public static bool IsRetrying;

    // One-shot: suppress the auto-EnterMapCoord on StartingMapPoint
    // that EnterAct(0) does when StartedWithNeow=true. Cleared by
    // RunManager_EnterMapCoord_Patch as soon as it fires once, so a
    // later EnterMapCoord on the actual target room passes through
    // unchanged. See SkipNeowEntryPatch for rationale.
    public static bool SkipNextNeowEntry;

    // When the retry target's MapPointType is Unknown, the game
    // rolls a concrete RoomType via Odds.UnknownMapPoint. We don't
    // have the original odds counter state, so we tell the
    // RollRoomTypeFor patch what the historical resolution was and
    // force it. Set right before EnterMapCoord(target); cleared
    // after.
    public static MegaCrit.Sts2.Core.Rooms.RoomType? TargetExpectedRoomType;

    // Historical card-reward offer (SerializableCard list) for the
    // target combat. CardFactory.CreateForReward Prefix consumes
    // this once and clears it. Without it, post-combat card rewards
    // are rolled fresh from CombatCardGeneration RNG and differ
    // from the original 3 the player saw.
    public static System.Collections.Generic.List<MegaCrit.Sts2.Core.Saves.Runs.SerializableCard>? TargetCardChoices;

    // Historical relic picked at the target room (Treasure, Elite,
    // or Boss). One-shot — consumed by the next
    // RelicFactory.PullNextRelicFromFront/Back call. Without this,
    // relic rewards are rolled from the grab-bag using the relic
    // RNG, producing a different relic than the original.
    public static MegaCrit.Sts2.Core.Models.ModelId? TargetExpectedRelic;

    // Multiplayer history hosting hands a completely native save to
    // LoadRunLobby. None of the single-player reconstruction hooks may
    // remain armed once that lobby is opened, because clients without
    // Retry would not execute the matching override.
    public static void ResetAll()
    {
        IsRetrying = false;
        SkipNextNeowEntry = false;
        TargetExpectedRoomType = null;
        TargetCardChoices = null;
        TargetExpectedRelic = null;
    }
}
