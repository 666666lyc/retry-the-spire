using MegaCrit.Sts2.Core.Localization;

namespace Retry;

// Read the active game language when a control is created; do not cache it.
internal static class HistoryRetryText
{
    internal static string ViewActs => Select("View Acts", "查看章节", "查看章節");
    internal static string AbandonSavedRunTitle => Select("Abandon saved run?", "放弃已保存的对局？", "放棄已儲存的對局？");
    internal static string AbandonSavedMultiplayerRunTitle => Select("Abandon saved multiplayer run?", "放弃已保存的多人对局？", "放棄已儲存的多人對局？");
    internal static string AbandonCurrentRunTitle => Select("Abandon current run?", "放弃当前对局？", "放棄目前的對局？");
    internal static string RetryEndsCurrentRunBody => Select(
        "Starting a retry will end your in-progress run.",
        "开始重打将结束正在进行的对局。", "開始重打將結束正在進行的對局。");
    internal static string ViewingOtherActsBody => Select(
        "Viewing acts of another run requires ending your in-progress run.",
        "查看其他对局的章节需要结束正在进行的对局。", "查看其他對局的章節需要結束正在進行的對局。");
    internal static string RetryOverwritesSavedRunBody => Select(
        "Starting a retry will overwrite your existing saved run.",
        "重打将覆盖当前已保存的对局。", "重打將覆寫目前已儲存的對局。");
    internal static string BrowserOverwritesSavedRunBody => Select(
        "Starting this retry will overwrite your existing saved run.",
        "重打将覆盖当前已保存的对局。", "重打將覆寫目前已儲存的對局。");
    internal static string BrowserOverwritesSavedMultiplayerRunBody => Select(
        "Starting this retry will overwrite your existing multiplayer saved run. Your single-player save is not affected.",
        "重打将覆盖当前已保存的多人对局，不影响单人存档。", "重打將覆寫目前已儲存的多人對局，不影響單人存檔。");
    internal static string AbandonWithoutHistory => Select("Abandon (no save)", "放弃（不记录历史）", "放棄（不記錄歷史）");
    internal static string ClickAgainToConfirm => Select("Click again to confirm", "再次点击确认", "再次點擊確認");
    internal static string Confirm => Select("CONFIRM", "确认", "確認");

    private static string Select(string english, string simplifiedChinese, string traditionalChinese) => LocManager.Instance.Language switch
    {
        "zhs" => simplifiedChinese,
        "zht" => traditionalChinese,
        _ => english,
    };
}
