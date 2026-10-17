// Builds a native multiplayer SerializableRun at the boundary immediately
// before the selected historical room, then hands it to the game's own
// LoadRunLobby. The serialized payload contains no Retry-owned types, so
// joining clients do not need this mod installed.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

internal static class MultiplayerRetryLauncher
{
    public static void Begin(RunHistory history, RunHistoryPlayer selectedPlayer, int actIndex, int floorIndex, MapCoord targetCoord, IReadOnlyList<MapCoord> pathThroughTarget, ulong hostNetId, NMainMenu? mainMenu, bool browserAlreadyClosed = false)
    {
        bool browserClosed = browserAlreadyClosed;
        try
        {
            MultiplayerHistorySaveBuilder.ValidateRequest(history, actIndex, floorIndex, targetCoord, pathThroughTarget, hostNetId, mainMenu);
            var target = MultiplayerHistorySaveBuilder.BuildTarget(history, selectedPlayer, actIndex, floorIndex);
            // The browser owns a fake single-player RunManager. Tear it down
            // before installing the clean synthesis state used for ToSave.
            if (!browserClosed)
            {
                NActMapBrowser.Close();
                browserClosed = true;
            }

            var build = MultiplayerHistorySaveBuilder.BuildNativeSave(history, target, actIndex, floorIndex, targetCoord, pathThroughTarget, hostNetId);
            var save = build.Save;
            RetryContext.ResetAll();
            GD.Print($"{RetryMod.LogPrefix}multiplayer native save ready: " + $"players={save.Players.Count} act={save.CurrentActIndex} " + $"visited={save.VisitedMapCoords.Count} history={save.MapPointHistory.Sum(a => a.Count)} " + $"mode={save.GameMode} ascension={save.Ascension}");
            // Let the browser overlay finish its queued frees before pushing
            // the multiplayer submenu and starting the Steam host.
            var tree = mainMenu!.GetTree();
            tree.CreateTimer(0.05).Connect("timeout", Callable.From(() =>
            {
                try
                {
                    RetryContext.ResetAll();
                    LocalContext.NetId = hostNetId;
                    void StartLobby()
                    {
                        var submenu = mainMenu.OpenMultiplayerSubmenu();
                        submenu.StartHost(save);
                        GD.Print($"{RetryMod.LogPrefix}native multiplayer load lobby requested");
                    }

                    if (!string.IsNullOrWhiteSpace(build.TargetRoomWarning))
                    {
                        OneButtonNotice.Show("房间类型还原警告", build.TargetRoomWarning + "\n\n仍将继续创建房间。", "继续开房", StartLobby);
                    }
                    else
                    {
                        StartLobby();
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}start multiplayer host: {ex.Message}\n{ex.StackTrace}");
                }
            }));
        }
        catch (Exception ex)
        {
            RetryContext.ResetAll();
            GD.PrintErr($"{RetryMod.LogPrefix}multiplayer host failed: {ex.Message}\n{ex.StackTrace}");
            if (!browserClosed)
                NActMapBrowser.NotifyMultiplayerLaunchFailed();
        }
    }
}
