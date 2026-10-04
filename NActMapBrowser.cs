// Act-map browser overlay. Spawns a real NRun off to the side as a
// child of NGame.Instance — NOT via RootSceneContainer.SetCurrentScene —
// so the user's current submenu (e.g. Run History) stays alive in the
// tree beneath us and we can return to it cleanly with a Back button.
//
// Tricks:
//   • NRun.Instance reads NGame.CurrentRunNode (= RootSceneContainer
//     .CurrentScene as NRun). Since CurrentScene is still the menu,
//     CurrentRunNode is null. We Harmony-patch the getter to return
//     our overlay NRun while the browser is active. Side effect: all
//     downstream code (NMapScreen.Instance, etc.) routes through our
//     NRun automatically.
//   • RunManager state is still primed (NMapScreen needs NetService,
//     MapSelectionSynchronizer, LocalContext.NetId). Back-button
//     calls CleanUp.
//   • Clicks: NMapPoint.IsTravelable bails on Traveled state unless
//     IsDebugTravelEnabled. We turn that on.
//   • Scrolling: NMapScreen.CanScroll requires _actAnimTween==null
//     or _canInterruptAnim; we set _hasPlayedAnimation=true before
//     Open to skip the fade-in entirely.
//   • Tab switch: regenerate the act's map, update VisitedMapCoords
//     and CurrentActIndex, SetMap, refresh NMapBg textures (they're
//     wired only to VisibilityChanged — we toggle to fire it).
//   • Back: tear down the overlay, CleanUp run state. The menu/Run
//     History under us is untouched.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

public static class NActMapBrowser
{
    public static bool Active { get; private set; }
    public static NRun? SpawnedNRun { get; private set; }

    private static RunHistory? _history;
    private static RunHistoryPlayer? _player;
    private static RunState? _runState;
    private static int _currentAct;
    private static ActMap?[]? _actMaps;
    private static List<MapCoord>[]? _actVisited;
    private static Control? _overlay; // the spawned NRun itself
    private static Control? _hud;     // floating overlay above NRun for tabs/confirm
    private static Control? _hiddenMenu;
    private static readonly List<Type> _poppedSubmenus = new();
    private static Button[]? _tabButtons;
    private static Control? _confirmButton;
    private static bool _commitInProgress;
    // The map coord the user has highlighted but not yet confirmed.
    // Two-step click flow: 1st click previews, confirm button retries.
    private static MapCoord? _pendingCoord;

    // Exposed so the per-frame _Process patch on NNormalMapPoint can
    // cheaply ask "am I the selected node?" without reaching into the
    // browser internals.
    public static MapCoord? SelectedCoord => _pendingCoord;

    public static void Open(RunHistory history, RunHistoryPlayer player)
    {
        try
        {
            if (Active)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser already active");
                return;
            }
            GD.Print($"{RetryMod.LogPrefix}browser Open: seed={history.Seed}");
            _history = history;
            _player = player;
            _currentPlayerIdx = Math.Max(0, history.Players.FindIndex(p => p.Id == player.Id));

            if (!PrepareRunState()) return;
            PrecomputeActs();
            if (_runState == null) return;

            _runState.ExtraFields.StartedWithNeow = true;
            // Open to the most recent act (where the run ended)
            // instead of always Act 1. AfterRunReady will call
            // ShowAct(initialAct) once the NRun scene is up.
            int initialAct = Math.Max(0, _history.MapPointHistory.Count - 1);
            SetCurrentActIndex(initialAct);
            _runState.Map = _actMaps![initialAct]!;
            ReplaceVisitedCoords(_actVisited![initialAct]);

            RunManager.Instance.Launch();

            // Hide (don't free) the menu so its submenu _Input handlers
            // don't fire (IsVisibleInTree returns false on all descendants),
            // and disable processing as belt-and-suspenders. Returning
            // is then just "Visible=true" — no scene recreation.
            _hiddenMenu = NGame.Instance.RootSceneContainer.CurrentScene;
            if (_hiddenMenu != null)
            {
                // Stop the menu from processing input / tweens while
                // we're up, but DON'T touch its Visible. We instead
                // toggle the active submenu's own Visible below, which
                // drives the natural OnScreenVisibilityChange ->
                // back-button slide-out animation.
                _hiddenMenu.ProcessMode = Node.ProcessModeEnum.Disabled;
                _hiddenMenu.MouseFilter = Control.MouseFilterEnum.Ignore;
                _activeSubmenu = FindActiveSubmenu(_hiddenMenu);
                if (_activeSubmenu != null)
                    _activeSubmenu.Visible = false;
            }

            // Spawn NRun as a direct overlay child of NGame (sibling of
            // RootSceneContainer). Input routes to NMapScreen because
            // our HUD uses MouseFilter=Ignore (set in BuildHud) — Pass
            // would absorb events first; Ignore lets them fall through
            // to NMapScreen while keeping the HUD's own buttons clickable.
            Active = true; // BEFORE AddChild so NRun.Instance patch covers _Ready
            var nrun = NRun.Create(_runState);
            SpawnedNRun = nrun;
            NGame.Instance.AddChildSafely(nrun);
            _overlay = nrun;
            GD.Print($"{RetryMod.LogPrefix}browser: NRun overlay attached, menu hidden");

            // Defer the rest until the _Ready chains finish.
            nrun.GetTree().CreateTimer(0.05).Connect("timeout",
                Callable.From(() => AfterRunReady()));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser Open EXC: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            // Walk inner exceptions in case the throw was wrapped.
            var inner = ex.InnerException;
            while (inner != null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}  inner: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
                inner = inner.InnerException;
            }
            Active = false;
        }
    }

    private static void AfterRunReady()
    {
        try
        {
            int initialAct = _history != null
                ? Math.Max(0, _history.MapPointHistory.Count - 1)
                : 0;
            ShowAct(initialAct);
            // Seed the preview with the run's final state so the
            // top-bar relics / HP / gold reflect where the run ended,
            // not the character's starting kit. The user can then click
            // any earlier node to roll the preview backward.
            ApplyDeathFloorPreview();
            BuildHud();
            HookPortraitForPlayerSwitch();
            TrimTopBar();
            AttachTabsToTopBar();
            ShowGameBackButton();
            // NHoverTipSet adds the map-point tooltips to
            // NGame.HoverTipsContainer (a sibling of our NRun overlay
            // earlier in the children list, so it draws BEHIND us).
            // Move it to the end so its tooltips render above the map.
            try
            {
                var htc = NGame.Instance?.HoverTipsContainer;
                if (htc is Node hn) NGame.Instance!.MoveChild(hn, -1);
            }
            catch (Exception ex)
            { GD.PrintErr($"{RetryMod.LogPrefix}browser hover-z: {ex.Message}"); }
            ((SceneTree)Engine.GetMainLoop()).Root.GuiReleaseFocus();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser AfterRunReady EXC: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Hide TopBar elements that aren't meaningful for a view-only
    // history browser: the Map toggle, the speedrun Timer. Pause stays
    // visible — its click is rerouted to NSettingsScreen via a Harmony
    // patch (NTopBarPauseButton_OnRelease_Patch below) so the user
    // gets the out-of-run Settings UI, not the in-run pause menu.
    // NRunTimer's visibility is reasserted by RefreshVisibility (wired
    // to NMapScreen.VisibilityChanged), so a one-shot Visible=false
    // doesn't stick — that's handled via NRunTimer_ToggleTimer_Patch.
    private static void TrimTopBar()
    {
        try
        {
            var nrun = SpawnedNRun;
            if (nrun == null) return;
            var topBar = nrun.GlobalUi?.TopBar;
            if (topBar == null) return;
            if (topBar.Map != null) topBar.Map.Visible = false;
            if (topBar.Timer != null) topBar.Timer.Visible = false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser TrimTopBar: {ex.Message}");
        }
    }

    // Reparent the act tabs from our HUD onto the TopBar itself so
    // they ride along with TopBar.AnimHide/AnimShow when the Settings
    // capstone opens. Called after BuildHud has created the tabs.
    private static void AttachTabsToTopBar()
    {
        try
        {
            var tabs = _hud?.GetNodeOrNull<HBoxContainer>("RetryTheSpireActTabs");
            var topBar = SpawnedNRun?.GlobalUi?.TopBar;
            if (tabs == null || topBar == null) return;
            tabs.Reparent(topBar);
            // Sit just below the deck/pause cluster at top-right.
            tabs.AnchorLeft = 1; tabs.AnchorRight = 1;
            tabs.AnchorTop = 0; tabs.AnchorBottom = 0;
            tabs.OffsetLeft = -560; tabs.OffsetRight = -360;
            tabs.OffsetTop = 18; tabs.OffsetBottom = 72;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser AttachTabsToTopBar: {ex.Message}");
        }
    }

    // NMapScreen already owns an NBackButton ("Back" child node) — it's
    // moved off-screen by default. Enable it (which tweens it in) and
    // wire its Released signal to our Close.
    private static void ShowGameBackButton()
    {
        try
        {
            var ms = NMapScreen.Instance;
            if (ms == null) return;
            var f = typeof(NMapScreen).GetField("_backButton",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (f?.GetValue(ms) is MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton bb)
            {
                bb.Connect(MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl.SignalName.Released,
                    Callable.From<MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton>(_ => Close()));
                bb.Enable();
                GD.Print($"{RetryMod.LogPrefix}browser: NBackButton wired and enabled");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser ShowGameBackButton: {ex.Message}");
        }
    }

    public static void Close()
    {
        // Always flip Active off first — if any step below hangs or
        // throws, we don't want a stale Active=true blocking future
        // Opens or making the browser think it's still up.
        bool wasActive = Active;
        Active = false;
        long t0 = System.Environment.TickCount64;
        long step;

        try
        {
            if (!wasActive && _overlay == null) return;

            // Per-step timing so if Close hangs (e.g. beachball after
            // a long session on view-acts) we know which call is the
            // culprit. Free the overlay first since its _ExitTree
            // cascade is usually the heaviest piece.
            step = System.Environment.TickCount64;
            try
            {
                if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
                    _overlay.QueueFreeSafely();
            }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}Close overlay free: {ex.Message}"); }
            GD.Print($"{RetryMod.LogPrefix}Close step overlay-free {System.Environment.TickCount64 - step}ms");
            _hud = null;
            _overlay = null;
            SpawnedNRun = null;

            step = System.Environment.TickCount64;
            try { RunManager.Instance.CleanUp(graceful: false); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}Close CleanUp: {ex.Message}"); }
            GD.Print($"{RetryMod.LogPrefix}Close step CleanUp {System.Environment.TickCount64 - step}ms");

            step = System.Environment.TickCount64;
            try
            {
                if (_hiddenMenu != null && GodotObject.IsInstanceValid(_hiddenMenu))
                {
                    _hiddenMenu.ProcessMode = Node.ProcessModeEnum.Inherit;
                    _hiddenMenu.MouseFilter = Control.MouseFilterEnum.Stop;
                    if (_activeSubmenu != null && GodotObject.IsInstanceValid(_activeSubmenu))
                        _activeSubmenu.Visible = true;
                }
            }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}Close menu restore: {ex.Message}"); }
            GD.Print($"{RetryMod.LogPrefix}Close step menu-restore {System.Environment.TickCount64 - step}ms");
            _activeSubmenu = null;
            _hiddenMenu = null;

            // Restore the user's actual local NetId; nuking it would
            // leave subsequent runs / menus with LocalContext.GetMe
            // returning null.
            MegaCrit.Sts2.Core.Context.LocalContext.NetId = _savedLocalNetId;
            _savedLocalNetId = null;
            NetServiceNetIdOverride = null;

            _poppedSubmenus.Clear();
            _history = null;
            _player = null;
            _runState = null;
            _actMaps = null;
            _actVisited = null;
            _commitInProgress = false;
            GD.Print($"{RetryMod.LogPrefix}browser Close: done in {System.Environment.TickCount64 - t0}ms");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser Close EXC: {ex.Message}");
        }
    }

    private static bool PrepareRunState()
    {
        if (_history == null || _player == null) return false;

        // For MP runs we must build the runState with one Player per
        // historical participant — map gen branches on
        // `runState.Players.Count > 1` (StandardActMap.cs:96), so a
        // single-player runState produces an SP map that the historical
        // MP path can never satisfy. SP runs stick with the legacy
        // NetId=1uL convention; MP uses the recorded Steam-style ids.
        bool isMp = _history.Players.Count > 1;
        var unlockState = MegaCrit.Sts2.Core.Unlocks.UnlockState.all;
        var players = new List<MegaCrit.Sts2.Core.Entities.Players.Player>();
        if (isMp)
        {
            foreach (var hp in _history.Players)
            {
                var cm = ModelDb.GetByIdOrNull<CharacterModel>(hp.Character);
                if (cm == null) continue;
                players.Add(MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun(cm, unlockState, hp.Id));
            }
        }
        if (players.Count == 0)
        {
            var fallbackChar = ModelDb.GetByIdOrNull<CharacterModel>(_player.Character);
            if (fallbackChar == null) return false;
            players.Add(MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun(fallbackChar, unlockState, 1uL));
        }
        var actsList = new List<ActModel>();
        foreach (var id in _history.Acts) actsList.Add(ModelDb.GetById<ActModel>(id).ToMutable());
        _runState = RunState.CreateForNewRun(
            players,
            actsList,
            new List<ModifierModel>(),
            _history.GameMode,
            _history.Ascension,
            _history.Seed);

        // Populate _mapPointHistory from the saved history so
        // NMapPoint.OnFocus → GetHistoryEntryFor returns real data
        // and the hover tooltip (NMapPointHistoryHoverTip) displays
        // floor/encounter/reward info on visited nodes.
        var historyField = typeof(RunState).GetField("_mapPointHistory",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (historyField?.GetValue(_runState) is List<List<MapPointHistoryEntry>> mph)
        {
            mph.Clear();
            foreach (var act in _history.MapPointHistory)
            {
                mph.Add(new List<MapPointHistoryEntry>(act));
            }
        }

        // Snapshot the pre-browser LocalContext.NetId so Close can
        // restore it.
        _savedLocalNetId = MegaCrit.Sts2.Core.Context.LocalContext.NetId;

        // For MP runs, override what the SP NetService reports as its
        // NetId. RunManager.InitializeShared and Launch both copy
        // NetService.NetId into LocalContext.NetId, so if we don't
        // override the getter here, LocalContext.GetMe(playerCollection)
        // looks for player with NetId=1 but our MP players have Steam
        // IDs → throws. Patched at NetSingleplayerGameService_NetId_Patch.
        if (_history.Players.Count > 1 && _player != null)
            NetServiceNetIdOverride = _player.Id;
        else
            NetServiceNetIdOverride = null;

        var rm = RunManager.Instance;
        if (!rm.IsInProgress)
        {
            rm.SetUpNewSingleplayer(_runState, shouldSave: false);
        }
        return true;
    }

    // When non-null, NetSingleplayerGameService_NetId_Patch returns
    // this value from NetService.NetId. That makes RunManager.Launch's
    // `LocalContext.NetId = NetService.NetId` resolve to the selected
    // MP player, so the RunStarted event subscribers
    // (NReactionWheel.OnRunStarted, etc) that call
    // LocalContext.GetMe(playerCollection) find the right player
    // instead of throwing "Local player not found".
    //
    // SP keeps this null — the SP fallback player has NetId=1uL
    // already matching the default, no override needed.
    public static ulong? NetServiceNetIdOverride;

    private static void OverrideLocalNetId()
    {
        if (_history != null && _history.Players.Count > 1 && _player != null)
        {
            MegaCrit.Sts2.Core.Context.LocalContext.NetId = _player.Id;
            GD.Print($"{RetryMod.LogPrefix}browser LocalContext.NetId override → {_player.Id}");
        }
    }

    // Pre-browser LocalContext.NetId so we can restore it on Close.
    private static ulong? _savedLocalNetId;

    // Active main-menu submenu (e.g. NRunHistory) at the moment we
    // opened. Toggling its local Visible fires the normal submenu
    // transition animation, including the back-button slide-out/in.
    // Hiding the parent NMainMenu wouldn't — Godot's
    // visibility_changed fires for IsVisibleInTree changes, but
    // OnScreenVisibilityChange tests base.Visible (local), which
    // stayed true, so the wrong branch ran and the back button got
    // stranded at _hidePos.
    private static MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu? _activeSubmenu;

    // Track currently-previewed floor so we can re-apply on player
    // switch without resetting to the death floor every time.
    private static int _previewAct = -1, _previewFloor = -1;

    // Index of selected player into _history.Players (0..N-1).
    private static int _currentPlayerIdx;

    private static void PrecomputeActs()
    {
        if (_history == null || _runState == null) return;
        int n = _history.MapPointHistory.Count;
        _actMaps = new ActMap?[n];
        _actVisited = new List<MapCoord>[n];
        for (int i = 0; i < n; i++)
        {
            try
            {
                var (map, visited) = ReconstructAct(i);
                _actMaps[i] = map;
                _actVisited[i] = visited;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser precompute act {i}: {ex.Message}");
            }
        }
    }

    private static (ActMap? map, List<MapCoord> visited) ReconstructAct(int actIdx)
    {
        if (_history == null || _runState == null) return (null, new());
        if (actIdx >= _history.Acts.Count) return (null, new());
        SetCurrentActIndex(actIdx);
        var act = _runState.Acts[actIdx];
        var map = act.CreateMap(_runState, replaceTreasureWithElites: false);

        var entries = _history.MapPointHistory[actIdx];
        var typeChain = new List<MapPointType>();
        foreach (var e in entries) typeChain.Add(e.MapPointType);

        var exactHints = new Dictionary<int, MapCoord>();
        var legacyHints = new Dictionary<int, MapCoord>();
        for (int floor = 0; floor < entries.Count; floor++)
        {
            var exact = RngSnapshotStore.TryGetExactCoord(
                _history.StartTime, _history.Seed, actIdx, floor);
            if (exact.HasValue) exactHints[floor] = exact.Value;
            var legacy = RngSnapshotStore.TryGetLegacyCoord(_history.Seed, actIdx, floor);
            if (legacy.HasValue) legacyHints[floor] = legacy.Value;
        }

        List<MapPoint>? path = null;
        if (exactHints.Count == entries.Count)
        {
            var coords = Enumerable.Range(0, entries.Count).Select(i => exactHints[i]).ToList();
            if (MapPathSearch.TryBuildCoordinatePath(map, coords, out var coordinatePath, out var failure))
            {
                path = coordinatePath;
                GD.Print($"{RetryMod.LogPrefix}browser act {actIdx}: restored {path.Count} exact v3 coordinates");
            }
            else
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser act {actIdx}: v3 coordinate chain rejected: {failure}");
            }
        }

        path ??= MapPathSearch.FindPath(map, typeChain);
        if (path == null)
        {
            var closest = MapPathSearch.FindClosestPath(map, typeChain, exactHints, legacyHints);
            if (closest != null)
            {
                path = closest.Path;
                string mismatches = string.Join(", ", path
                    .Select((point, floor) => (point, floor))
                    .Where(x => x.floor < typeChain.Count && x.point.PointType != typeChain[x.floor])
                    .Select(x => $"{x.floor}:{typeChain[x.floor]}→{x.point.PointType}"));
                GD.PrintErr(
                    $"{RetryMod.LogPrefix}browser act {actIdx}: exact history path unavailable; " +
                    $"best legal path length={path.Count}/{entries.Count} " +
                    $"typeMismatches={closest.TypeMismatches} exactCoordMatches={closest.ExactCoordinateMatches} " +
                    $"legacyCoordMatches={closest.LegacyCoordinateMatches} complete={closest.Complete}" +
                    (mismatches.Length > 0 ? $" [{mismatches}]" : ""));
            }
        }
        var visited = new List<MapCoord>();
        if (path != null)
            foreach (var n in path) visited.Add(n.coord);
        return (map, visited);
    }

    private static void SetCurrentActIndex(int idx)
    {
        if (_runState == null) return;
        var prop = typeof(RunState).GetProperty(nameof(RunState.CurrentActIndex));
        prop?.SetValue(_runState, idx);
    }

    private static void ReplaceVisitedCoords(IEnumerable<MapCoord> coords)
    {
        if (_runState == null) return;
        var field = typeof(RunState).GetField("_visitedMapCoords",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(_runState) is List<MapCoord> list)
        {
            list.Clear();
            list.AddRange(coords);
        }
    }

    private static void ShowAct(int actIdx)
    {
        if (_history == null || _runState == null) return;
        if (_actMaps == null || actIdx >= _actMaps.Length || _actMaps[actIdx] == null) return;
        _currentAct = actIdx;
        _pendingCoord = null;

        SetCurrentActIndex(actIdx);
        ReplaceVisitedCoords(_actVisited![actIdx]);
        var map = _actMaps[actIdx]!;
        _runState.Map = map;

        var ms = NMapScreen.Instance;
        if (ms == null) { GD.PrintErr($"{RetryMod.LogPrefix}browser ShowAct: NMapScreen.Instance null"); return; }
        try
        {
            // Clear stale _bossPointNode / _secondBossPointNode /
            // _startingPointNode references before SetMap — preventing
            // the disposed-object NRE on repeat tab clicks.
            NullStalePointNodes(ms);

            SetHasPlayedAnimation(ms, true);
            ms.SetMap(map, _runState.Rng.Seed, clearDrawings: true);
            ms.SetDebugTravelEnabled(true);
            ms.SetTravelEnabled(true);

            var visited = _actVisited[actIdx];
            if (visited.Count > 0) ms.InitMarker(visited[visited.Count - 1]);

            RefreshMapBg(ms);
            if (!ms.IsOpen) ms.Open(isOpenedFromTopBar: true);
            ForceRecalculateTravelability(ms);

            // Scroll target: the last visited node's row. For a
            // completed act that's the boss (top of the map); for
            // the death act that's the floor the player died on.
            // Open's else branch only scrolled for the first open
            // (and used CurrentMapCoord which we don't set), so we
            // do this explicitly on every ShowAct.
            if (visited.Count > 0)
                ScrollToRow(ms, visited[visited.Count - 1].row);
            else
                ScrollToRow(ms, 0);

            if (_hud != null) UpdateTabHighlights();
            GD.Print($"{RetryMod.LogPrefix}browser act {actIdx}: SetMap done");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser ShowAct {actIdx} EXC: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void SetHasPlayedAnimation(NMapScreen ms, bool v)
    {
        var f = typeof(NMapScreen).GetField("_hasPlayedAnimation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        f?.SetValue(ms, v);
    }

    // Replace bg textures for the current act. NMapBg.OnVisibilityChanged
    // is the game's only path that does this, and triggering it via
    // a Visible=false/true toggle is timing-fragile (the signal can
    // fire in the wrong order when ms.Open later changes visibility,
    // so the bg sometimes resolved to the previous act). Setting the
    // private TextureRect fields directly is deterministic.
    private static readonly FieldInfo? _mapBgContainerField =
        typeof(NMapScreen).GetField("_mapBgContainer",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static void RefreshMapBg(NMapScreen ms)
    {
        if (_runState == null) return;
        if (_mapBgContainerField?.GetValue(ms) is not Control bg) return;

        var act = _runState.Act;
        if (act == null) return;

        var bgType = bg.GetType();
        var bf = BindingFlags.Instance | BindingFlags.NonPublic;
        var top = bgType.GetField("_mapTop", bf)?.GetValue(bg) as TextureRect;
        var mid = bgType.GetField("_mapMid", bf)?.GetValue(bg) as TextureRect;
        var bot = bgType.GetField("_mapBot", bf)?.GetValue(bg) as TextureRect;
        if (top != null) top.Texture = act.MapTopBg;
        if (mid != null) mid.Texture = act.MapMidBg;
        if (bot != null) bot.Texture = act.MapBotBg;
    }

    // Scroll the map so the most-recent visited node is centered.
    // For completed acts (last visited == boss row), that's the top
    // of the map; for the death act, it's wherever the player died.
    // Uses the same math Open's else branch uses for its initial
    // scroll: position.Y = -600 + row * _distY.
    private static readonly FieldInfo? _mapContainerField =
        typeof(NMapScreen).GetField("_mapContainer",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? _targetDragPosField =
        typeof(NMapScreen).GetField("_targetDragPos",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? _distYField =
        typeof(NMapScreen).GetField("_distY",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static void ScrollToRow(NMapScreen ms, int row)
    {
        try
        {
            if (_distYField?.GetValue(ms) is not float distY) return;
            float y = -600f + row * distY;
            if (_mapContainerField?.GetValue(ms) is Control mc)
                mc.Position = new Vector2(mc.Position.X, y);
            if (_targetDragPosField != null)
                _targetDragPosField.SetValue(ms, new Vector2(0f, y));
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}ScrollToRow: {ex.Message}"); }
    }

    // Null out the special point-node fields before each SetMap so
    // the game's SetMap doesn't access a stale-disposed reference
    // via the field. SetMap queues `_bossPointNode?.QueueFreeSafely()`
    // etc., but the field still points at the disposed instance for
    // a window, and across multiple act-switches that stale ref
    // surfaces as `Cannot access a disposed object (NBossMapPoint)`
    // inside SetMap's focus-neighbor GetPath() block. The actual
    // node children are still cleaned up by `_points.FreeChildren()`
    // since they remain in the tree until that runs.
    private static readonly FieldInfo? _bossPointNodeField =
        typeof(NMapScreen).GetField("_bossPointNode",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? _secondBossPointNodeField =
        typeof(NMapScreen).GetField("_secondBossPointNode",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? _startingPointNodeField =
        typeof(NMapScreen).GetField("_startingPointNode",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static void NullStalePointNodes(NMapScreen ms)
    {
        try
        {
            // Queue free any still-valid stale node first, then null
            // the field. Skipping the QueueFree on already-disposed
            // refs avoids re-disposal exceptions.
            foreach (var f in new[] { _bossPointNodeField, _secondBossPointNodeField, _startingPointNodeField })
            {
                if (f?.GetValue(ms) is Node n && GodotObject.IsInstanceValid(n))
                    n.QueueFreeSafely();
                f?.SetValue(ms, null);
            }
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}NullStalePointNodes: {ex.Message}"); }
    }

    // SetMap's tail does
    //     if (IsVisible()) { RecalculateTravelability(); RefreshAllPointVisuals(); }
    // — but on repeat clicks the screen can transiently be invisible
    // at SetMap time (Open hasn't run yet), so neither call fires and
    // we end up with all map points stuck at the default Untravelable
    // state: no visited-circle markers, no clickable nodes. Calling
    // RecalculateTravelability explicitly afterwards makes the screen
    // state independent of whatever visibility flicker the open path
    // produced.
    private static readonly MethodInfo? _recalcTravelabilityMethod =
        typeof(NMapScreen).GetMethod("RecalculateTravelability",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static void ForceRecalculateTravelability(NMapScreen ms)
    {
        try
        {
            _recalcTravelabilityMethod?.Invoke(ms, null);
            ms.RefreshAllPointVisuals();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}force recalc travel: {ex.Message}");
        }
    }

    private static readonly FieldInfo? MapPointDictField = typeof(NMapScreen)
        .GetField("_mapPointDictionary", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? MapMarkerField = typeof(NMapScreen)
        .GetField("_marker", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? CircleVfxField = typeof(NNormalMapPoint)
        .GetField("_circleVfx", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? OutlineField = typeof(NNormalMapPoint)
        .GetField("_outline", BindingFlags.Instance | BindingFlags.NonPublic);

    // After the user clicks a visited node, tint that node's drawn-on
    // circle gold and reset every other node's circle to the default
    // white (the value NMapCircleVfx assigns itself in _Ready).
    // Per-circle caches: the visited-node circle has its visible
    // texture on a child TextureRect whose Modulate is set in the .tscn
    // to a near-black brown — that's the "dark circle" we see when
    // nothing's selected. To override it we cache the as-loaded value
    // the first time we touch each circle, then restore it whenever
    // the node loses selection.
    private static readonly Dictionary<ulong, Color> _origCircleChildModulate = new();
    private static readonly Dictionary<ulong, Color> _origOutlineModulate = new();

    // Boss / starting / second-boss points don't ship with an
    // NMapCircleVfx the way NNormalMapPoint does, so visiting the
    // boss leaves it ringless. We adopt one onto each non-normal
    // visited point so the highlight ring works there too.

    private static Control? FindCircleVfx(NMapPoint p)
    {
        if (p is NNormalMapPoint && CircleVfxField?.GetValue(p) is Control c) return c;
        foreach (var ch in p.GetChildren())
        {
            if (ch is MegaCrit.Sts2.Core.Nodes.Vfx.NMapCircleVfx vfx) return vfx;
        }
        return null;
    }

    private static void RefreshNodeHighlights()
    {
        try
        {
            var ms = NMapScreen.Instance;
            if (ms == null) return;
            if (MapPointDictField?.GetValue(ms) is not System.Collections.IDictionary dict) return;
            var highlight = new Color(0.78f, 0.58f, 0.13f, 1.0f); // muted gold
            foreach (var v in dict.Values)
            {
                if (v is not NMapPoint p) continue;
                bool isSelected = _pendingCoord is MapCoord sc
                    && p.Point.coord.row == sc.row && p.Point.coord.col == sc.col;
                var circle = FindCircleVfx(p);
                if (circle != null)
                {
                    // The visible sprite lives on the child TextureRect.
                    // Its Modulate (loaded from the .tscn) is a dark
                    // brown — that's the "dark circle" we were drawing
                    // gold on top of. Cache on first encounter so we
                    // can restore it when the node loses selection.
                    foreach (var ch in circle.GetChildren())
                    {
                        if (ch is not CanvasItem ci) continue;
                        ulong id = ci.GetInstanceId();
                        if (!_origCircleChildModulate.ContainsKey(id))
                            _origCircleChildModulate[id] = ci.Modulate;
                        ci.Modulate = isSelected ? highlight : _origCircleChildModulate[id];
                    }
                }
                // _outline is only on NNormalMapPoint.
                if (p is NNormalMapPoint && OutlineField?.GetValue(p) is CanvasItem outline)
                {
                    ulong oid = outline.GetInstanceId();
                    if (!_origOutlineModulate.ContainsKey(oid))
                        _origOutlineModulate[oid] = outline.Modulate;
                    outline.Modulate = isSelected ? highlight : _origOutlineModulate[oid];
                }
            }
            // Slide the position marker (the red arrow) to the selected
            // node. SetMapPoint is a no-op while the marker is already
            // visible, so we first flip Visible off via ResetMapPoint
            // — that path actually re-runs the entry tween at the new
            // node position.
            if (_pendingCoord is MapCoord pc
                && MapMarkerField?.GetValue(ms) is MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapMarker marker)
            {
                try
                {
                    marker.ResetMapPoint(); // sets Visible=false
                    ms.InitMarker(pc);      // SetMapPoint now repositions + re-tweens
                }
                catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}move marker: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}RefreshNodeHighlights: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Lift the actual game NConfirmButton out of the character-select
    // scene. We instantiate the scene without adding to the SceneTree
    // (so its other children's _Ready never fires), find the button by
    // name, reparent it into our HUD (which triggers JUST the button's
    // _Ready), then free the orphan scene. This gives us the real red-
    // banner art + behavior with no other side effects.
    private static Control? ExtractGameConfirmButton()
    {
        // Probe several scenes known to host an NConfirmButton.
        string[] candidates = new[]
        {
            "res://scenes/screens/character_select_screen.tscn",
            "res://scenes/screens/custom_run/custom_run_load_screen.tscn",
            "res://scenes/screens/load_run_lobby.tscn",
        };
        foreach (var path in candidates)
        {
            try
            {
                bool exists = Godot.ResourceLoader.Exists(path);
                GD.Print($"{RetryMod.LogPrefix}ExtractConfirm probe: {path} exists={exists}");
                if (!exists) continue;
                var packed = Godot.ResourceLoader.Load<PackedScene>(path);
                if (packed == null) { GD.Print($"{RetryMod.LogPrefix}ExtractConfirm: load returned null"); continue; }
                var orphan = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
                if (orphan == null) { GD.Print($"{RetryMod.LogPrefix}ExtractConfirm: Instantiate null"); continue; }
                // Dump orphan's immediate children + grandchildren to
                // find what's actually named.
                var names = new System.Text.StringBuilder();
                DumpTree(orphan, names, 0, 3);
                GD.Print($"{RetryMod.LogPrefix}ExtractConfirm tree of {path}:\n{names}");
                if (FindFirstNConfirmButton(orphan) is not Control confirm)
                {
                    GD.Print($"{RetryMod.LogPrefix}ExtractConfirm: no NConfirmButton in {path}");
                    orphan.QueueFreeSafely();
                    continue;
                }
                confirm.GetParent()?.RemoveChild(confirm);
                orphan.QueueFreeSafely();

                confirm.Name = "RetryTheSpireConfirmBtn";
                confirm.Visible = false;
                confirm.Connect("Released",
                    Callable.From<Godot.Node>(_ => CommitRetry()));
                GD.Print($"{RetryMod.LogPrefix}ExtractConfirm: extracted from {path}");
                return confirm;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}ExtractGameConfirmButton {path}: {ex.Message}");
            }
        }
        return null;
    }

    private static Node? FindByName(Node root, string name)
    {
        if (root.Name == name) return root;
        foreach (var ch in root.GetChildren())
        {
            var hit = FindByName(ch, name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static Node? FindFirstNConfirmButton(Node root)
    {
        if (root is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton) return root;
        foreach (var ch in root.GetChildren())
        {
            var hit = FindFirstNConfirmButton(ch);
            if (hit != null) return hit;
        }
        return null;
    }

    private static void DumpTree(Node n, System.Text.StringBuilder sb, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        sb.Append(new string(' ', depth * 2))
          .Append(n.GetType().Name).Append(":").Append(n.Name).Append('\n');
        foreach (var ch in n.GetChildren()) DumpTree(ch, sb, depth + 1, maxDepth);
    }

    private static Button MakeBannerButton(string text, Color bg, Color hoverBg)
    {
        // Cream-on-red banner styled to match the game's UI buttons —
        // beveled corners, heavy outline, drop shadow.
        var btn = new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        btn.AddThemeFontSizeOverride("font_size", 22);
        var normal = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = new Color(0.0f, 0.0f, 0.0f, 0.85f),
            BorderWidthLeft = 3, BorderWidthRight = 3,
            BorderWidthTop = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 18, ContentMarginRight = 18,
            ContentMarginTop = 8, ContentMarginBottom = 8,
            ShadowSize = 8, ShadowColor = new Color(0, 0, 0, 0.65f),
        };
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = hoverBg;
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", hover);
        btn.AddThemeColorOverride("font_color", new Color(1f, 0.97f, 0.83f));
        btn.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f));
        btn.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0));
        btn.AddThemeConstantOverride("outline_size", 4);
        return btn;
    }

    private static void BuildHud()
    {
        try
        {
            if (_overlay == null) return;
            // The HUD is a sibling of NRun (under our overlay) so it
            // floats above the in-game UI and is freed with the overlay.
            _hud = new Control { Name = "RetryTheSpireBrowserHud" };
            _hud.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            // Ignore so empty HUD areas don't intercept scroll/click for
            // NMapScreen underneath; child Buttons still get clicks
            // because Ignore on a parent doesn't block children.
            _hud.MouseFilter = Control.MouseFilterEnum.Ignore;
            _hud.ZIndex = 100;
            _overlay.AddChildSafely(_hud);

            // Game-style act tabs in the top-right of the TopBar
            // (where the timer used to sit). Use cream/gold colors
            // and the game's heavy outline-style font.
            var tabs = new HBoxContainer { Name = "RetryTheSpireActTabs" };
            tabs.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            tabs.OffsetLeft = -460; tabs.OffsetRight = -260;
            tabs.OffsetTop = 18; tabs.OffsetBottom = 72;
            tabs.AddThemeConstantOverride("separation", 6);
            _hud.AddChildSafely(tabs);
            BuildTabs(tabs);

            // Real in-game NConfirmButton — extracted from the
            // character_select_screen scene so we get the actual red
            // banner art, hover scaling, and tween-in animation.
            // Stored as _confirmButton (Control type since NConfirmButton
            // lives in CommonUi and we just need its base Control API).
            _confirmButton = ExtractGameConfirmButton();
            if (_confirmButton != null)
            {
                _hud.AddChildSafely(_confirmButton);
            }
            else
            {
                // Fallback to our own styled button if extraction failed.
                GD.PrintErr($"{RetryMod.LogPrefix}browser: confirm extract failed, using fallback");
                _confirmButton = MakeBannerButton("CONFIRM", new Color(0.78f, 0.16f, 0.10f), new Color(1f, 0.30f, 0.18f));
                _confirmButton.Name = "RetryTheSpireConfirmBtn";
                _confirmButton.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
                _confirmButton.OffsetLeft = -260; _confirmButton.OffsetRight = -30;
                _confirmButton.OffsetTop = -90; _confirmButton.OffsetBottom = -30;
                _confirmButton.CustomMinimumSize = new Vector2(230, 60);
                _confirmButton.Visible = false;
                _confirmButton.Connect(Button.SignalName.Pressed, Callable.From(() => CommitRetry()));
                _hud.AddChildSafely(_confirmButton);
            }

            GD.Print($"{RetryMod.LogPrefix}browser BuildHud: done");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser BuildHud EXC: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void BuildTabs(HBoxContainer container)
    {
        if (_history == null) return;
        int actCount = _history.MapPointHistory.Count;
        _tabButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            int actIdx = i;
            string name = i switch { 0 => "I", 1 => "II", 2 => "III", _ => $"{i+1}" };
            bool reached = actIdx < actCount && _history.MapPointHistory[actIdx].Count > 0;
            // Roman-numeral act buttons in the dark/cream TopBar palette
            // so they read as part of the top bar.
            var btn = MakeBannerButton(name,
                new Color(0.16f, 0.11f, 0.06f), // dark wood
                new Color(0.32f, 0.22f, 0.08f)); // hover lighter
            btn.Disabled = !reached;
            btn.CustomMinimumSize = new Vector2(60, 54);
            btn.AddThemeFontSizeOverride("font_size", 24);
            btn.Connect(Button.SignalName.Pressed, Callable.From(() => ShowAct(actIdx)));
            container.AddChildSafely(btn);
            _tabButtons[i] = btn;
        }
        UpdateTabHighlights();
    }

    private static void UpdateTabHighlights()
    {
        if (_tabButtons == null) return;
        for (int i = 0; i < _tabButtons.Length; i++)
        {
            var btn = _tabButtons[i];
            if (btn == null) continue;
            // Gold highlight on the current act; dimmed on unreached.
            if (i == _currentAct) btn.Modulate = new Color(1f, 0.92f, 0.55f, 1f);
            else if (btn.Disabled) btn.Modulate = new Color(0.4f, 0.4f, 0.4f, 0.55f);
            else btn.Modulate = new Color(1, 1, 1, 1);
        }
    }

    // First click on a visited node: select + preview the floor's
    // saved state (HP, gold). Confirm button commits the retry.
    public static void HandleMapPointClick(MapCoord coord)
    {
        if (_history == null || _player == null) return;
        if (_actVisited == null) return;
        var visited = _actVisited[_currentAct];
        int floor = -1;
        for (int i = 0; i < visited.Count; i++)
        {
            if (visited[i].row == coord.row && visited[i].col == coord.col) { floor = i; break; }
        }
        if (floor < 0)
        {
            GD.Print($"{RetryMod.LogPrefix}browser click rejected: coord {coord.row},{coord.col} not in visited");
            return;
        }
        var actEntries = _history.MapPointHistory[_currentAct];
        if (floor >= actEntries.Count) return;

        _pendingCoord = coord;
        UpdatePreviewForFloor(_currentAct, floor);
        RefreshNodeHighlights();
        if (_confirmButton != null)
        {
            _confirmButton.Visible = true;
            // If this is the real NConfirmButton extracted from the
            // character-select scene, call Enable() so it tweens in
            // from off-screen (its _showPos is bottom-right).
            try
            {
                var enableMethod = _confirmButton.GetType().GetMethod("Enable");
                enableMethod?.Invoke(_confirmButton, null);
            }
            catch { }
        }
        GD.Print($"{RetryMod.LogPrefix}browser preview: act={_currentAct} floor={floor} coord=({coord.row},{coord.col})");
    }

    // Find the currently-displayed submenu (e.g. NRunHistory) by
    // walking the menu's children for an NSubmenu with local Visible
    // = true. The menu only shows one submenu at a time so the first
    // match is the right one.
    private static MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu? FindActiveSubmenu(Node root)
    {
        foreach (var sm in FindAllOfType<MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu>(root))
            if (sm.Visible) return sm;
        return null;
    }

    private static IEnumerable<T> FindAllOfType<T>(Node root) where T : Node
    {
        if (root is T t) yield return t;
        foreach (var c in root.GetChildren())
        {
            if (c is Node n)
                foreach (var hit in FindAllOfType<T>(n))
                    yield return hit;
        }
    }

    // In MP runs (Players.Count > 1), wire the top-bar character
    // portrait's mouse input so a click cycles to the next player.
    // We use gui_input rather than NClickableControl's Released signal
    // because NTopBarPortrait is a bare Control (no signal infra).
    private static void HookPortraitForPlayerSwitch()
    {
        try
        {
            if (_history == null || _history.Players.Count < 2) return;
            var portrait = SpawnedNRun?.GlobalUi?.TopBar?.Portrait;
            if (portrait == null) return;
            portrait.MouseFilter = Control.MouseFilterEnum.Stop;
            portrait.Connect("gui_input", Callable.From<InputEvent>(OnPortraitGuiInput));
            GD.Print($"{RetryMod.LogPrefix}browser portrait click hook installed (MP run, {_history.Players.Count} players)");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser portrait hook: {ex.Message}");
        }
    }

    private static void OnPortraitGuiInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            CycleToNextPlayer();
        }
    }

    private static void CycleToNextPlayer()
    {
        try
        {
            if (_history == null || _history.Players.Count < 2) return;
            int next = (_currentPlayerIdx + 1) % _history.Players.Count;
            SwitchToPlayer(next);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser cycle player: {ex.Message}");
        }
    }

    private static void SwitchToPlayer(int nextIdx)
    {
        if (_history == null || _runState == null) return;
        if (nextIdx < 0 || nextIdx >= _history.Players.Count) return;
        _currentPlayerIdx = nextIdx;
        _player = _history.Players[nextIdx];
        MegaCrit.Sts2.Core.Context.LocalContext.NetId = _player.Id;

        // Swap the character icon in the top-bar portrait. Initialize
        // adds the icon as a child, so clear existing children first
        // to avoid stacking portraits on repeated switches.
        try
        {
            var portrait = SpawnedNRun?.GlobalUi?.TopBar?.Portrait;
            var live = _runState.Players.FirstOrDefault(p => p.NetId == _player.Id);
            if (portrait != null && live != null)
            {
                foreach (var child in portrait.GetChildren())
                    if (GodotObject.IsInstanceValid(child)) child.QueueFreeSafely();
                portrait.Initialize(live);
            }
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}portrait switch: {ex.Message}"); }

        // Re-apply the floor preview for the new player — it handles
        // Hp/Gold/Deck/Inventory/Potion refresh and the room icon.
        if (_previewAct >= 0 && _previewFloor >= 0)
            UpdatePreviewForFloor(_previewAct, _previewFloor);
        else
            ApplyDeathFloorPreview();

        GD.Print($"{RetryMod.LogPrefix}browser switched to player {nextIdx} ({_player.Character} id={_player.Id})");
    }

    // Clear-then-Initialize NRelicInventory + NPotionContainer so
    // they reflect the live player's current Relics/Potions lists.
    // Required after InventoryInjector.Apply(silent: true) because
    // the inventory widgets normally rebuild themselves via the
    // RelicObtained / PotionProcured player events — which silent
    // apply suppressed. Initialize itself only appends (it doesn't
    // clear), so we manually flush existing holders first.
    private static void RefreshInventoryHud()
    {
        try
        {
            var inv = SpawnedNRun?.GlobalUi?.RelicInventory;
            if (inv != null && _runState != null)
            {
                foreach (var c in inv.GetChildren())
                    if (GodotObject.IsInstanceValid(c)) c.QueueFreeSafely();
                var nodesField = typeof(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory).GetField(
                    "_relicNodes", BindingFlags.NonPublic | BindingFlags.Instance);
                if (nodesField?.GetValue(inv) is System.Collections.IList list) list.Clear();
                inv.Initialize(_runState);
            }
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}relic hud refresh: {ex.Message}"); }

        try
        {
            var pots = SpawnedNRun?.GlobalUi?.TopBar?.PotionContainer;
            if (pots != null && _runState != null)
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
                if (ptype.GetField("_potionHolders", bf)?.GetValue(pots)
                    is Control holdersParent)
                {
                    foreach (var ch in holdersParent.GetChildren())
                        if (GodotObject.IsInstanceValid(ch)) ch.QueueFreeSafely();
                }
                if (ptype.GetField("_holders", bf)?.GetValue(pots)
                    is System.Collections.IList holderList)
                {
                    holderList.Clear();
                }
                pots.Initialize(_runState);
            }
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}potion hud refresh: {ex.Message}"); }
    }

    // Apply the historical run's final-floor state on first browser
    // open — last floor of the last act in MapPointHistory. Falls back
    // to no-op if history is empty.
    private static void ApplyDeathFloorPreview()
    {
        try
        {
            if (_history == null) return;
            int lastAct = _history.MapPointHistory.Count - 1;
            if (lastAct < 0) return;
            int lastFloor = _history.MapPointHistory[lastAct].Count - 1;
            if (lastFloor < 0) return;
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
    private static void UpdatePreviewForFloor(int actIdx, int floor)
    {
        try
        {
            if (_history == null || _player == null || _runState == null) return;
            if (actIdx >= _history.MapPointHistory.Count) return;
            var entries = _history.MapPointHistory[actIdx];
            if (floor >= entries.Count) return;

            // Reconstruct every historical participant at this node, not
            // just the portrait currently selected in the top bar. The
            // multiplayer player strip reads each live Player directly;
            // leaving the others untouched made it show their starter
            // decks/relics until each portrait had been selected once.
            PlayerStateSnapshot? snapshot = null;
            foreach (var historicalPlayer in _history.Players)
            {
                var playerSnapshot = StateReconstructor.ReconstructAtTarget(
                    _history, actIdx, floor, historicalPlayer);
                // silent=true: the browser is a preview, not a live run.
                // Avoid replaying every card/relic acquisition animation;
                // the visible HUD widgets are refreshed explicitly below.
                InventoryInjector.Apply(_runState, playerSnapshot, silent: true);
                if (historicalPlayer.Id == _player.Id)
                    snapshot = playerSnapshot;
            }

            if (snapshot == null)
                throw new InvalidOperationException(
                    $"selected historical player {_player.Id} was not reconstructed");

            // Force-re-init the topbar scalar widgets (HP/Gold/Deck-count
            // badge). These read directly from player rather than
            // subscribing to scalar-change events, so Initialize is the
            // cleanest way to refresh them.
            var topBar = SpawnedNRun?.GlobalUi?.TopBar;
            var p = _runState.Players.FirstOrDefault(x => x.NetId == _player.Id)
                ?? _runState.Players.FirstOrDefault();
            if (topBar != null && p != null)
            {
                try { topBar.Hp?.Initialize(p); } catch { }
                try { topBar.Gold?.Initialize(p); } catch { }
                try { topBar.Deck?.Initialize(p); } catch { }
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
            try { UpdateRoomIconForFloor(actIdx, floor); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}browser room-icon refresh: {ex.Message}"); }
            // Silent apply skipped the RelicObtained / PotionProcured
            // events the UI listens to, so re-Initialize the inventory
            // widgets after clearing their existing child holders to
            // pick up the new state without duplicates.
            RefreshInventoryHud();

            _previewAct = actIdx;
            _previewFloor = floor;
            GD.Print($"{RetryMod.LogPrefix}browser preview state: hp={snapshot.CurrentHp}/{snapshot.MaxHp} gold={snapshot.Gold} deck={snapshot.Deck.Count} relics={snapshot.Relics.Count} potions={snapshot.Potions.Count}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser preview EXC: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Force-paint the top-bar's room-type icon (the "next room" widget
    // top-left of HP) using ImageHelper.GetRoomIconPath. Required
    // because NTopBarRoomIcon.UpdateIcon early-outs when CurrentRoom
    // is null — true for the entire browser session — leaving the red
    // missing-texture placeholder. We set the inner TextureRects
    // directly via reflection.
    private static System.Reflection.FieldInfo? _roomIconField;
    private static System.Reflection.FieldInfo? _roomIconOutlineField;

    private static void UpdateRoomIconForFloor(int actIdx, int floor)
    {
        if (_history == null || _runState == null) return;
        if (actIdx >= _history.MapPointHistory.Count) return;
        var entries = _history.MapPointHistory[actIdx];
        if (floor < 0 || floor >= entries.Count) return;

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

        if (modelId == null && actIdx < _runState.Acts.Count)
        {
            var act = _runState.Acts[actIdx];
            if (pointType == MapPointType.Boss) modelId = act.BossEncounter?.Id;
            else if (pointType == MapPointType.Ancient) modelId = act.Ancient?.Id;
        }

        var topBar = SpawnedNRun?.GlobalUi?.TopBar;
        if (topBar?.RoomIcon == null) return;

        _roomIconField ??= typeof(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarRoomIcon).GetField(
            "_roomIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        _roomIconOutlineField ??= typeof(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarRoomIcon).GetField(
            "_roomIconOutline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        var iconPath = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetRoomIconPath(pointType, roomType, modelId);
        var outlinePath = MegaCrit.Sts2.Core.Helpers.ImageHelper.GetRoomIconOutlinePath(pointType, roomType, modelId);

        var iconRect = _roomIconField?.GetValue(topBar.RoomIcon) as TextureRect;
        var outlineRect = _roomIconOutlineField?.GetValue(topBar.RoomIcon) as TextureRect;
        if (iconRect != null)
        {
            if (iconPath != null)
            {
                iconRect.Visible = true;
                iconRect.Texture = MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetCompressedTexture2D(iconPath);
            }
            else { iconRect.Visible = false; }
        }
        if (outlineRect != null)
        {
            if (outlinePath != null)
            {
                outlineRect.Visible = true;
                outlineRect.Texture = MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetCompressedTexture2D(outlinePath);
            }
            else { outlineRect.Visible = false; }
        }
    }

    private static void CommitRetry()
    {
        if (_commitInProgress) return;
        if (_pendingCoord is not MapCoord coord) return;
        if (_history == null || _player == null || _actVisited == null) return;
        var visited = _actVisited[_currentAct];
        int floor = -1;
        for (int i = 0; i < visited.Count; i++)
        {
            if (visited[i].row == coord.row && visited[i].col == coord.col) { floor = i; break; }
        }
        if (floor < 0) return;

        // Capture before any tear-down so the closure has stable args.
        var history = _history;
        var player = _player;
        var act = _currentAct;
        bool isMultiplayer = history.Players.Count > 1;

        List<MapCoord>? multiplayerPath = null;
        if (isMultiplayer)
        {
            var fullPath = _actVisited != null && act < _actVisited.Length
                ? _actVisited[act]
                : null;
            if (fullPath == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}multiplayer host: reconstructed path unavailable");
                return;
            }
            int targetPathIndex = fullPath.FindIndex(c => c.row == coord.row && c.col == coord.col);
            if (targetPathIndex < 0)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}multiplayer host: selected coord is not on the historical path");
                return;
            }
            multiplayerPath = fullPath.Take(targetPathIndex + 1).ToList();
        }

        // Lock before any modal or launch work. Extracted NConfirmButton
        // instances can emit Released more than once for a single click.
        _commitInProgress = true;
        GD.Print($"{RetryMod.LogPrefix}browser CONFIRM → retry mode={(isMultiplayer ? "multiplayer" : "singleplayer")} act={act} floor={floor} coord=({coord.row},{coord.col})");

        NMainMenu? mainMenu = FindMainMenu(_hiddenMenu)
            ?? FindMainMenu(NGame.Instance?.RootSceneContainer?.CurrentScene);
        ulong multiplayerHostNetId = isMultiplayer ? (_savedLocalNetId ?? player.Id) : 0;

        void DoRetry(bool browserAlreadyClosed = false)
        {
            if (isMultiplayer)
            {
                if (multiplayerPath == null) return;
                MultiplayerRetryLauncher.Begin(
                    history, player, act, floor, coord,
                    multiplayerPath, multiplayerHostNetId,
                    mainMenu, browserAlreadyClosed);
                return;
            }

            // Full tear-down via the standard Close path: frees the
            // NRun overlay (which kills our HUD, restores menu, etc.)
            // and lets RetryRunner.Begin transition cleanly.
            Close();
            RetryRunner.Begin(history, player, act, floor, targetCoord: coord);
        }

        // Modal-protect only when there's an actual on-disk save the
        // retry would clobber. IsInProgress can't be used here because
        // the browser itself installs a preview runState via
        // PrepareRunState's SetUpNewSingleplayer — that makes
        // RunManager.IsInProgress=true even though no "real" run is
        // happening, and would unconditionally pop the modal.
        // Use the save flag for the slot this retry will actually use;
        // the single-player and multiplayer slots are independent.
        // The game intentionally keeps independent active-run slots:
        // current_run.save (single-player) and current_run_mp.save
        // (multiplayer). A multiplayer retry must not prompt for, load,
        // or delete the single-player slot.
        bool hasSave = isMultiplayer
            ? MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.HasMultiplayerRunSave == true
            : MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.HasRunSave == true;
        if (hasSave)
        {
            // NModalContainer.Instance resolves to the preview NRun's modal
            // layer while View Acts is open. Popups added there render behind
            // the browser HUD. Capture launch data above, close the preview,
            // then wait for its queued free before resolving the menu modal.
            if (mainMenu == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser save confirmation failed: main menu unavailable mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
                _commitInProgress = false;
                return;
            }

            var tree = mainMenu.GetTree();
            GD.Print($"{RetryMod.LogPrefix}browser save confirmation: closing preview mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
            Close();
            tree.CreateTimer(0.05).Connect("timeout", Callable.From(() =>
            {
                string title = isMultiplayer
                    ? "Abandon saved multiplayer run?"
                    : "Abandon saved run?";
                string body = isMultiplayer
                    ? "Starting this retry will overwrite your existing multiplayer saved run. Your single-player save is not affected."
                    : "Starting this retry will overwrite your existing saved run.";

                GD.Print($"{RetryMod.LogPrefix}browser save confirmation: showing mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
                RetryAbandonModal.Show(
                    title: title,
                    body: body,
                    onChoice: c =>
                    {
                        if (c == RetryAbandonModal.Choice.Cancel)
                        {
                            GD.Print($"{RetryMod.LogPrefix}browser retry cancelled at save confirmation mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
                            return;
                        }

                        bool writeHistory = c == RetryAbandonModal.Choice.AbandonSave;
                        if (isMultiplayer)
                        {
                            RetryRunner.PerformMultiplayerAbandon(
                                writeHistory: writeHistory,
                                localPlayerId: multiplayerHostNetId);
                        }
                        else
                        {
                            RetryRunner.PerformAbandon(
                                writeHistory: writeHistory,
                                inProgress: false);
                        }

                        GD.Print($"{RetryMod.LogPrefix}browser save confirmation accepted mode={(isMultiplayer ? "multiplayer" : "singleplayer")} writeHistory={writeHistory}");
                        DoRetry(browserAlreadyClosed: true);
                    });
            }));
            return;
        }
        else
        {
            GD.Print($"{RetryMod.LogPrefix}browser retry starting without save confirmation mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
            DoRetry();
        }
    }

    internal static void NotifyMultiplayerLaunchFailed()
    {
        _commitInProgress = false;
        if (_confirmButton != null && GodotObject.IsInstanceValid(_confirmButton))
            _confirmButton.MouseFilter = Control.MouseFilterEnum.Stop;
    }

    private static NMainMenu? FindMainMenu(Node? root)
    {
        if (root == null) return null;
        if (root is NMainMenu menu) return menu;
        foreach (Node child in root.GetChildren())
        {
            var found = FindMainMenu(child);
            if (found != null) return found;
        }
        return null;
    }
}

// While our browser is active, NRun.Instance must return the spawned
// overlay NRun (not the null CurrentRunNode). NMapScreen.Instance and
// most other in-run helpers route through NRun.Instance, so this one
// patch fixes their lookups too.
[HarmonyPatch(typeof(NRun), "get_Instance")]
public static class NRun_Instance_Patch
{
    static bool Prefix(ref NRun? __result)
    {
        if (NActMapBrowser.Active && NActMapBrowser.SpawnedNRun is { } nr)
        {
            __result = nr;
            return false;
        }
        return true;
    }
}

// During an MP browser session, return the selected historical
// player's id from NetSingleplayerGameService.NetId so
// RunManager.Launch's `LocalContext.NetId = NetService.NetId`
// resolves to a player that actually exists in the runState's player
// collection. Without this, RunStarted subscribers (NReactionWheel,
// etc) calling LocalContext.GetMe(playerCollection) throw "Local
// player not found in player collection." SP browser sessions keep
// the default 1uL.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.NetSingleplayerGameService), "get_NetId")]
public static class NetSingleplayerGameService_NetId_Patch
{
    static bool Prefix(ref ulong __result)
    {
        if (NActMapBrowser.NetServiceNetIdOverride is { } id)
        {
            __result = id;
            return false;
        }
        return true;
    }
}

// Force NRelicInventory.Add to use startsShown=true while the
// browser is active. Without this, InventoryInjector.AddRelicInternal
// fires the player's RelicObtained event → NRelicInventory.OnRelicObtained
// → Add(startsShown=false), which sets the icon's modulate alpha to
// zero and waits for a corresponding AnimateRelic call. That fade-in
// only happens during the live treasure / combat reward flows, so for
// our preview swap the relics stay invisible. The browser is purely
// visual, so the "fade in on grab" animation isn't meaningful — we
// just want every relic shown immediately.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory), "Add")]
public static class NRelicInventory_Add_Patch
{
    static void Prefix(ref bool startsShown)
    {
        if (NActMapBrowser.Active) startsShown = true;
    }
}

// Intercept node clicks while the browser is active.
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.OnMapPointSelectedLocally))]
public static class NMapScreen_OnMapPointSelectedLocally_Patch
{
    static bool Prefix(NMapPoint point)
    {
        if (NActMapBrowser.Active)
        {
            NActMapBrowser.HandleMapPointClick(point.Point.coord);
            return false;
        }
        return true;
    }
}

// If the user goes via the in-run pause menu's Save & Quit (which calls
// NGame.ReturnToMainMenu), make sure we tear down our overlay first so
// the menu can land cleanly on NMainMenu.
[HarmonyPatch(typeof(NGame), nameof(NGame.ReturnToMainMenu))]
public static class NGame_ReturnToMainMenu_Patch
{
    static void Prefix()
    {
        if (NActMapBrowser.Active) NActMapBrowser.Close();
    }
}

// Force-hide the speedrun timer while the browser is up. NRunTimer's
// RefreshVisibility fires on NMapScreen.VisibilityChanged and re-shows
// the timer if PrefsSave.ShowRunTimer is on — so a one-shot
// topBar.Timer.Visible = false from TrimTopBar gets clobbered.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.TopBar.NRunTimer), "ToggleTimer")]
public static class NRunTimer_ToggleTimer_Patch
{
    static bool Prefix(MegaCrit.Sts2.Core.Nodes.TopBar.NRunTimer __instance, ref bool on)
    {
        if (NActMapBrowser.Active)
        {
            on = false; // force-hidden while browsing
        }
        return true;
    }
}

// When browser is active, route the TopBar Pause button to the
// out-of-run Settings screen instead of NPauseMenu (the in-run
// version exposes Save & Quit / Give Up which are nonsensical here).
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarPauseButton), "OnRelease")]
public static class NTopBarPauseButton_OnRelease_Patch
{
    static bool Prefix(MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarPauseButton __instance)
    {
        if (!NActMapBrowser.Active) return true;
        try
        {
            var stack = NRun.Instance?.GlobalUi?.SubmenuStack;
            stack?.ShowScreen(MegaCrit.Sts2.Core.Nodes.Screens.CapstoneSubmenuType.Settings);
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}pause→settings: {ex.Message}"); }
        return false;
    }
}

// While the browser is up, NNormalMapPoint._Process drives a sinusoidal
// "I'm selectable" pulse on every Travelable node (anything visited,
// because we turn on debug travel). For preview UX we want only the
// node the user has clicked to pulse — everyone else should sit still.
[HarmonyPatch]
public static class NNormalMapPoint_Process_Patch
{
    private static readonly FieldInfo? IconContainerField =
        typeof(NNormalMapPoint).GetField("_iconContainer",
            BindingFlags.Instance | BindingFlags.NonPublic);

    static MethodBase TargetMethod()
        => AccessTools.Method(typeof(NNormalMapPoint), "_Process", new[] { typeof(double) });

    static bool Prefix(NNormalMapPoint __instance)
    {
        if (!NActMapBrowser.Active) return true;
        var sel = NActMapBrowser.SelectedCoord;
        bool isSelected = sel is MapCoord sc
            && __instance.Point.coord.row == sc.row
            && __instance.Point.coord.col == sc.col;
        if (isSelected) return true; // run original pulse logic
        // Force the icon back to its rest scale and skip _Process so the
        // sin-wave from the original doesn't run.
        if (IconContainerField?.GetValue(__instance) is Control ic) ic.Scale = Vector2.One;
        return false;
    }
}

// Original NPotionHolder.DiscardPotion runs a 0.4s slide-up tween,
// which during preview swaps leaves the outgoing potions sliding away
// while the new potions have already snapped into their slots. Replace
// with a quick fade-out so the cross-fade reads as a single swap.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder), "DiscardPotion")]
public static class NPotionHolder_DiscardPotion_Patch
{
    private static readonly System.Reflection.MethodInfo? PotionSetter =
        AccessTools.PropertySetter(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder), "Potion");
    private static readonly FieldInfo? EmptyIconField =
        typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder)
            .GetField("_emptyIcon", BindingFlags.Instance | BindingFlags.NonPublic);
    // Park the empty-icon fade-in tween in the holder's own
    // _emptyPotionTween slot. That's the field the original AddPotion
    // calls `?.Kill()` on, so when a fresh potion lands in this slot
    // a beat later, the lingering fade is cancelled — otherwise it
    // keeps lerping the placeholder back to White and we see both the
    // empty icon and the new potion stacked.
    private static readonly FieldInfo? EmptyPotionTweenField =
        typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder)
            .GetField("_emptyPotionTween", BindingFlags.Instance | BindingFlags.NonPublic);

    static bool Prefix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder __instance)
    {
        if (!NActMapBrowser.Active) return true;
        try
        {
            var potion = __instance.Potion;
            if (potion == null) return true;
            PotionSetter?.Invoke(__instance, new object?[] { null });

            // Tween 1: fade the outgoing potion to transparent + remove.
            var potionTween = __instance.CreateTween();
            potionTween.TweenProperty(potion, "modulate", Colors.Transparent, 0.12);
            potionTween.TweenCallback(Callable.From(() =>
            {
                try { __instance.RemoveChildSafely(potion); potion.QueueFreeSafely(); }
                catch { }
            }));

            // Tween 2: cross-fade the empty placeholder in. Stored in
            // _emptyPotionTween so AddPotion's Kill() takes care of it.
            if (EmptyIconField?.GetValue(__instance) is TextureRect emptyIcon)
            {
                if (EmptyPotionTweenField?.GetValue(__instance) is Tween prior)
                {
                    try { prior.Kill(); } catch { }
                }
                var emptyTween = __instance.CreateTween();
                emptyTween.TweenProperty(emptyIcon, "modulate", Colors.White, 0.15);
                EmptyPotionTweenField?.SetValue(__instance, emptyTween);
            }
            return false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}potion fast-discard: {ex.Message}");
            return true;
        }
    }
}

// Pair: short fade-in on AddPotion so swapped-in potions don't pop
// instantly while the outgoing ones are still fading out.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder), "AddPotion")]
public static class NPotionHolder_AddPotion_Patch
{
    static void Postfix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder __instance)
    {
        if (!NActMapBrowser.Active) return;
        try
        {
            var p = __instance.Potion;
            if (p == null) return;
            p.Modulate = Colors.Transparent;
            var tween = __instance.CreateTween();
            tween.TweenProperty(p, "modulate", Colors.White, 0.15);
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}potion fade-in: {ex.Message}"); }
    }
}
