using System.Reflection;
using Godot;
using HarmonyLib;
using Retry;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Platform.Steam;

[ModInitializer("Initialize")]
public static class Smoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static bool fired;
    static NGame Game => NGame.Instance ?? throw new InvalidOperationException("NGame is unavailable");
    static Assembly Mod => typeof(RetryMod).Assembly;
    static object? Call(string type, string method, params object?[] args) => Mod.GetType("Retry." + type)!.GetMethod(method, All)!.Invoke(null, args);
    static void Flag(string name, bool value) => Mod.GetType("Retry.RetryContext")!.GetField(name, All)!.SetValue(null, value);
    public static void Initialize()
    {
        GD.Print("COMPAT_SMOKE_USERDIR=" + OS.GetUserDataDir());
        if (SteamInitializer.Initialized || !OS.GetUserDataDir().Contains("RetryTheSpireCompat")) throw new Exception("Isolated user directory required");
        SaveManager.Instance.SettingsSave.SeenEaDisclaimer = true;
        new Harmony("retry.compat.unlocks").Patch(typeof(SaveManager).GetMethod("GenerateUnlockStateFromProgress"), prefix: new HarmonyMethod(typeof(Smoke).GetMethod(nameof(AllUnlocks))));
        new Harmony("retry.compat.smoke").Patch(typeof(NMainMenu).GetMethod("_Ready"), postfix: new HarmonyMethod(typeof(Smoke).GetMethod(nameof(Ready))));
    }
    public static bool AllUnlocks(ref UnlockState __result) { __result = UnlockState.all; return false; }
    public static void Ready(NMainMenu __instance)
    {
        if (fired) return; fired = true;
        __instance.GetTree().CreateTimer(2).Timeout += () => _ = Run();
    }
    static async Task Delay(double seconds) => await Game.ToSignal(Game.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Run()
    {
        int exitCode = 0;
        try
        {
            var modes = Enum.GetNames<GameMode>().Contains("Blitz") ? new[] { "Standard", "Dread", "Blitz" } : new[] { "Standard", "Custom" };
            foreach (var name in modes)
            {
                var mode = Enum.Parse<GameMode>(name);
                var character = ModelDb.AllCharacters.First(c => c.GetType().Name == (Enum.GetNames<GameMode>().Contains("Blitz") ? "Fool" : "Ironclad"));
                var target = new RetryTarget { Seed = "TEST12345678", GameMode = mode, Ascension = 0 };
                target.Player.NetId = 1; target.Player.CharacterId = character.Id;
                Flag("IsRetrying", true); Flag("SkipNextNeowEntry", true);
                var state = await (Task<RunState>)Call("GameCompatibility", "StartSingleplayer", character, ActModel.GetDefaultList(), new List<ModifierModel>(), target)!;
                Call("RetryContext", "ResetAll");
                var point = state.Map!.StartingMapPoint.Children.First(p => p.PointType == MapPointType.Monster);
                await RunManager.Instance.EnterMapCoord(point.coord);
                await Delay(1);
                Require(CombatManager.Instance.IsInProgress, "combat not initialized");
                if (SaveManager.Instance.CurrentRunSaveTask is { } task) await task;
                var save = SaveManager.Instance.LoadRunSave().SaveData!;
                Require(save != null, "room save missing");
                var floor = state.MapPointHistory[state.CurrentActIndex].Count - 1;
                target.SourceStartTime = save!.StartTime; target.TargetActIndex = state.CurrentActIndex; target.TargetFloorIndex = floor;
                var native = Call("NativeRunSnapshot", "Find", save.StartTime, state.Rng.StringSeed, state.CurrentActIndex, floor);
                Require(native != null, "native snapshot was not captured");
                Call("NativeRunSnapshot", "ValidateForRestore", save.StartTime, state.Rng.StringSeed, state.CurrentActIndex, floor);
                foreach (var action in new[] { "Exact", "RerollShuffle", "RerollShuffle", "RerollEncounter" })
                {
                    var copy = SaveManager.Instance.LoadRunSave().SaveData!;
                    var restartMode = Enum.Parse(Mod.GetType("Retry.RestartMode")!, action);
                    Call("RoomSaveTransform", "PrepareLoadedSave", copy, restartMode);
                    await (Task)Call("RoomReload", "RestartSingleplayerAsync", RunState.FromSerializable(copy), copy)!;
                    await Delay(.5);
                    Require(CombatManager.Instance.IsInProgress, "combat missing after " + action);
                    GD.Print("COMPAT_SMOKE_PASS " + name + " " + action);
                }
                RunManager.Instance.CleanUp(true);
                await (Task)Call("SingleplayerHistoryLauncher", "LaunchAsync", target)!;
                await Delay(.5);
                Require(CombatManager.Instance.IsInProgress, "history retry did not enter combat");
                Require(RunManager.Instance.ToSave(null).GameMode.ToString() == name, "history mode changed");
                GD.Print("COMPAT_SMOKE_PASS " + name + " native-history " + character.GetType().Name);
                var persisted = SaveManager.Instance.LoadRunSave().SaveData!;
                var expected = (SerializableRun)native!;
                Require(persisted.RunTime == expected.RunTime, "native history room-entry timer changed");
                foreach (var kv in expected.SerializableRng.Rngs)
                    Require(persisted.SerializableRng.Rngs[kv.Key].Equals(kv.Value), "native history room-entry RNG changed: " + kv.Key);
                GD.Print("COMPAT_SMOKE_PASS " + name + " persisted-room-entry");
                RunManager.Instance.CleanUp(true);
                await Delay(.5);
            }
            GD.Print("COMPAT_SMOKE_SUCCESS");
        }
        catch (Exception ex) { exitCode = 1; GD.PrintErr("COMPAT_SMOKE_FAIL " + ex); }
        finally { Game.GetTree().Quit(exitCode); }
    }
}
