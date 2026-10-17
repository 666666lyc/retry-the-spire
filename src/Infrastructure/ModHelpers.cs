// Mod-wide helpers, ported from patterns in Alchyr's BaseLib-StS2.
// See run_table/ModHelpers.cs for the rationale; copy kept per-mod
// to avoid a shared dependency.
using System;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace Retry;

internal static class ModHelpers
{
    public static void TryPatchAll(Harmony harmony, Assembly assembly, string logPrefix)
    {
        int ok = 0, fail = 0;
        foreach (var type in PatchRegistration.TypesInOrder(assembly))
        {
            var attrs = HarmonyMethodExtensions.GetFromType(type);
            if (attrs == null || attrs.Count == 0)
                continue;
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                GD.PrintErr($"{logPrefix}patch {type.Name} skipped: {ex.Message}");
            }
        }

        GD.Print($"{logPrefix}Harmony: {ok} ok, {fail} failed");
    }

    private static readonly string[] AllFontSizeKeys =
    {
        "font_size",
        "normal_font_size",
        "bold_font_size",
        "italics_font_size",
        "bold_italics_font_size",
        "mono_font_size",
    };
    public static void AddThemeFontSizeOverrideAll(this Control control, int fontSize)
    {
        foreach (var k in AllFontSizeKeys)
            control.AddThemeFontSizeOverride(k, fontSize);
    }
}
