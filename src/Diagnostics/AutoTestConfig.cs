using System;
using Godot;

namespace Retry;

public sealed class AutoTestConfig
{
    public bool Enabled;
    public string RunFileBasename = "";
    public int Act;
    public int Floor;
    public bool BrowserTest;
    public bool CompareAfterRetry;
    public bool ExactSecondPass;
    public bool AutoAbandonExisting;
    public bool WriteHistoryWhenAutoAbandon = true;
    public string SequenceTargets = "";
    public bool QuitWhenDone;
    public string BrowserClickFloors = "";
    public string ScreenshotPrefix = "retry_the_spire_browser";
    public static AutoTestConfig? LoadOrNull()
    {
        try
        {
            using var f = FileAccess.Open("user://retry_the_spire_test_target.json", FileAccess.ModeFlags.Read);
            if (f == null)
                return null;
            var json = f.GetAsText();
            var parser = new Json();
            if (parser.Parse(json) != Error.Ok)
                return null;
            if (parser.Data.AsGodotDictionary()is not Godot.Collections.Dictionary d)
                return null;
            var cfg = new AutoTestConfig
            {
                Enabled = d.ContainsKey("enabled") && (bool)d["enabled"],
                RunFileBasename = d.ContainsKey("run_file_basename") ? (string)d["run_file_basename"] : "",
                Act = d.ContainsKey("act") ? (int)d["act"] : 0,
                Floor = d.ContainsKey("floor") ? (int)d["floor"] : 0,
                BrowserTest = d.ContainsKey("browser_test") && (bool)d["browser_test"],
                CompareAfterRetry = d.ContainsKey("compare_after_retry") && (bool)d["compare_after_retry"],
                ExactSecondPass = d.ContainsKey("exact_second_pass") && (bool)d["exact_second_pass"],
                AutoAbandonExisting = d.ContainsKey("auto_abandon_existing") && (bool)d["auto_abandon_existing"],
                WriteHistoryWhenAutoAbandon = !d.ContainsKey("write_history_when_auto_abandon") || (bool)d["write_history_when_auto_abandon"],
                SequenceTargets = d.ContainsKey("sequence_targets") ? (string)d["sequence_targets"] : "",
                QuitWhenDone = d.ContainsKey("quit_when_done") && (bool)d["quit_when_done"],
                BrowserClickFloors = d.ContainsKey("browser_click_floors") ? (string)d["browser_click_floors"] : "",
                ScreenshotPrefix = d.ContainsKey("screenshot_prefix") ? (string)d["screenshot_prefix"] : "retry_the_spire_browser",
            };
            return cfg;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test config: {ex.Message}");
            return null;
        }
    }
}
