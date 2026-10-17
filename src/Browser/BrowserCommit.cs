using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace Retry;

// Owns commit operations for a browser session.
internal sealed class BrowserCommit
{
    private readonly BrowserSession _session;
    internal BrowserCommit(BrowserSession session) => _session = session;
    internal void CommitRetry()
    {
        if (_session._commitInProgress)
            return;
        if (_session._pendingCoord is not MapCoord coord)
            return;
        if (_session._history == null || _session._player == null || _session._actVisited == null)
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
            return;
        // Capture before any tear-down so the closure has stable args.
        var history = _session._history;
        var player = _session._player;
        var act = _session._currentAct;
        bool isMultiplayer = history.Players.Count > 1;
        List<MapCoord>? multiplayerPath = null;
        if (isMultiplayer)
        {
            var fullPath = _session._actVisited != null && act < _session._actVisited.Length ? _session._actVisited[act] : null;
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
        _session._commitInProgress = true;
        GD.Print($"{RetryMod.LogPrefix}browser CONFIRM → retry mode={(isMultiplayer ? "multiplayer" : "singleplayer")} act={act} floor={floor} coord=({coord.row},{coord.col})");
        NMainMenu? mainMenu = FindMainMenu(_session._hiddenMenu) ?? FindMainMenu(NGame.Instance?.RootSceneContainer?.CurrentScene);
        ulong multiplayerHostNetId = isMultiplayer ? (_session._savedLocalNetId ?? player.Id) : 0;
        void DoRetry(bool browserAlreadyClosed = false)
        {
            if (isMultiplayer)
            {
                if (multiplayerPath == null)
                    return;
                MultiplayerRetryLauncher.Begin(history, player, act, floor, coord, multiplayerPath, multiplayerHostNetId, mainMenu, browserAlreadyClosed);
                return;
            }

            // Full tear-down via the standard Close path: frees the
            // NRun overlay (which kills our HUD, restores menu, etc.)
            // and lets RetryRunner.Begin transition cleanly.
            _session.Close();
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
        bool hasSave = isMultiplayer ? MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.HasMultiplayerRunSave == true : MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.HasRunSave == true;
        if (hasSave)
        {
            // NModalContainer.Instance resolves to the preview NRun's modal
            // layer while View Acts is open. Popups added there render behind
            // the browser HUD. Capture launch data above, close the preview,
            // then wait for its queued free before resolving the menu modal.
            if (mainMenu == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser save confirmation failed: main menu unavailable mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
                _session._commitInProgress = false;
                return;
            }

            var tree = mainMenu.GetTree();
            GD.Print($"{RetryMod.LogPrefix}browser save confirmation: closing preview mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
            _session.Close();
            tree.CreateTimer(0.05).Connect("timeout", Callable.From(() =>
            {
                string title = isMultiplayer ? "Abandon saved multiplayer run?" : "Abandon saved run?";
                string body = isMultiplayer ? "Starting this retry will overwrite your existing multiplayer saved run. Your single-player save is not affected." : "Starting this retry will overwrite your existing saved run.";
                GD.Print($"{RetryMod.LogPrefix}browser save confirmation: showing mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
                RetryAbandonModal.Show(title: title, body: body, onChoice: c =>
                {
                    if (c == RetryAbandonModal.Choice.Cancel)
                    {
                        GD.Print($"{RetryMod.LogPrefix}browser retry cancelled at save confirmation mode={(isMultiplayer ? "multiplayer" : "singleplayer")}");
                        return;
                    }

                    bool writeHistory = c == RetryAbandonModal.Choice.AbandonSave;
                    if (isMultiplayer)
                    {
                        RetryRunner.PerformMultiplayerAbandon(writeHistory: writeHistory, localPlayerId: multiplayerHostNetId);
                    }
                    else
                    {
                        RetryRunner.PerformAbandon(writeHistory: writeHistory, inProgress: false);
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

    internal NMainMenu? FindMainMenu(Node? root)
    {
        if (root == null)
            return null;
        if (root is NMainMenu menu)
            return menu;
        foreach (Node child in root.GetChildren())
        {
            var found = FindMainMenu(child);
            if (found != null)
                return found;
        }

        return null;
    }
}
