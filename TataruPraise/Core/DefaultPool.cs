using System.Collections.Generic;

namespace TataruPraise.Core;

/// <summary>
/// 內建的預設誇獎池：<b>每個情境一句極短提示</b>。
/// </summary>
/// <remarks>
/// 🔴 <b>定位＝「不是要太多對話，只是要有聲音」</b>（使用者實機用過之後定調的）。
/// 每個情境只有一句、全部在 8 個字以內，聽起來像音效而不是對白。
/// 想要長句誇獎的人請自己在設定視窗的「進階」把句長上限調高，再用 Gemini 擴充池——
/// <b>那是加上去的能力，不是預設</b>。
/// <para>
/// 🔴 每一句都<b>必須含自然的中文標點</b>（句號、驚嘆號、問號）。GPT-SoVITS 橋接是靠標點斷句的，
/// 沒有標點的句子會讓聲線越念越高變成怪腔——這是 d:\love 那邊的實測結論，不是風格偏好。
/// </para>
/// <para>
/// 📌 用詞對齊台服：<b>Gil 不譯</b>（台服 <c>Addon</c>／<c>LogMessage</c> 裡一律寫「Gil」，沒有「金幣」）。
/// 長度量法用 <see cref="PraiseText.CountChars"/>（與生成端的硬過濾同一把尺）。
/// </para>
/// <para>
/// 📌 這裡只在「池是空的」時候當種子灌進去（見 <see cref="PraisePool.SeedIfEmpty"/>）、
/// 或是<b>某個內建情境的鍵不存在</b>時補那一個鍵（見 <see cref="PraisePool.Load"/>），
/// <b>不會覆蓋使用者已經有的池</b>，也不會把使用者刪掉的句子塞回來。
/// 🔴 反過來說，改這裡的句子<b>對既有使用者沒有任何效果</b>——他們的 pool.json 早就存在了。
/// 要換句子請用設定視窗情境表格上的「短句」欄位直接編輯，或按「重置為預設池」。
/// </para>
/// <para>
/// ⚠️ 舊版的 102 句長誇獎句（12~25 字）已於本版整批移除。要找回來看 git 歷史。
/// </para>
/// </remarks>
public static class DefaultPool
{
    public static Dictionary<string, List<string>> Lines { get; } = new()
    {
        [PraiseCategory.DutyComplete] = ["完美收工！"],
        [PraiseCategory.LevelUp] = ["恭喜升等！"],
        [PraiseCategory.Login] = ["歡迎回來。"],
        [PraiseCategory.GilMilestone] = ["這麼多 Gil！"],
        [PraiseCategory.Submarine] = ["潛艇回來啦！"],
        [PraiseCategory.Retainer] = ["僱員回來啦！"],
        [PraiseCategory.ExpertDelivery] = ["稀有品都交完啦！"],
        [PraiseCategory.Market] = ["市場重掛好啦！"],
        [PraiseCategory.Crafting] = ["製作完成！"],
        [PraiseCategory.Cosmic] = ["任務金評。"],
        [PraiseCategory.LowHp] = ["危險！"],
        [PraiseCategory.MarkedByMany] = ["小心！"],
        [PraiseCategory.EnemyBehind] = ["後面！"],
        [PraiseCategory.DutyStart] = ["出發！"],
        [PraiseCategory.ReadyCheck] = ["準備確認！"],
        [PraiseCategory.CutsceneEnd] = ["注意！"],
        [PraiseCategory.DutyPop] = ["排到了！"],
        [PraiseCategory.FlagArrived] = ["到了！"],
        [PraiseCategory.Tell] = ["有人找你！"],
        [PraiseCategory.Arrived] = ["到了！"],
        [PraiseCategory.Jackpot] = ["恭喜中獎！"],
        [PraiseCategory.NeedHelp] = ["需要幫忙！"],
        [PraiseCategory.PlayerAlert] = ["注意注意！"],
        [PraiseCategory.BeingWatched] = ["有人盯著你！"],
        [PraiseCategory.TellReceived] = ["有人密你！"],
        [PraiseCategory.PartyInvite] = ["組隊邀請！"],
        [PraiseCategory.TradeRequest] = ["交易請求！"],
        [PraiseCategory.DutyRunStopped] = ["跑本停了！"],
        [PraiseCategory.GatherStopped] = ["採集停了！"],
        [PraiseCategory.RareFish] = ["釣到稀有魚！"],
        [PraiseCategory.HuntFound] = ["發現魔物！"],
        [PraiseCategory.BagAlmostFull] = ["背包快滿了！"],
        [PraiseCategory.DailyReset] = ["每日重置！"],
    };
}
