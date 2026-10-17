using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

// Owns the preview lifetime and mutable session state.
internal sealed class BrowserSession
{
    internal BrowserMaps Maps { get; }
    internal BrowserView View { get; }
    internal BrowserPreview Preview { get; }
    internal BrowserCommit Commit { get; }

    internal BrowserSession()
    {
        Maps = new BrowserMaps(this);
        View = new BrowserView(this);
        Preview = new BrowserPreview(this);
        Commit = new BrowserCommit(this);
    }

    internal event Action? ReadyForAutomation;
    internal bool Active { get; set; }
    internal NRun? SpawnedNRun { get; set; }

    internal RunHistory? _history;
    internal RunHistoryPlayer? _player;
    internal RunState? _runState;
    internal int _currentAct;
    internal ActMap? []? _actMaps;
    internal List<MapCoord>[]? _actVisited;
    internal Control? _overlay; // the spawned NRun itself
    internal Control? _hud; // floating overlay above NRun for tabs/confirm
    internal Control? _hiddenMenu;
    internal readonly List<Type> _poppedSubmenus = new();
    internal Button[]? _tabButtons;
    internal Control? _confirmButton;
    internal bool _commitInProgress;
    // The map coord the user has highlighted but not yet confirmed.
    // Two-step click flow: 1st click previews, confirm button retries.
    internal MapCoord? _pendingCoord;
    // Exposed so the per-frame _Process patch on NNormalMapPoint can
    // cheaply ask "am I the selected node?" without reaching into the
    // browser internals.
    internal MapCoord? SelectedCoord => _pendingCoord;

    internal void Open(RunHistory history, RunHistoryPlayer player)
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
            if (!Maps.PrepareRunState())
                return;
            Maps.PrecomputeActs();
            if (_runState == null)
                return;
            _runState.ExtraFields.StartedWithNeow = true;
            // Open to the most recent act (where the run ended)
            // instead of always Act 1. AfterRunReady will call
            // ShowAct(initialAct) once the NRun scene is up.
            int initialAct = Math.Max(0, _history.MapPointHistory.Count - 1);
            Maps.SetCurrentActIndex(initialAct);
            _runState.Map = _actMaps![initialAct]!;
            Maps.ReplaceVisitedCoords(_actVisited![initialAct]);
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
                _activeSubmenu = View.FindActiveSubmenu(_hiddenMenu);
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
            nrun.GetTree().CreateTimer(0.05).Connect("timeout", Callable.From(() => AfterRunReady()));
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

    internal void AfterRunReady()
    {
        try
        {
            int initialAct = _history != null ? Math.Max(0, _history.MapPointHistory.Count - 1) : 0;
            View.ShowAct(initialAct);
            // Seed the preview with the run's final state so the
            // top-bar relics / HP / gold reflect where the run ended,
            // not the character's starting kit. The user can then click
            // any earlier node to roll the preview backward.
            Preview.ApplyDeathFloorPreview();
            View.BuildHud();
            Preview.HookPortraitForPlayerSwitch();
            View.TrimTopBar();
            View.AttachTabsToTopBar();
            View.ShowGameBackButton();
            // NHoverTipSet adds the map-point tooltips to
            // NGame.HoverTipsContainer (a sibling of our NRun overlay
            // earlier in the children list, so it draws BEHIND us).
            // Move it to the end so its tooltips render above the map.
            try
            {
                var htc = NGame.Instance?.HoverTipsContainer;
                if (htc is Node hn)
                    NGame.Instance!.MoveChild(hn, -1);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser hover-z: {ex.Message}");
            }

            ((SceneTree)Engine.GetMainLoop()).Root.GuiReleaseFocus();
            try
            {
                ReadyForAutomation?.Invoke();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser automation ready: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser AfterRunReady EXC: {ex.Message}\n{ex.StackTrace}");
        }
    }

    internal void Close()
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
            if (!wasActive && _overlay == null)
                return;
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
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Close overlay free: {ex.Message}");
            }

            GD.Print($"{RetryMod.LogPrefix}Close step overlay-free {System.Environment.TickCount64 - step}ms");
            _hud = null;
            _overlay = null;
            SpawnedNRun = null;
            step = System.Environment.TickCount64;
            try
            {
                RunManager.Instance.CleanUp(graceful: false);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Close CleanUp: {ex.Message}");
            }

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
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Close menu restore: {ex.Message}");
            }

            GD.Print($"{RetryMod.LogPrefix}Close step menu-restore {System.Environment.TickCount64 - step}ms");
            _activeSubmenu = null;
            _hiddenMenu = null;
            // Restore the user's actual local NetId; nuking it would
            // leave subsequent runs / menus with LocalContext.GetMe
            // returning null.
            MegaCrit.Sts2.Core.Context.LocalContext.NetId = _savedLocalNetId;
            _savedLocalNetId = null;
            NActMapBrowser.NetServiceNetIdOverride = null;
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

    // Pre-browser LocalContext.NetId so we can restore it on Close.
    internal ulong? _savedLocalNetId;
    // Active main-menu submenu (e.g. NRunHistory) at the moment we
    // opened. Toggling its local Visible fires the normal submenu
    // transition animation, including the back-button slide-out/in.
    // Hiding the parent NMainMenu wouldn't — Godot's
    // visibility_changed fires for IsVisibleInTree changes, but
    // OnScreenVisibilityChange tests base.Visible (local), which
    // stayed true, so the wrong branch ran and the back button got
    // stranded at _hidePos.
    internal MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu? _activeSubmenu;
    // Track currently-previewed floor so we can re-apply on player
    // switch without resetting to the death floor every time.
    internal int _previewAct = -1, _previewFloor = -1;
    // Index of selected player into _history.Players (0..N-1).
    internal int _currentPlayerIdx;
    // Replace bg textures for the current act. NMapBg.OnVisibilityChanged
    // is the game's only path that does this, and triggering it via
    // a Visible=false/true toggle is timing-fragile (the signal can
    // fire in the wrong order when ms.Open later changes visibility,
    // so the bg sometimes resolved to the previous act). Setting the
    // private TextureRect fields directly is deterministic.
    internal readonly FieldInfo? _mapBgContainerField = typeof(NMapScreen).GetField("_mapBgContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    // Scroll the map so the most-recent visited node is centered.
    // For completed acts (last visited == boss row), that's the top
    // of the map; for the death act, it's wherever the player died.
    // Uses the same math Open's else branch uses for its initial
    // scroll: position.Y = -600 + row * _distY.
    internal readonly FieldInfo? _mapContainerField = typeof(NMapScreen).GetField("_mapContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    internal readonly FieldInfo? _targetDragPosField = typeof(NMapScreen).GetField("_targetDragPos", BindingFlags.Instance | BindingFlags.NonPublic);
    internal readonly FieldInfo? _distYField = typeof(NMapScreen).GetField("_distY", BindingFlags.Instance | BindingFlags.NonPublic);
    // Null out the special point-node fields before each SetMap so
    // the game's SetMap doesn't access a stale-disposed reference
    // via the field. SetMap queues `_bossPointNode?.QueueFreeSafely()`
    // etc., but the field still points at the disposed instance for
    // a window, and across multiple act-switches that stale ref
    // surfaces as `Cannot access a disposed object (NBossMapPoint)`
    // inside SetMap's focus-neighbor GetPath() block. The actual
    // node children are still cleaned up by `_points.FreeChildren()`
    // since they remain in the tree until that runs.
    internal readonly FieldInfo? _bossPointNodeField = typeof(NMapScreen).GetField("_bossPointNode", BindingFlags.Instance | BindingFlags.NonPublic);
    internal readonly FieldInfo? _secondBossPointNodeField = typeof(NMapScreen).GetField("_secondBossPointNode", BindingFlags.Instance | BindingFlags.NonPublic);
    internal readonly FieldInfo? _startingPointNodeField = typeof(NMapScreen).GetField("_startingPointNode", BindingFlags.Instance | BindingFlags.NonPublic);
    // SetMap's tail does
    //     if (IsVisible()) { RecalculateTravelability(); RefreshAllPointVisuals(); }
    // — but on repeat clicks the screen can transiently be invisible
    // at SetMap time (Open hasn't run yet), so neither call fires and
    // we end up with all map points stuck at the default Untravelable
    // state: no visited-circle markers, no clickable nodes. Calling
    // RecalculateTravelability explicitly afterwards makes the screen
    // state independent of whatever visibility flicker the open path
    // produced.
    internal readonly MethodInfo? _recalcTravelabilityMethod = typeof(NMapScreen).GetMethod("RecalculateTravelability", BindingFlags.Instance | BindingFlags.NonPublic);
    internal readonly FieldInfo? MapPointDictField = typeof(NMapScreen).GetField("_mapPointDictionary", BindingFlags.Instance | BindingFlags.NonPublic);
    internal readonly FieldInfo? MapMarkerField = typeof(NMapScreen).GetField("_marker", BindingFlags.Instance | BindingFlags.NonPublic);
    internal readonly FieldInfo? CircleVfxField = typeof(NNormalMapPoint).GetField("_circleVfx", BindingFlags.Instance | BindingFlags.NonPublic);
    internal readonly FieldInfo? OutlineField = typeof(NNormalMapPoint).GetField("_outline", BindingFlags.Instance | BindingFlags.NonPublic);
    // After the user clicks a visited node, tint that node's drawn-on
    // circle gold and reset every other node's circle to the default
    // white (the value NMapCircleVfx assigns itself in _Ready).
    // Per-circle caches: the visited-node circle has its visible
    // texture on a child TextureRect whose Modulate is set in the .tscn
    // to a near-black brown — that's the "dark circle" we see when
    // nothing's selected. To override it we cache the as-loaded value
    // the first time we touch each circle, then restore it whenever
    // the node loses selection.
    internal readonly Dictionary<ulong, Color> _origCircleChildModulate = new();
    internal readonly Dictionary<ulong, Color> _origOutlineModulate = new();
    // Force-paint the top-bar's room-type icon (the "next room" widget
    // top-left of HP) using ImageHelper.GetRoomIconPath. Required
    // because NTopBarRoomIcon.UpdateIcon early-outs when CurrentRoom
    // is null — true for the entire browser session — leaving the red
    // missing-texture placeholder. We set the inner TextureRects
    // directly via reflection.
    internal System.Reflection.FieldInfo? _roomIconField;
    internal System.Reflection.FieldInfo? _roomIconOutlineField;
    internal void NotifyMultiplayerLaunchFailed()
    {
        _commitInProgress = false;
        if (_confirmButton != null && GodotObject.IsInstanceValid(_confirmButton))
            _confirmButton.MouseFilter = Control.MouseFilterEnum.Stop;
    }
}
