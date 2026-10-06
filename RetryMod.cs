// Retry mod entry point. The mod patches the run-history screen to
// make past nodes clickable and resumable, with an optional per-act
// map view button for picking a node from the real map UI. Both
// paths funnel into RetryRunner, which reconstructs the player's
// state at the target node and loads a synthesized SerializableRun
// through the same NGame.LoadRun path the "Continue" button uses.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Retry;

[ModInitializer("Initialize")]
public static class RetryMod
{
    public const string Version = "0.5.3";
    public const string LogPrefix = "[Retry the Spire] ";

    public static bool Enabled = true;

    private static Harmony? _harmony;

    public static void Initialize()
    {
        Guid mvid = typeof(RetryMod).Assembly.ManifestModule.ModuleVersionId;
        GD.Print($"{LogPrefix}v{Version} initializing mvid={mvid:D} raw=steam-checked-v1...");
        try
        {
            _harmony = new Harmony("retrythespire.mod");
            ModHelpers.TryPatchAll(_harmony, typeof(RetryMod).Assembly, LogPrefix);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix}Init harmony: {ex}");
        }
    }
}
