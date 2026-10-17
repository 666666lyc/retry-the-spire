// Diagnostic Harmony patches around the act-map browser. Active only
// while NActMapBrowser.Active so they don't spam the log otherwise.
// We log:
//   • Every NMapScreen._GuiInput call (proves input reached the screen
//     — or didn't).
//   • Every NMapScreen.ProcessScrollEvent / ProcessMouseEvent (so we
//     can see if CanScroll bailed).
//   • Every NMapPoint.OnRelease (proves clicks reach a node — or not).
//   • A one-shot dump after Open: ActiveScreenContext.GetCurrentScreen,
//     viewport focus owner, NMapScreen.IsOpen, _hasPlayedAnimation,
//     _actAnimTween, _isInputDisabled.
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace Retry;

internal static class BrowserDiagnostics
{
    private static int _guiCalls;
    private static int _scrollCalls;
    private static int _mouseCalls;
    private static int _clickCalls;
    public static void Reset()
    {
        _guiCalls = _scrollCalls = _mouseCalls = _clickCalls = 0;
    }

    public static void DumpState()
    {
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            var vp = tree.Root;
            var focus = vp?.GuiGetFocusOwner();
            var current = MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext.Instance.GetCurrentScreen();
            var ms = NMapScreen.Instance;
            string state = "ms=null";
            if (ms != null)
            {
                var f1 = typeof(NMapScreen).GetField("_hasPlayedAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
                var f2 = typeof(NMapScreen).GetField("_isInputDisabled", BindingFlags.Instance | BindingFlags.NonPublic);
                var f3 = typeof(NMapScreen).GetField("_actAnimTween", BindingFlags.Instance | BindingFlags.NonPublic);
                bool? hpa = f1?.GetValue(ms) as bool? ;
                bool? iid = f2?.GetValue(ms) as bool? ;
                var anim = f3?.GetValue(ms);
                state = $"ms.IsOpen={ms.IsOpen} ms.Visible={ms.Visible} ms.IsTraveling={ms.IsTraveling} ms.IsTravelEnabled={ms.IsTravelEnabled} _hasPlayedAnimation={hpa} _isInputDisabled={iid} _actAnimTween={(anim != null)}";
            }

            GD.Print($"{RetryMod.LogPrefix}DIAG: current={current?.GetType().Name ?? "null"} focus={focus?.GetType().Name ?? "null"}({focus?.Name}) {state} counts=gui:{_guiCalls}/scroll:{_scrollCalls}/mouse:{_mouseCalls}/click:{_clickCalls}");
            // Dump NMapScreen geometry so we can see if its rect even
            // contains the mouse cursor.
            if (ms != null)
            {
                var rect = ms.GetGlobalRect();
                var mouse = ms.GetGlobalMousePosition();
                GD.Print($"{RetryMod.LogPrefix}DIAG ms.GlobalRect={rect} mouse={mouse} hit={rect.HasPoint(mouse)} ms.MouseFilter={ms.MouseFilter}");
                // Walk up and dump each ancestor's siblings — find what
                // might be hit-testing first.
                Godot.Node? cur = ms.GetParent();
                int depth = 0;
                while (cur != null && depth < 4)
                {
                    var sb = new System.Text.StringBuilder($"DIAG siblings@{cur.GetType().Name}: ");
                    foreach (var ch in cur.GetChildren())
                    {
                        sb.Append(ch.GetType().Name).Append("/").Append(ch.Name);
                        if (ch is Godot.Control cc)
                        {
                            var crect = cc.GetGlobalRect();
                            var hits = crect.HasPoint(mouse);
                            sb.Append($"[v={cc.Visible}/mf={cc.MouseFilter}/rect={crect}/hit={hits}] ");
                        }
                        else
                            sb.Append(' ');
                    }

                    GD.Print($"{RetryMod.LogPrefix}{sb}");
                    cur = cur.GetParent();
                    depth++;
                }
            }

            // Dump NMapScreen's ancestor chain so we can see which
            // control is blocking input from reaching it.
            if (ms != null)
            {
                var chain = new System.Text.StringBuilder();
                Godot.Node? cur = ms;
                int hops = 0;
                while (cur != null && hops < 20)
                {
                    chain.Append(cur.GetType().Name).Append("/").Append(cur.Name);
                    if (cur is Godot.Control c)
                    {
                        chain.Append($"[v={c.Visible}/mf={c.MouseFilter}/pm={cur.ProcessMode}/z={c.ZIndex}]");
                    }

                    cur = cur.GetParent();
                    if (cur != null)
                        chain.Append(" < ");
                    hops++;
                }

                GD.Print($"{RetryMod.LogPrefix}DIAG chain: {chain}");
            }

            // Also dump NGame's direct children so we can see what
            // might be a sibling intercepting events.
            try
            {
                var ng = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
                if (ng != null)
                {
                    var siblings = new System.Text.StringBuilder();
                    foreach (var ch in ng.GetChildren())
                    {
                        siblings.Append(ch.GetType().Name).Append("/").Append(ch.Name);
                        if (ch is Godot.Control cc)
                            siblings.Append($"[v={cc.Visible}/mf={cc.MouseFilter}/pm={ch.ProcessMode}/z={cc.ZIndex}] ");
                        else
                            siblings.Append(' ');
                    }

                    GD.Print($"{RetryMod.LogPrefix}DIAG NGame.children: {siblings}");
                }
            }
            catch
            {
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}DIAG EXC: {ex.Message}");
        }
    }

    public static void TickGui()
    {
        if (NActMapBrowser.Active)
            _guiCalls++;
    }

    public static void TickScroll()
    {
        if (NActMapBrowser.Active)
            _scrollCalls++;
    }

    public static void TickMouse()
    {
        if (NActMapBrowser.Active)
            _mouseCalls++;
    }

    public static void TickClick()
    {
        if (NActMapBrowser.Active)
            _clickCalls++;
    }
}

[HarmonyPatch]
internal static class NMapScreen_GuiInput_Diag
{
    static MethodBase TargetMethod() => AccessTools.Method(typeof(NMapScreen), "_GuiInput", new[] { typeof(InputEvent) });
    private static int _logged;
    static void Prefix(InputEvent inputEvent)
    {
        BrowserDiagnostics.TickGui();
        if (_logged < 10)
        {
            _logged++;
            GD.Print($"{RetryMod.LogPrefix}DIAG _GuiInput PATCH HIT: {inputEvent.GetType().Name} active={NActMapBrowser.Active}");
        }
    }
}

// Verify Harmony works at all by patching _Process, which fires every
// frame.
[HarmonyPatch]
internal static class NMapScreen_Process_Diag
{
    static MethodBase TargetMethod() => AccessTools.Method(typeof(NMapScreen), "_Process", new[] { typeof(double) });
    private static int _logged;
    static void Prefix()
    {
        if (_logged < 3)
        {
            _logged++;
            GD.Print($"{RetryMod.LogPrefix}DIAG _Process PATCH HIT (Harmony works on NMapScreen)");
        }
    }
}

[HarmonyPatch(typeof(NMapScreen), "ProcessScrollEvent")]
internal static class NMapScreen_ProcessScroll_Diag
{
    static void Prefix(InputEvent inputEvent)
    {
        if (!NActMapBrowser.Active)
            return;
        BrowserDiagnostics.TickScroll();
        if (inputEvent is InputEventMouseButton mb)
            GD.Print($"{RetryMod.LogPrefix}DIAG ProcessScrollEvent mb btn={mb.ButtonIndex}");
    }
}

[HarmonyPatch(typeof(NMapScreen), "ProcessMouseEvent")]
internal static class NMapScreen_ProcessMouse_Diag
{
    static void Prefix(InputEvent inputEvent)
    {
        if (!NActMapBrowser.Active)
            return;
        BrowserDiagnostics.TickMouse();
    }
}

// NMapPoint.OnRelease is protected sealed — patch by AccessTools.
[HarmonyPatch]
internal static class NMapPoint_OnRelease_Diag
{
    static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(NMapPoint), "OnRelease");
    static void Prefix(NMapPoint __instance)
    {
        if (!NActMapBrowser.Active)
            return;
        BrowserDiagnostics.TickClick();
        // Compute IsTravelable directly so we know why the click might
        // bail.
        var screen = NMapScreen.Instance;
        GD.Print($"{RetryMod.LogPrefix}DIAG OnRelease coord=({__instance.Point.coord.row},{__instance.Point.coord.col}) state={__instance.State} debugTravel={screen?.IsDebugTravelEnabled} travelEnabled={screen?.IsTravelEnabled}");
    }
}

// Trace NMapPoint.OnFocus to see why hover tooltip doesn't fire.
[HarmonyPatch]
internal static class NMapPoint_OnFocus_Diag
{
    private static int _logged;
    static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(NMapPoint), "OnFocus");
    static void Prefix(NMapPoint __instance)
    {
        if (!NActMapBrowser.Active)
            return;
        if (_logged > 8)
            return;
        _logged++;
        var ms = NMapScreen.Instance;
        var runState = ms != null ? typeof(NMapScreen).GetField("_runState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(ms) : null;
        var rs = runState as MegaCrit.Sts2.Core.Runs.RunState;
        var entryFor = rs?.GetHistoryEntryFor(new MegaCrit.Sts2.Core.Runs.MapLocation(__instance.Point.coord, rs.CurrentActIndex));
        GD.Print($"{RetryMod.LogPrefix}DIAG OnFocus coord=({__instance.Point.coord.row},{__instance.Point.coord.col}) state={__instance.State} curCoord={rs?.MapLocation.coord} historyEntry={(entryFor != null ? "found" : "null")} usingController={MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager.Instance.IsUsingController}");
    }
}
