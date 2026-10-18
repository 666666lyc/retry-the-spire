using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace Retry;

// Owns view operations for a browser session.
internal sealed class BrowserView
{
    private readonly BrowserSession _session;
    internal BrowserView(BrowserSession session) => _session = session;
    // Hide TopBar elements that aren't meaningful for a view-only
    // history browser: the Map toggle, the speedrun Timer. Pause stays
    // visible — its click is rerouted to NSettingsScreen via a Harmony
    // patch (NTopBarPauseButton_OnRelease_Patch below) so the user
    // gets the out-of-run Settings UI, not the in-run pause menu.
    // NRunTimer's visibility is reasserted by RefreshVisibility (wired
    // to NMapScreen.VisibilityChanged), so a one-shot Visible=false
    // doesn't stick — that's handled via NRunTimer_ToggleTimer_Patch.
    internal void TrimTopBar()
    {
        try
        {
            var nrun = _session.SpawnedNRun;
            if (nrun == null)
                return;
            var topBar = nrun.GlobalUi?.TopBar;
            if (topBar == null)
                return;
            if (topBar.Map != null)
                topBar.Map.Visible = false;
            if (topBar.Timer != null)
                topBar.Timer.Visible = false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser TrimTopBar: {ex.Message}");
        }
    }

    // Reparent the act tabs from our HUD onto the TopBar itself so
    // they ride along with TopBar.AnimHide/AnimShow when the Settings
    // capstone opens. Called after BuildHud has created the tabs.
    internal void AttachTabsToTopBar()
    {
        try
        {
            var tabs = _session._hud?.GetNodeOrNull<HBoxContainer>("RetryTheSpireActTabs");
            var topBar = _session.SpawnedNRun?.GlobalUi?.TopBar;
            if (tabs == null || topBar == null)
                return;
            tabs.Reparent(topBar);
            // Sit just below the deck/pause cluster at top-right.
            tabs.AnchorLeft = 1;
            tabs.AnchorRight = 1;
            tabs.AnchorTop = 0;
            tabs.AnchorBottom = 0;
            tabs.OffsetLeft = -560;
            tabs.OffsetRight = -360;
            tabs.OffsetTop = 18;
            tabs.OffsetBottom = 72;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser AttachTabsToTopBar: {ex.Message}");
        }
    }

    // NMapScreen already owns an NBackButton ("Back" child node) — it's
    // moved off-screen by default. Enable it (which tweens it in) and
    // wire its Released signal to our Close.
    internal void ShowGameBackButton()
    {
        try
        {
            var ms = NMapScreen.Instance;
            if (ms == null)
                return;
            var f = typeof(NMapScreen).GetField("_backButton", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f?.GetValue(ms)is MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton bb)
            {
                bb.Connect(MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl.SignalName.Released, Callable.From<MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton>(_ => _session.Close()));
                bb.Enable();
                GD.Print($"{RetryMod.LogPrefix}browser: NBackButton wired and enabled");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser ShowGameBackButton: {ex.Message}");
        }
    }

    internal void ShowAct(int actIdx)
    {
        if (_session._history == null || _session._runState == null)
            return;
        if (_session._actMaps == null || actIdx >= _session._actMaps.Length || _session._actMaps[actIdx] == null)
            return;
        _session._currentAct = actIdx;
        _session._pendingCoord = null;
        _session.Maps.SetCurrentActIndex(actIdx);
        _session.Maps.ReplaceVisitedCoords(_session._actVisited![actIdx]);
        var map = _session._actMaps[actIdx]!;
        _session._runState.Map = map;
        var ms = NMapScreen.Instance;
        if (ms == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser ShowAct: NMapScreen.Instance null");
            return;
        }

        try
        {
            // Clear stale _bossPointNode / _secondBossPointNode /
            // _startingPointNode references before SetMap — preventing
            // the disposed-object NRE on repeat tab clicks.
            NullStalePointNodes(ms);
            SetHasPlayedAnimation(ms, true);
            ms.SetMap(map, _session._runState.Rng.Seed, clearDrawings: true);
            ms.SetDebugTravelEnabled(true);
            ms.SetTravelEnabled(true);
            var visited = _session._actVisited[actIdx];
            if (visited.Count > 0)
                ms.InitMarker(visited[visited.Count - 1]);
            RefreshMapBg(ms);
            if (!ms.IsOpen)
                ms.Open(isOpenedFromTopBar: true);
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
            if (_session._hud != null)
                UpdateTabHighlights();
            GD.Print($"{RetryMod.LogPrefix}browser act {actIdx}: SetMap done");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser ShowAct {actIdx} EXC: {ex.Message}\n{ex.StackTrace}");
        }
    }

    internal void SetHasPlayedAnimation(NMapScreen ms, bool v)
    {
        var f = typeof(NMapScreen).GetField("_hasPlayedAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
        f?.SetValue(ms, v);
    }

    internal void RefreshMapBg(NMapScreen ms)
    {
        if (_session._runState == null)
            return;
        if (_session._mapBgContainerField?.GetValue(ms)is not Control bg)
            return;
        var act = _session._runState.Act;
        if (act == null)
            return;
        var bgType = bg.GetType();
        var bf = BindingFlags.Instance | BindingFlags.NonPublic;
        var top = bgType.GetField("_mapTop", bf)?.GetValue(bg) as TextureRect;
        var mid = bgType.GetField("_mapMid", bf)?.GetValue(bg) as TextureRect;
        var bot = bgType.GetField("_mapBot", bf)?.GetValue(bg) as TextureRect;
        if (top != null)
            top.Texture = act.MapTopBg;
        if (mid != null)
            mid.Texture = act.MapMidBg;
        if (bot != null)
            bot.Texture = act.MapBotBg;
    }

    internal void ScrollToRow(NMapScreen ms, int row)
    {
        try
        {
            if (_session._distYField?.GetValue(ms)is not float distY)
                return;
            float y = -600f + row * distY;
            if (_session._mapContainerField?.GetValue(ms)is Control mc)
                mc.Position = new Vector2(mc.Position.X, y);
            if (_session._targetDragPosField != null)
                _session._targetDragPosField.SetValue(ms, new Vector2(0f, y));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}ScrollToRow: {ex.Message}");
        }
    }

    internal void NullStalePointNodes(NMapScreen ms)
    {
        try
        {
            // Queue free any still-valid stale node first, then null
            // the field. Skipping the QueueFree on already-disposed
            // refs avoids re-disposal exceptions.
            foreach (var f in new[]
            {
                _session._bossPointNodeField,
                _session._secondBossPointNodeField,
                _session._startingPointNodeField
            }

            )
            {
                if (f?.GetValue(ms)is Node n && GodotObject.IsInstanceValid(n))
                    n.QueueFreeSafely();
                f?.SetValue(ms, null);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}NullStalePointNodes: {ex.Message}");
        }
    }

    internal void ForceRecalculateTravelability(NMapScreen ms)
    {
        try
        {
            _session._recalcTravelabilityMethod?.Invoke(ms, null);
            ms.RefreshAllPointVisuals();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}force recalc travel: {ex.Message}");
        }
    }

    // Boss / starting / second-boss points don't ship with an
    // NMapCircleVfx the way NNormalMapPoint does, so visiting the
    // boss leaves it ringless. We adopt one onto each non-normal
    // visited point so the highlight ring works there too.
    internal Control? FindCircleVfx(NMapPoint p)
    {
        if (p is NNormalMapPoint && _session.CircleVfxField?.GetValue(p)is Control c)
            return c;
        foreach (var ch in p.GetChildren())
        {
            if (ch is MegaCrit.Sts2.Core.Nodes.Vfx.NMapCircleVfx vfx)
                return vfx;
        }

        return null;
    }

    internal void RefreshNodeHighlights()
    {
        try
        {
            var ms = NMapScreen.Instance;
            if (ms == null)
                return;
            if (_session.MapPointDictField?.GetValue(ms)is not System.Collections.IDictionary dict)
                return;
            var highlight = new Color(0.78f, 0.58f, 0.13f, 1.0f); // muted gold
            foreach (var v in dict.Values)
            {
                if (v is not NMapPoint p)
                    continue;
                bool isSelected = _session._pendingCoord is MapCoord sc && p.Point.coord.row == sc.row && p.Point.coord.col == sc.col;
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
                        if (ch is not CanvasItem ci)
                            continue;
                        ulong id = ci.GetInstanceId();
                        if (!_session._origCircleChildModulate.ContainsKey(id))
                            _session._origCircleChildModulate[id] = ci.Modulate;
                        ci.Modulate = isSelected ? highlight : _session._origCircleChildModulate[id];
                    }
                }

                // _outline is only on NNormalMapPoint.
                if (p is NNormalMapPoint && _session.OutlineField?.GetValue(p)is CanvasItem outline)
                {
                    ulong oid = outline.GetInstanceId();
                    if (!_session._origOutlineModulate.ContainsKey(oid))
                        _session._origOutlineModulate[oid] = outline.Modulate;
                    outline.Modulate = isSelected ? highlight : _session._origOutlineModulate[oid];
                }
            }

            // Slide the position marker (the red arrow) to the selected
            // node. SetMapPoint is a no-op while the marker is already
            // visible, so we first flip Visible off via ResetMapPoint
            // — that path actually re-runs the entry tween at the new
            // node position.
            if (_session._pendingCoord is MapCoord pc && _session.MapMarkerField?.GetValue(ms)is MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapMarker marker)
            {
                try
                {
                    marker.ResetMapPoint(); // sets Visible=false
                    ms.InitMarker(pc); // SetMapPoint now repositions + re-tweens
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}move marker: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}RefreshNodeHighlights: {ex.Message}\n{ex.StackTrace}");
        }
    }

    internal Control? ExtractGameConfirmButton() => GameConfirmButton.Extract(_session.Commit.CommitRetry, diagnostics: true);
    internal Node? FindByName(Node root, string name)
    {
        if (root.Name == name)
            return root;
        foreach (var ch in root.GetChildren())
        {
            var hit = FindByName(ch, name);
            if (hit != null)
                return hit;
        }

        return null;
    }

    internal Button MakeBannerButton(string text, Color bg, Color hoverBg)
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
            BorderWidthLeft = 3,
            BorderWidthRight = 3,
            BorderWidthTop = 3,
            BorderWidthBottom = 3,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 18,
            ContentMarginRight = 18,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            ShadowSize = 8,
            ShadowColor = new Color(0, 0, 0, 0.65f),
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

    internal void BuildHud()
    {
        try
        {
            if (_session._overlay == null)
                return;
            // The HUD is a sibling of NRun (under our overlay) so it
            // floats above the in-game UI and is freed with the overlay.
            _session._hud = new Control
            {
                Name = "RetryTheSpireBrowserHud"
            };
            _session._hud.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            // Ignore so empty HUD areas don't intercept scroll/click for
            // NMapScreen underneath; child Buttons still get clicks
            // because Ignore on a parent doesn't block children.
            _session._hud.MouseFilter = Control.MouseFilterEnum.Ignore;
            _session._hud.ZIndex = 100;
            _session._overlay.AddChildSafely(_session._hud);
            // Game-style act tabs in the top-right of the TopBar
            // (where the timer used to sit). Use cream/gold colors
            // and the game's heavy outline-style font.
            var tabs = new HBoxContainer
            {
                Name = "RetryTheSpireActTabs"
            };
            tabs.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            tabs.OffsetLeft = -460;
            tabs.OffsetRight = -260;
            tabs.OffsetTop = 18;
            tabs.OffsetBottom = 72;
            tabs.AddThemeConstantOverride("separation", 6);
            _session._hud.AddChildSafely(tabs);
            BuildTabs(tabs);
            // Real in-game NConfirmButton — extracted from the
            // character_select_screen scene so we get the actual red
            // banner art, hover scaling, and tween-in animation.
            // Stored as _confirmButton (Control type since NConfirmButton
            // lives in CommonUi and we just need its base Control API).
            _session._confirmButton = ExtractGameConfirmButton();
            if (_session._confirmButton != null)
            {
                _session._hud.AddChildSafely(_session._confirmButton);
            }
            else
            {
                // Fallback to our own styled button if extraction failed.
                GD.PrintErr($"{RetryMod.LogPrefix}browser: confirm extract failed, using fallback");
                _session._confirmButton = MakeBannerButton(HistoryRetryText.Confirm, new Color(0.78f, 0.16f, 0.10f), new Color(1f, 0.30f, 0.18f));
                _session._confirmButton.Name = "RetryTheSpireConfirmBtn";
                _session._confirmButton.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
                _session._confirmButton.OffsetLeft = -260;
                _session._confirmButton.OffsetRight = -30;
                _session._confirmButton.OffsetTop = -90;
                _session._confirmButton.OffsetBottom = -30;
                _session._confirmButton.CustomMinimumSize = new Vector2(230, 60);
                _session._confirmButton.Visible = false;
                _session._confirmButton.Connect(Button.SignalName.Pressed, Callable.From(() => _session.Commit.CommitRetry()));
                _session._hud.AddChildSafely(_session._confirmButton);
            }

            GD.Print($"{RetryMod.LogPrefix}browser BuildHud: done");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser BuildHud EXC: {ex.Message}\n{ex.StackTrace}");
        }
    }

    internal void BuildTabs(HBoxContainer container)
    {
        if (_session._history == null)
            return;
        int actCount = _session._history.MapPointHistory.Count;
        _session._tabButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            int actIdx = i;
            string name = i switch
            {
                0 => "I",
                1 => "II",
                2 => "III",
                _ => $"{i + 1}"};
            bool reached = actIdx < actCount && _session._history.MapPointHistory[actIdx].Count > 0;
            // Roman-numeral act buttons in the dark/cream TopBar palette
            // so they read as part of the top bar.
            var btn = MakeBannerButton(name, new Color(0.16f, 0.11f, 0.06f), // dark wood
 new Color(0.32f, 0.22f, 0.08f)); // hover lighter
            btn.Disabled = !reached;
            btn.CustomMinimumSize = new Vector2(60, 54);
            btn.AddThemeFontSizeOverride("font_size", 24);
            btn.Connect(Button.SignalName.Pressed, Callable.From(() => ShowAct(actIdx)));
            container.AddChildSafely(btn);
            _session._tabButtons[i] = btn;
        }

        UpdateTabHighlights();
    }

    internal void UpdateTabHighlights()
    {
        if (_session._tabButtons == null)
            return;
        for (int i = 0; i < _session._tabButtons.Length; i++)
        {
            var btn = _session._tabButtons[i];
            if (btn == null)
                continue;
            // Gold highlight on the current act; dimmed on unreached.
            if (i == _session._currentAct)
                btn.Modulate = new Color(1f, 0.92f, 0.55f, 1f);
            else if (btn.Disabled)
                btn.Modulate = new Color(0.4f, 0.4f, 0.4f, 0.55f);
            else
                btn.Modulate = new Color(1, 1, 1, 1);
        }
    }

    // First click on a visited node: select + preview the floor's
    // saved state (HP, gold). Confirm button commits the retry.
    internal void HandleMapPointClick(MapCoord coord)
    {
        if (_session._history == null || _session._player == null)
            return;
        if (_session._actVisited == null)
            return;
        var visited = _session._actVisited[_session._currentAct];
        int floor = -1;
        for (int i = 0; i < visited.Count; i++)
        {
            if (visited[i].row == coord.row && visited[i].col == coord.col)
            {
                floor = i;
                break;
            }
        }

        if (floor < 0)
        {
            GD.Print($"{RetryMod.LogPrefix}browser click rejected: coord {coord.row},{coord.col} not in visited");
            return;
        }

        var actEntries = _session._history.MapPointHistory[_session._currentAct];
        if (floor >= actEntries.Count)
            return;
        _session._pendingCoord = coord;
        _session.Preview.UpdatePreviewForFloor(_session._currentAct, floor);
        RefreshNodeHighlights();
        if (_session._confirmButton != null)
        {
            _session._confirmButton.Visible = true;
            // If this is the real NConfirmButton extracted from the
            // character-select scene, call Enable() so it tweens in
            // from off-screen (its _showPos is bottom-right).
            try
            {
                var enableMethod = _session._confirmButton.GetType().GetMethod("Enable");
                enableMethod?.Invoke(_session._confirmButton, null);
            }
            catch
            {
            }
        }

        GD.Print($"{RetryMod.LogPrefix}browser preview: act={_session._currentAct} floor={floor} coord=({coord.row},{coord.col})");
    }

    // Find the currently-displayed submenu (e.g. NRunHistory) by
    // walking the menu's children for an NSubmenu with local Visible
    // = true. The menu only shows one submenu at a time so the first
    // match is the right one.
    internal MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu? FindActiveSubmenu(Node root)
    {
        foreach (var sm in FindAllOfType<MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenu>(root))
            if (sm.Visible)
                return sm;
        return null;
    }

    internal IEnumerable<T> FindAllOfType<T>(Node root)
        where T : Node
    {
        if (root is T t)
            yield return t;
        foreach (var c in root.GetChildren())
        {
            if (c is Node n)
                foreach (var hit in FindAllOfType<T>(n))
                    yield return hit;
        }
    }
}
