using System;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;

namespace Retry;

// Owns preview operations for a browser session.
internal sealed class BrowserPreview
{
    private readonly BrowserSession _session;
    internal BrowserPreview(BrowserSession session) => _session = session;
    // In MP runs (Players.Count > 1), wire the top-bar character
    // portrait's mouse input so a click cycles to the next player.
    // We use gui_input rather than NClickableControl's Released signal
    // because NTopBarPortrait is a bare Control (no signal infra).
    internal void HookPortraitForPlayerSwitch()
    {
        try
        {
            if (_session._history == null || _session._history.Players.Count < 2)
                return;
            var portrait = _session.SpawnedNRun?.GlobalUi?.TopBar?.Portrait;
            if (portrait == null)
                return;
            portrait.MouseFilter = Control.MouseFilterEnum.Stop;
            portrait.Connect("gui_input", Callable.From<InputEvent>(OnPortraitGuiInput));
            GD.Print($"{RetryMod.LogPrefix}browser portrait click hook installed (MP run, {_session._history.Players.Count} players)");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser portrait hook: {ex.Message}");
        }
    }

    internal void OnPortraitGuiInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            CycleToNextPlayer();
        }
    }

    internal void CycleToNextPlayer()
    {
        try
        {
            if (_session._history == null || _session._history.Players.Count < 2)
                return;
            int next = (_session._currentPlayerIdx + 1) % _session._history.Players.Count;
            SwitchToPlayer(next);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser cycle player: {ex.Message}");
        }
    }

    internal void SwitchToPlayer(int nextIdx)
    {
        if (_session._history == null || _session._runState == null)
            return;
        if (nextIdx < 0 || nextIdx >= _session._history.Players.Count)
            return;
        _session._currentPlayerIdx = nextIdx;
        _session._player = _session._history.Players[nextIdx];
        MegaCrit.Sts2.Core.Context.LocalContext.NetId = _session._player.Id;
        // Swap the character icon in the top-bar portrait. Initialize
        // adds the icon as a child, so clear existing children first
        // to avoid stacking portraits on repeated switches.
        try
        {
            var portrait = _session.SpawnedNRun?.GlobalUi?.TopBar?.Portrait;
            var live = _session._runState.Players.FirstOrDefault(p => p.NetId == _session._player.Id);
            if (portrait != null && live != null)
            {
                foreach (var child in portrait.GetChildren())
                    if (GodotObject.IsInstanceValid(child))
                        child.QueueFreeSafely();
                portrait.Initialize(live);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}portrait switch: {ex.Message}");
        }

        // Re-apply the floor preview for the new player — it handles
        // Hp/Gold/Deck/Inventory/Potion refresh and the room icon.
        if (_session._previewAct >= 0 && _session._previewFloor >= 0)
            UpdatePreviewForFloor(_session._previewAct, _session._previewFloor);
        else
            ApplyDeathFloorPreview();
        GD.Print($"{RetryMod.LogPrefix}browser switched to player {nextIdx} ({_session._player.Character} id={_session._player.Id})");
    }

    // Clear-then-Initialize NRelicInventory + NPotionContainer so
    // they reflect the live player's current Relics/Potions lists.
    // Required after InventoryInjector.Apply(silent: true) because
    // the inventory widgets normally rebuild themselves via the
    // RelicObtained / PotionProcured player events — which silent
    // apply suppressed. Initialize itself only appends (it doesn't
    // clear), so we manually flush existing holders first.
    internal void RefreshInventoryHud()
    {
        try
        {
            var inv = _session.SpawnedNRun?.GlobalUi?.RelicInventory;
            if (inv != null && _session._runState != null)
            {
                foreach (var c in inv.GetChildren())
                    if (GodotObject.IsInstanceValid(c))
                        c.QueueFreeSafely();
                var nodesField = typeof(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory).GetField("_relicNodes", BindingFlags.NonPublic | BindingFlags.Instance);
                if (nodesField?.GetValue(inv)is System.Collections.IList list)
                    list.Clear();
                inv.Initialize(_session._runState);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}relic hud refresh: {ex.Message}");
        }

        try
        {
            var pots = _session.SpawnedNRun?.GlobalUi?.TopBar?.PotionContainer;
            if (pots != null && _session._runState != null)
            {
                // NPotionContainer keeps holders as children of a sub-
                // Control (_potionHolders = MarginContainer/PotionHolders)
                // AND tracks them in a private _holders list. Clearing
                // children of NPotionContainer itself doesn't touch
                // either, so re-Initialize would see _holders.Count >=
                // MaxPotionCount and GrowPotionHolders becomes a no-op
                // — net effect was zero visible holders. Reset both
                // via reflection before Initialize.
                var ptype = typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionContainer);
                var bf = BindingFlags.Instance | BindingFlags.NonPublic;
                if (ptype.GetField("_potionHolders", bf)?.GetValue(pots)is Control holdersParent)
                {
                    foreach (var ch in holdersParent.GetChildren())
                        if (GodotObject.IsInstanceValid(ch))
                            ch.QueueFreeSafely();
                }

                if (ptype.GetField("_holders", bf)?.GetValue(pots)is System.Collections.IList holderList)
                {
                    holderList.Clear();
                }

                pots.Initialize(_session._runState);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}potion hud refresh: {ex.Message}");
        }
    }

    // Apply the historical run's final-floor state on first browser
    // open — last floor of the last act in MapPointHistory. Falls back
    // to no-op if history is empty.
    internal void ApplyDeathFloorPreview()
    {
        try
        {
            if (_session._history == null)
                return;
            int lastAct = _session._history.MapPointHistory.Count - 1;
            if (lastAct < 0)
                return;
            int lastFloor = _session._history.MapPointHistory[lastAct].Count - 1;
            if (lastFloor < 0)
                return;
            UpdatePreviewForFloor(lastAct, lastFloor);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser death-floor preview: {ex.Message}");
        }
    }

    // Pull HP/gold from the player's recorded stats at this floor and
    // shove them into the live RunState so the TopBar widgets reflect
    // the historical state. Purely visual — RetryRunner rebuilds the
    // real state from history when the user confirms.
    internal void UpdatePreviewForFloor(int actIdx, int floor)
    {
        try
        {
            if (_session._history == null || _session._player == null || _session._runState == null)
                return;
            if (actIdx >= _session._history.MapPointHistory.Count)
                return;
            var entries = _session._history.MapPointHistory[actIdx];
            if (floor >= entries.Count)
                return;
            // Reconstruct every historical participant at this node, not
            // just the portrait currently selected in the top bar. The
            // multiplayer player strip reads each live Player directly;
            // leaving the others untouched made it show their starter
            // decks/relics until each portrait had been selected once.
            PlayerStateSnapshot? snapshot = null;
            foreach (var historicalPlayer in _session._history.Players)
            {
                var playerSnapshot = StateReconstructor.ReconstructAtTarget(_session._history, actIdx, floor, historicalPlayer);
                // silent=true: the browser is a preview, not a live run.
                // Avoid replaying every card/relic acquisition animation;
                // the visible HUD widgets are refreshed explicitly below.
                InventoryInjector.Apply(_session._runState, playerSnapshot, silent: true);
                if (historicalPlayer.Id == _session._player.Id)
                    snapshot = playerSnapshot;
            }

            if (snapshot == null)
                throw new InvalidOperationException($"selected historical player {_session._player.Id} was not reconstructed");
            // Force-re-init the topbar scalar widgets (HP/Gold/Deck-count
            // badge). These read directly from player rather than
            // subscribing to scalar-change events, so Initialize is the
            // cleanest way to refresh them.
            var topBar = _session.SpawnedNRun?.GlobalUi?.TopBar;
            var p = _session._runState.Players.FirstOrDefault(x => x.NetId == _session._player.Id) ?? _session._runState.Players.FirstOrDefault();
            if (topBar != null && p != null)
            {
                try
                {
                    topBar.Hp?.Initialize(p);
                }
                catch
                {
                }

                try
                {
                    topBar.Gold?.Initialize(p);
                }
                catch
                {
                }

                try
                {
                    topBar.Deck?.Initialize(p);
                }
                catch
                {
                }
            }

            // Relic visibility is now handled at the source via the
            // NRelicInventory_Add_Patch below — it forces startsShown
            // to true while the browser is active so InventoryInjector's
            // AddRelicInternal → OnRelicObtained → Add chain produces
            // already-visible holders. No re-Initialize needed (that
            // path duplicates because Initialize doesn't clear
            // _relicNodes before re-adding).
            // Update the top-bar room-type icon to match the selected
            // floor (was stuck on the "no current room" red-X fallback).
            try
            {
                UpdateRoomIconForFloor(actIdx, floor);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser room-icon refresh: {ex.Message}");
            }

            // Silent apply skipped the RelicObtained / PotionProcured
            // events the UI listens to, so re-Initialize the inventory
            // widgets after clearing their existing child holders to
            // pick up the new state without duplicates.
            RefreshInventoryHud();
            _session._previewAct = actIdx;
            _session._previewFloor = floor;
            GD.Print($"{RetryMod.LogPrefix}browser preview state: hp={snapshot.CurrentHp}/{snapshot.MaxHp} gold={snapshot.Gold} deck={snapshot.Deck.Count} relics={snapshot.Relics.Count} potions={snapshot.Potions.Count}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser preview EXC: {ex.Message}\n{ex.StackTrace}");
        }
    }

    internal void UpdateRoomIconForFloor(int actIdx, int floor)
    {
        if (_session._history == null || _session._runState == null)
            return;
        if (actIdx >= _session._history.MapPointHistory.Count)
            return;
        var entries = _session._history.MapPointHistory[actIdx];
        if (floor < 0 || floor >= entries.Count)
            return;
        var entry = entries[floor];
        var pointType = entry.MapPointType;
        // Pull the resolved room (and any specific modelId) from the
        // historical Rooms list. For a normal floor that's a single
        // entry; for Unknown rooms it's the resolved type. Boss /
        // Ancient model ids come from the act so the icon matches the
        // specific boss / ancient encountered.
        MegaCrit.Sts2.Core.Rooms.RoomType roomType = MegaCrit.Sts2.Core.Rooms.RoomType.Unassigned;
        ModelId? modelId = null;
        if (entry.Rooms != null && entry.Rooms.Count > 0)
        {
            var lastRoom = entry.Rooms[entry.Rooms.Count - 1];
            roomType = lastRoom.RoomType;
            modelId = lastRoom.ModelId;
        }
        else
        {
            // Fall back to MapPointType when no rooms recorded.
            roomType = pointType switch
            {
                MapPointType.Monster => MegaCrit.Sts2.Core.Rooms.RoomType.Monster,
                MapPointType.Elite => MegaCrit.Sts2.Core.Rooms.RoomType.Elite,
                MapPointType.Boss => MegaCrit.Sts2.Core.Rooms.RoomType.Boss,
                MapPointType.Treasure => MegaCrit.Sts2.Core.Rooms.RoomType.Treasure,
                MapPointType.Shop => MegaCrit.Sts2.Core.Rooms.RoomType.Shop,
                MapPointType.RestSite => MegaCrit.Sts2.Core.Rooms.RoomType.RestSite,
                _ => MegaCrit.Sts2.Core.Rooms.RoomType.Monster,
            };
        }

        if (modelId == null && actIdx < _session._runState.Acts.Count)
        {
            var act = _session._runState.Acts[actIdx];
            if (pointType == MapPointType.Boss)
                modelId = act.BossEncounter?.Id;
            else if (pointType == MapPointType.Ancient)
                modelId = act.Ancient?.Id;
        }

        var topBar = _session.SpawnedNRun?.GlobalUi?.TopBar;
        if (topBar?.RoomIcon == null)
            return;
        _session._roomIconField ??= typeof(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarRoomIcon).GetField("_roomIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        _session._roomIconOutlineField ??= typeof(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarRoomIcon).GetField("_roomIconOutline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var iconPath = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetRoomIconPath(pointType, roomType, modelId);
        var outlinePath = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetRoomIconOutlinePath(pointType, roomType, modelId);
        var iconTexture = LoadRoomIconTexture(iconPath, pointType, roomType, outline: false);
        var outlineTexture = LoadRoomIconTexture(outlinePath, pointType, roomType, outline: true);
        var iconRect = _session._roomIconField?.GetValue(topBar.RoomIcon) as TextureRect;
        var outlineRect = _session._roomIconOutlineField?.GetValue(topBar.RoomIcon) as TextureRect;
        if (iconRect != null)
        {
            if (iconTexture != null)
            {
                iconRect.Visible = true;
                iconRect.Texture = iconTexture;
            }
            else
            {
                iconRect.Visible = false;
            }
        }

        if (outlineRect != null)
        {
            if (outlineTexture != null)
            {
                outlineRect.Visible = true;
                outlineRect.Texture = outlineTexture;
            }
            else
            {
                outlineRect.Visible = false;
            }
        }
    }

    internal Texture2D? LoadRoomIconTexture(string? preferredPath, MapPointType pointType, MegaCrit.Sts2.Core.Rooms.RoomType roomType, bool outline)
    {
        string? path = preferredPath;
        if (path == null || !ResourceLoader.Exists(path))
        {
            path = outline ? MegaCrit.Sts2.Core.Helpers.ImageHelper.GetRoomIconOutlinePath(pointType, roomType, modelId: null) : MegaCrit.Sts2.Core.Helpers.ImageHelper.GetRoomIconPath(pointType, roomType, modelId: null);
        }

        if (path == null || !ResourceLoader.Exists(path))
            return null;
        return MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetCompressedTexture2D(path);
    }
}
