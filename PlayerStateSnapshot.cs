// Player state reconstructed at a specific historical node.
//
// Builds the per-player half of a SerializableRun: deck, relics,
// potions, HP, gold, and the various "discovered / completed"
// trackers. Cards and relics carry FloorAddedToDeck so the in-game
// "added on floor X" badges still read correctly post-retry.
//
// The companion RetryTarget holds run-level metadata (seed, char,
// ascension, target act/floor, modifiers).
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public sealed class PlayerStateSnapshot
{
    // Identity / character
    public ulong NetId;
    public ModelId? CharacterId;

    // Health
    public int CurrentHp;
    public int MaxHp;

    // Economy
    public int Gold;
    public int MaxPotionSlotCount = 3;

    // Inventory (final state at target node)
    public List<SerializableCard> Deck = new();
    public List<SerializableRelic> Relics = new();
    public List<SerializablePotion> Potions = new();

    // Tracking — needed so events don't repeat, quests don't reset, etc.
    public List<ModelId> EventsSeen = new();
    public List<ModelId> CompletedQuests = new();
}

public sealed class RetryTarget
{
    // Run-level metadata
    public string Seed = "";
    public int Ascension;
    public GameMode GameMode = GameMode.Standard;
    public List<ModelId> ActIds = new();
    public List<SerializableModifier> Modifiers = new();

    // Snapshot of the source history's wall-clock fields so the
    // in-run timer can be offset to roughly reflect where the
    // historical run was at the target node.
    public float OriginalRunTime;
    public int OriginalTotalFloors;

    // Where the player is being placed
    // ActIndex is the 0-based index into MapPointHistory[]
    // FloorIndex is the 0-based index into MapPointHistory[ActIndex]
    public int TargetActIndex;
    public int TargetFloorIndex;

    // Truncated history (entries before target). When the target is
    // selected on (act 1, floor 5), MapPointHistorySoFar contains
    // entries [act0][..], [act1][0..4].
    public List<List<MegaCrit.Sts2.Core.Runs.History.MapPointHistoryEntry>> MapPointHistorySoFar = new();

    // Selected player, retained for the single-player reconstruction
    // path and for run-level history simulation.
    public PlayerStateSnapshot Player = new();

    // All historical participants. Multiplayer history hosting fills
    // this list and serializes each player under their original NetId.
    public List<PlayerStateSnapshot> Players = new();
}
