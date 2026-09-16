using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Lumina.Excel.Sheets;

namespace TataruPraise.Core;

/// <summary>
/// 遊戲事件 → 出聲的內建觸發線。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意不做任何靠聊天文字比對的觸發</b>（成就、製作大成功…）。台服的中文訊息字面沒辦法離線確定，
/// 憑印象或照國際服寫死一定錯，而且錯法是靜默的（比不到就永遠不觸發，看起來像功能沒做）。
/// 全部走 Dalamud 的<b>結構化</b>事件或直接讀遊戲數值：
/// 密語看的是 <see cref="XivChatType.TellIncoming"/> <b>這個列舉值</b>，不是訊息內容；
/// 組隊邀請與交易請求看的是<b>那個視窗看不看得見</b>（每 250ms 輪詢、取上升緣），不是視窗裡的字。
/// <para>
/// 🔴 <b>只讀狀態、只出聲</b>：不施放、不走位、不改目標、不按任何按鈕。
/// </para>
/// </remarks>
public sealed class TriggerWatcher : IDisposable
{
    /// <summary>Gil 的輪詢間隔。5 秒對「跨過一百萬」這種粒度綽綽有餘，而且完全不佔 tick。</summary>
    private static readonly TimeSpan GilPollInterval = TimeSpan.FromSeconds(5);

    private readonly Configuration config;
    private readonly PraiseService service;

    /// <summary>各職業上一次看到的等級。<b>沒有紀錄就不觸發</b>，見 <see cref="OnLevelChanged"/>。</summary>
    private readonly Dictionary<uint, uint> knownLevels = [];

    /// <summary>
    /// 已通關過的副本（ContentFinderCondition row id）；載入時從設定灌進來。
    /// </summary>
    /// <remarks>
    /// 📌 設定檔存 <see cref="System.Collections.Generic.List{T}"/>（JSON 好讀好手改），
    /// 執行期用 HashSet 比對——副本完成是低頻事件，但線性搜尋一份會長到幾百筆的清單沒有必要。
    /// </remarks>
    private readonly HashSet<uint> clearedDuties;

    /// <summary>ContentFinderCondition 反查壞掉過就不再試，也只寫一次 log。</summary>
    private bool contentLookupBroken;

    /// <summary>警示線的輪詢間隔。血量與周圍敵人不需要每幀掃，4 Hz 對「來得及喊一聲」綽綽有餘。</summary>
    private static readonly TimeSpan AlertPollInterval = TimeSpan.FromMilliseconds(250);

    private DateTime lastAlertPollUtc = DateTime.MinValue;

    /// <summary>
    /// 血量警示的遲滯：血量回到門檻以上才重新「上膛」。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有遲滯的話，血量在門檻附近抖動時每一次輪詢都會觸發一次，冷卻一到就再喊一次。
    /// 上膛條件刻意是「回到門檻以上」而不是「離開戰鬥」——一場戰鬥裡掉血兩次是該喊兩次的。
    /// </remarks>
    private bool lowHpArmed = true;

    /// <summary>
    /// 上一次輪詢時，各敵對玩家離我多遠（<c>GameObjectId</c> → 距離）。
    /// </summary>
    /// <remarks>
    /// 🔴 只存 <b>id 與純量</b>。原生指標與 <see cref="Dalamud.Game.ClientState.Objects.Types.IGameObject"/>
    /// 包裝一律不跨幀保存：本 pin 的 ObjectTable 是每格重用同一個包裝、存取時就地改寫 Address，
    /// 跨幀持有會靜默換人或懸空。
    /// </remarks>
    private readonly Dictionary<ulong, float> lastEnemyDistance = [];

    /// <summary>這一輪掃描用的暫存（每次輪詢重用，不要每 250ms 配一個新字典）。</summary>
    private readonly Dictionary<ulong, float> enemyDistanceScratch = [];

    private DateTime lastGilPollUtc = DateTime.MinValue;

    /// <summary>上一次讀到的 Gil。<c>-1</c>＝還沒讀到過（登入後第一次讀不算里程碑）。</summary>
    private long lastGil = -1;

    /// <summary>Gil 讀取失敗過就不再重試，也只寫一次 log（避免每 5 秒洗一行）。</summary>
    private bool gilReadBroken;

    /// <summary>
    /// 組隊邀請彈窗的 addon 名。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不可以用 <c>SelectYesno</c></b>：那是通用的是／否對話框，
    /// 從「要不要丟棄道具」到「要不要退出副本」都用它——拿它當判準會變成什麼都喊一聲。
    /// <para>
    /// 📌 <b>名字的離線證據</b>（2026-08-29 掃台服 <c>ffxiv_dx11.exe</c>，先用
    /// <c>SelectYesno</c>／<c>Trade</c>／<c>_PartyList</c> 等一定存在的名字校準過掃描）：
    /// 執行檔裡<b>沒有</b> <c>PartyInvite</c>、<c>InviteReply</c>、<c>_PartyInvite</c> 這些名字；
    /// 「有人要你答應一件事」的彈窗全部是 <c>_Notification*</c> 這一族——
    /// <c>_NotificationFriend</c>（好友邀請）、<c>_NotificationFcJoin</c>（部隊邀請）、
    /// <c>_NotificationLinkShell</c>（通訊貝邀請）、<c>_NotificationReadyCheck</c>（準備確認）…
    /// 其中對應組隊的就是 <c>_NotificationParty</c>。
    /// </para>
    /// <para>
    /// 📌 更硬的證據（2026-08-29 反組譯）：通知彈窗是一張<b>指標陣列</b>（基底 <c>0x142123DA0</c>、
    /// 每格 8 bytes），長度 34 是從關閉函式 <c>0x14146CC20</c> 開頭的界限檢查 <c>cmp edi, 0x22</c>
    /// <b>讀出來</b>的（不是數出來的）；<c>_NotificationParty</c> 是<b>第 12 格</b>
    /// （<c>0x142123E00</c> → <c>0x1421149A8</c>，全檔僅 1 份）。34 種共用同一個 vtable
    /// <c>0x142123B50</c> 與同一份 <c>Notification.uld</c>，但<b>各自有獨立的 AtkUnitBase 名字</b>，
    /// 所以用名字分辨得出來。旁證：TCToolbox 已在用同表第 24 格的 <c>_NotificationCircleBook</c>；
    /// 台服 <c>Addon.csv</c> 第 170~172 列正是連號的「入隊邀請／好友申請／通訊貝邀請」。
    /// </para>
    /// <para>
    /// 🔴 <c>SelectYesno</c> 不是這個彈窗，是<b>按下彈窗按鈕之後</b>才出現的第二層
    /// （<c>Addon#120</c> 加入／<c>Addon#121</c>「確定要拒絕…發來的組隊邀請嗎？」）。
    /// </para>
    /// <para>
    /// ⚠️ <b>「第 12 格就是收到組隊邀請的那一個」這最後一環還沒有實機驗過</b>——型別 12 的寫入端
    /// 離線追不到。不成立的話是<b>在錯的時機響或永遠不響</b>，不會崩潰。沒響的時候第一件事是
    /// 確認這個字串，不是去查冷卻或機率——啟動時的 <c>Information</c> 記錄有印出來。
    /// </para>
    /// </remarks>
    internal const string PartyInviteAddon = "_NotificationParty";

    /// <summary>
    /// 交易視窗的 addon 名。
    /// </summary>
    /// <remarks>
    /// 📌 <c>Trade</c> 是主 addon 定義表（<c>0x141FE7160</c>、24-byte stride、931 筆）的第 44 筆，
    /// 而且 <c>ui/uld/Trade.uld</c> 在台服 sqpack 裡確實存在；艦隊裡 Lifestream 與 AutoRetainer
    /// 也都是用 <c>"Trade"</c> 去抓交易視窗的，所以這個名字是確定的。
    /// <para>
    /// 🔴 <b>整顆客戶端沒有「交易請求」專用彈窗</b>：34 個 <c>_Notification*</c> 裡沒有交易，
    /// 931 筆 addon 表裡也沒有 <c>TradeRequest</c> 之類的東西，台服 <c>Addon.csv</c> 的交易叢集
    /// （#201~#208）全是介面標籤、沒有任何「要接受交易申請嗎？」的提示文字。
    /// ⇒ 收到請求時直接開的就是 <c>Trade</c> 視窗本身（拒絕＝按「中止交易」）。
    /// </para>
    /// <para>
    /// ⚠️ <b>不要跟 <c>TradeMultiple</c> 搞混</b>：那是對 NPC 一次交多個道具的對話框（AgentId 149），
    /// 跟玩家間交易無關；YesAlready 設定裡的 <c>TradeMultiple</c> 講的也是它。
    /// </para>
    /// <para>
    /// ⚠️ <b>這個視窗在「自己主動發起交易」時也會被建出來</b>，所以那時候也會喊一聲。
    /// 要分辨得去讀視窗內容，那是為了一個提示音不值得的複雜度。
    /// </para>
    /// </remarks>
    internal const string TradeAddon = "Trade";

    private bool disposed;

    public TriggerWatcher(Configuration config, PraiseService service)
    {
        this.config = config;
        this.service = service;
        clearedDuties = new HashSet<uint>(config.ClearedDuties);

        Svc.DutyState.DutyCompleted += OnDutyCompleted;
        Svc.ClientState.LevelChanged += OnLevelChanged;
        Svc.ClientState.Login += OnLogin;
        Svc.ClientState.Logout += OnLogout;
        Svc.ClientState.CfPop += OnCfPop;
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Svc.DutyState.DutyCompleted -= OnDutyCompleted;
        Svc.ClientState.LevelChanged -= OnLevelChanged;
        Svc.ClientState.Login -= OnLogin;
        Svc.ClientState.Logout -= OnLogout;
        Svc.ClientState.CfPop -= OnCfPop;
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.Framework.Update -= OnFrameworkUpdate;
    }

    /// <summary>
    /// 收到密語。
    /// </summary>
    /// <remarks>
    /// 🔴 判準<b>只有 <see cref="XivChatType.TellIncoming"/> 這個列舉值</b>，一個字都不比對。
    /// 台服的中文字面沒辦法離線確定，比對文字一定錯而且錯法是靜默的。
    /// <para>
    /// 📌 自己送出去的密語是 <see cref="XivChatType.TellOutgoing"/>（12），
    /// 跟 <see cref="XivChatType.TellIncoming"/>（13）是不同的值，所以不會自己喊自己。
    /// </para>
    /// <para>
    /// 🔴 <paramref name="isHandled"/> <b>一個字都不能動</b>：那是給「要不要把這則訊息吃掉」用的，
    /// 我們只是旁聽。動了它會讓別的外掛（或聊天視窗本身）收不到訊息，而且完全不會有人知道是我們幹的。
    /// </para>
    /// <para>
    /// ⚠️ 這條線<b>不擲觸發機率骰</b>（等同 100%），跟警示同一個理由：
    /// 30% 機率才響一次的通知比沒有還糟——使用者會學會不相信它。冷卻照走（內建 5 秒）。
    /// </para>
    /// </remarks>
    private void OnChatMessage(
        XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (!config.TriggerTellReceived) return;
        if (type != XivChatType.TellIncoming) return;

        service.TryTrigger(PraiseCategory.TellReceived, chanceOverride: 100);
    }

    /// <summary>
    /// 副本配對排到。
    /// </summary>
    /// <remarks>
    /// 📌 情境鍵用的是既有的「副本排到」——NotificationMaster 之類的外部呼叫端也是叫這個鍵。
    /// 兩路並存是刻意的：<b>逐情境冷卻（內建 5 秒）會把緊接著的第二次吸掉</b>，不會聽到兩聲。
    /// <para>
    /// 📌 參數是 <c>ContentFinderCondition</c>，這裡<b>用不到</b>——只是「排到了」這件事本身。
    /// 不去讀它的欄位也就不必擔心台服的表布局。
    /// </para>
    /// </remarks>
    private void OnCfPop(ContentFinderCondition condition)
    {
        if (!config.TriggerDutyPop) return;

        service.TryTrigger(PraiseCategory.DutyPop, chanceOverride: 100);
    }

    /// <summary>上一輪輪詢時，組隊邀請彈窗是不是可見的。</summary>
    /// <remarks>🔴 只存 <see cref="bool"/>。原生指標一律當輪解析、當輪丟棄。</remarks>
    private bool partyInviteVisible;

    /// <summary>上一輪輪詢時，交易視窗是不是可見的。</summary>
    private bool tradeVisible;

    /// <summary>
    /// 彈窗類的通知：用「看不見 → 看得見」的<b>邊緣</b>觸發，不用 addon 生命週期事件。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意不用 <c>IAddonLifecycle</c> 的 <c>PostSetup</c>。</b>
    /// 台服客戶端顯示通知彈窗的路徑（<c>0x14146E500</c>）是<b>先用名字查有沒有既存的、查到就重用</b>，
    /// 只有查不到才配置新的。而關閉走的是 <c>AtkUnitBase::Close(false)</c>——它到底有沒有真的釋放掉，
    /// 離線判不出來。如果沒有釋放，<c>PostSetup</c> 就<b>只有登入後第一次邀請會觸發</b>，
    /// 之後每一次都靜默無反應，而且看起來會像「冷卻沒到」或「機率沒中」，完全誤導。
    /// <para>
    /// 📌 旁證：TCToolbox 對同一族的 <c>_NotificationCircleBook</c> 用的是 <c>PreDraw</c> 而不是
    /// <c>PostSetup</c>（<c>Modules/AutoHideNeedlessPopups.cs</c>），它的上游 DailyRoutines 也是——
    /// 很可能正是為了繞開同一件事。
    /// </para>
    /// <para>
    /// 🔴 改成輪詢可見性之後，「重用還是重建」這個問題<b>整個不存在</b>了：不管客戶端在底下怎麼搞，
    /// 「本來看不見、現在看得見」就是一次新的彈窗。代價是最多 250ms 的延遲，對提示音無所謂。
    /// </para>
    /// <para>
    /// 🔴 <b>只讀 <c>IsVisible</c>，不碰視窗裡的任何東西</b>——不按同意、不按拒絕、不讀欄位。
    /// <see cref="Dalamud.Game.NativeWrapper.AtkUnitBasePtr"/> 是當輪解析、當輪用完就丟的值型別包裝，
    /// <b>不跨輪保存</b>；跨輪留下來的只有上面那兩個 <see cref="bool"/>。
    /// </para>
    /// </remarks>
    private void PollPopups()
    {
        if (config.TriggerPartyInvite)
        {
            var visible = IsAddonVisible(PartyInviteAddon);
            if (visible && !partyInviteVisible)
                service.TryTrigger(PraiseCategory.PartyInvite, chanceOverride: 100);
            partyInviteVisible = visible;
        }
        else
        {
            partyInviteVisible = false;
        }

        if (config.TriggerTradeRequest)
        {
            var visible = IsAddonVisible(TradeAddon);
            if (visible && !tradeVisible)
                service.TryTrigger(PraiseCategory.TradeRequest, chanceOverride: 100);
            tradeVisible = visible;
        }
        else
        {
            tradeVisible = false;
        }
    }

    /// <summary>某個 addon 現在存在而且看得見嗎。</summary>
    /// <remarks>
    /// 📌 <see cref="Dalamud.Game.NativeWrapper.AtkUnitBasePtr.IsVisible"/> 自己會判 null，
    /// 所以名字不存在時就是回 <c>false</c>，不需要 <c>unsafe</c>、也不會解參考空指標。
    /// </remarks>
    private static bool IsAddonVisible(string name) => Svc.GameGui.GetAddonByName(name).IsVisible;

    /// <summary>
    /// 副本完成。<b>首次通關</b>走另一個機率。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>DutyCompleted</c> 給的是 <b>territory id</b>（Dalamud 直接轉發 <c>ClientState.TerritoryType</c>），
    /// 不是 ContentFinderCondition 的 id。要判「這個副本我通關過沒有」必須先反查：
    /// <c>TerritoryType</c> 表的 <c>ContentFinderCondition</c> 欄就是那個 RowRef。
    /// <para>
    /// 📌 台服 7.20 的資料離線對過（<c>exd-tc/7.20</c>）：territory 1039 → CFC 1「監獄廢墟托托·拉克千獄」，
    /// 而 CFC 1 的 <c>TerritoryType</c> 欄也回 1039，雙向一致。非副本場景（例如 territory 128 利姆薩）
    /// 的 <c>ContentFinderCondition</c> 是 0。
    /// </para>
    /// <para>
    /// 🔴 反查不到（回 0、或表根本讀不到）就<b>照一般副本處理</b>：走原機率、也不記進已通關集合。
    /// 查不到要走得下去，不可以崩、也不可以誤判成首通而每次都用 100%。
    /// </para>
    /// <para>
    /// ⚠️ 「已通關」是<b>這個外掛自己記的</b>，不是遊戲的通關紀錄——老角色第一次跑舊副本照樣算首通。
    /// 設定視窗的 tooltip 有寫清楚。
    /// </para>
    /// </remarks>
    private void OnDutyCompleted(object? sender, ushort territoryType)
    {
        if (!config.TriggerDutyComplete) return;

        var contentId = ResolveContentFinderCondition(territoryType, out var contentName);
        if (contentId == 0)
        {
            Svc.Log.Information(
                $"[TataruPraise] 副本完成：territory {territoryType} 反查不到 ContentFinderCondition，照一般副本處理。");
            service.TryTrigger(PraiseCategory.DutyComplete);
            return;
        }

        // 🔴 先記「通關過了」再擲骰：這件事跟有沒有出聲無關，機率沒中也不能讓它下次又算首通。
        var firstClear = clearedDuties.Add(contentId);
        if (firstClear)
        {
            config.ClearedDuties.Add(contentId);
            config.Save();
        }

        var chance = firstClear
            ? Math.Clamp(config.FirstClearChancePercent, 0, 100)
            : Math.Clamp(config.ChancePercent, 0, 100);

        var label = contentName.Length > 0 ? contentName : $"CFC {contentId}";
        Svc.Log.Information(
            $"[TataruPraise] 副本完成：{label}（CFC {contentId}）"
            + $"，{(firstClear ? "首次通關" : "先前通關過")}，這次用 {chance}% 機率。");

        service.TryTrigger(PraiseCategory.DutyComplete, chance);
    }

    /// <summary>
    /// territory id → ContentFinderCondition row id。查不到回 0。
    /// </summary>
    /// <remarks>
    /// 📌 純 Lumina 查表，沒有原生指標、沒有跨幀狀態，這裡的 <c>try/catch</c> 是有效的
    /// （表不存在／欄位對不上時 Lumina 擲的是一般的受控例外）。
    /// <para>
    /// 🔴 失敗只發生一次就永久停用反查（<see cref="contentLookupBroken"/>）：每次通關都重試一次
    /// 只會洗掉實機記錄檔，而記錄檔是事後唯一的診斷來源。停用之後所有副本都走一般機率。
    /// </para>
    /// </remarks>
    private uint ResolveContentFinderCondition(ushort territoryType, out string name)
    {
        name = string.Empty;
        if (contentLookupBroken) return 0;

        try
        {
            var row = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryType);
            if (row == null) return 0;

            var reference = row.Value.ContentFinderCondition;
            if (reference.RowId == 0) return 0;

            name = reference.ValueNullable?.Name.ExtractText() ?? string.Empty;
            return reference.RowId;
        }
        catch (Exception ex)
        {
            contentLookupBroken = true;
            Svc.Log.Information(
                $"[TataruPraise] 反查 ContentFinderCondition 失敗，首次通關加權已停用（重載外掛才會再試）：{ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 升等。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只有「這個職業之前就有紀錄，而且新等級比舊的高」才算升等。</b>
    /// 理由有兩個，兩個都會讓使用者在完全沒升等的時候聽到誇獎：
    /// <list type="number">
    /// <item>登入時客戶端會把目前職業的等級「回報」一次，那不是升等。</item>
    /// <item>切職業（<c>ClassJobChanged</c> 之後）會回報新職業的等級，那也不是升等，
    /// 而且從 90 級切到 1 級的職業時新值還比舊值小。</item>
    /// </list>
    /// 第一次看到某個職業就只記下來、不出聲——代價是「登入後第一次升等」也不會漏，因為那時候已經有紀錄了。
    /// <para>⚠️ 這兩件事都是<b>離線推理</b>，實機上 <c>LevelChanged</c> 到底在登入時會不會發、發幾次，
    /// 我沒有辦法在這裡證明。上面的寫法讓「會發」與「不會發」兩種情況都不會誤觸。</para>
    /// <para>
    /// 🔴 <b>等級壓制（副本／大型任務把等級鎖低）也會經過這個事件，而且是雙向的誤觸來源。</b>
    /// 進入等級壓制的內容：真實等級（例如 90）被壓成內容上限（例如 50），50 &lt; 90，
    /// 靠上面「新值要比舊值高」那條就已經擋掉了，不需要特別處理。
    /// 但<b>離開</b>時等級從壓制值（50）恢復回真實等級（90），90 &gt; 50，會被誤判成升等——
    /// 明明只是恢復原本的等級，卻喊了一句「升等」。用 <see cref="PlayerState.IsLevelSynced"/>
    /// 判斷：事件發生當下若還處於等級壓制狀態，代表這次變動是壓制／恢復的一部分，不記錄也不觸發，
    /// 讓 <see cref="knownLevels"/> 繼續保留壓制前的真實等級，離開時的那次回報自然對不上「更高」。
    /// </para>
    /// </remarks>
    private unsafe void OnLevelChanged(uint classJobId, uint level)
    {
        if (PlayerState.Instance()->IsLevelSynced)
        {
            return;
        }

        var hadPrevious = knownLevels.TryGetValue(classJobId, out var previous);
        knownLevels[classJobId] = level;

        if (!config.TriggerLevelUp) return;
        if (!hadPrevious || level <= previous) return;

        service.TryTrigger(PraiseCategory.LevelUp);
    }

    private void OnLogin()
    {
        // 🔴 每次登入都要清乾淨：換角色時舊角色的等級表留著，會讓新角色的第一次等級回報被當成升等。
        knownLevels.Clear();
        lastGil = -1;
        lowHpArmed = true;
        lastEnemyDistance.Clear();

        if (!config.TriggerLogin) return;

        if (!config.LoginOncePerDay)
        {
            service.TryTrigger(PraiseCategory.Login);
            return;
        }

        // 🔴 日期戳只在「真的出聲了」之後才寫。冷卻擋掉、機率沒中、池裡沒有已合成的句子——
        //    這些都不算今天誇過了，不然一次沒中就整天都不會有。
        var today = Configuration.TodayStamp();
        if (string.Equals(config.LastLoginPraiseDate, today, StringComparison.Ordinal))
        {
            Svc.Log.Information($"[TataruPraise] 登入誇獎：今天（{today}）已經誇過了，這次略過。");
            return;
        }

        if (!service.TryTrigger(PraiseCategory.Login)) return;

        config.LastLoginPraiseDate = today;
        config.Save();
    }

    private void OnLogout(int type, int code)
    {
        knownLevels.Clear();
        lastGil = -1;
        lowHpArmed = true;
        lastEnemyDistance.Clear();

        // 🔴 彈窗的可見性基準也要清掉：不清的話，登出時剛好開著交易視窗，
        //    下次登入第一輪會看到「看不見」，再開才觸發——但反過來留著 true 更糟。
        partyInviteVisible = false;
        tradeVisible = false;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now - lastAlertPollUtc >= AlertPollInterval)
        {
            lastAlertPollUtc = now;
            PollAlerts();
            PollPopups();
        }

        if (!config.TriggerGilMilestone) return;
        if (gilReadBroken) return;
        if (!Svc.ClientState.IsLoggedIn)
        {
            lastGil = -1;
            return;
        }

        if (now - lastGilPollUtc < GilPollInterval) return;
        lastGilPollUtc = now;

        var gil = ReadGil();
        if (gil < 0) return;

        var step = config.GilMilestoneStep;
        if (step <= 0) return;

        if (lastGil < 0)
        {
            // 登入後第一次讀到：只當基準，不觸發。否則每次上線都會誇一次。
            lastGil = gil;
            return;
        }

        var previousMilestone = lastGil / step;
        var currentMilestone = gil / step;
        lastGil = gil;

        if (currentMilestone > previousMilestone)
            service.TryTrigger(PraiseCategory.GilMilestone);
    }

    /// <summary>
    /// 讀目前的 Gil。讀不到回 <c>-1</c>。
    /// </summary>
    /// <remarks>
    /// 走 <c>InventoryManager.Instance()-&gt;GetGil()</c>。
    /// <para>
    /// 📌 兩條特徵碼都在台服 7.20 客戶端上離線驗過（2026-08-28，<c>tools/sigscan/verify_cs_sigs.py</c>，
    /// 校準閘門全 PASS）：
    /// <c>InventoryManager.Instance()</c> 的 <c>[StaticAddress("48 8D 0D ?? ?? ?? ?? 81 C2", 3)]</c>
    /// 唯一命中（<c>lea</c> 取靜態位址，<b>不是</b>指標解參，所以永遠不會是 null）；
    /// <c>GetGil()</c> 的 <c>[MemberFunction("E8 ?? ?? ?? ?? 3B 44 24 58")]</c> 也是唯一命中。
    /// </para>
    /// <para>
    /// ⚠️ 特徵碼失配時 FFXIVClientStructs 會擲 <c>InvalidOperationException</c>（在受控框架裡攔得到），
    /// 所以這裡的 <c>try/catch</c> 對「台服改版後 sig 死掉」是有效的防護。
    /// <b>但它擋不住 AccessViolation</b>——那在 .NET Core 是 corrupted-state exception，攔不到。
    /// 這也是為什麼要先離線驗特徵碼，而不是靠 try/catch 兜底。
    /// </para>
    /// <para>
    /// 🔴 失敗只發生一次就永久停用這條線（<see cref="gilReadBroken"/>）：每 5 秒重試一次沒有意義，
    /// 只會把實機 log 洗掉，而 log 是事後唯一的診斷來源。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 三條戰鬥警示線：血量低、被大量標記、背後有人。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>純讀狀態、純出聲，不做任何遊戲操作</b>——不施放、不走位、不改目標。
    /// <para>
    /// 🔴 整段只在 <see cref="OnFrameworkUpdate"/> 裡跑，物件包裝<b>當幀用完即丟</b>，
    /// 只把 <c>GameObjectId</c>（ulong）與距離（float）留到下一輪。
    /// </para>
    /// <para>
    /// ⚠️ 後兩條只在 PvP 區域跑（<see cref="Dalamud.Plugin.Services.IClientState.IsPvP"/>）：
    /// PvE 的敵人不是玩家，「被幾個人標記」與「有人繞後」在那裡沒有意義，而且會白掃物件表。
    /// </para>
    /// </remarks>
    private void PollAlerts()
    {
        if (!config.TriggerLowHp && !config.TriggerMarkedByMany && !config.TriggerEnemyBehind)
        {
            if (lastEnemyDistance.Count > 0) lastEnemyDistance.Clear();
            return;
        }

        // 📌 走 IObjectTable.LocalPlayer：IClientState.LocalPlayer 在本 pin 已標記過時。
        var me = Svc.Objects.LocalPlayer;
        if (me == null)
        {
            lowHpArmed = true;
            lastEnemyDistance.Clear();
            return;
        }

        // 🔴 純量在這裡一次抄乾淨，後面完全不再碰 me（包裝不跨迴圈用）。
        var myId = me.GameObjectId;
        var myPosition = me.Position;
        var myRotation = me.Rotation;
        var currentHp = me.CurrentHp;
        var maxHp = me.MaxHp;

        if (config.TriggerLowHp) PollLowHp(currentHp, maxHp);

        if (!Svc.ClientState.IsPvP)
        {
            // 離開 PvP 就把上一輪的距離忘掉：留著會讓下次進場的第一輪誤判成「正在接近」。
            if (lastEnemyDistance.Count > 0) lastEnemyDistance.Clear();
            return;
        }

        if (!config.TriggerMarkedByMany && !config.TriggerEnemyBehind) return;
        PollHostilePlayers(myId, myPosition, myRotation);
    }

    /// <summary>
    /// 血量低。
    /// </summary>
    /// <remarks>
    /// 判定式：<c>InCombat</c> 且 <c>CurrentHp * 100 / MaxHp &lt; 門檻</c>，而且上一次觸發之後
    /// 血量曾經回到門檻以上（<see cref="lowHpArmed"/>）。
    /// <para>📌 用整數百分比比較，不用浮點——避免「29.999% 算不算跌破」這種邊界問題。</para>
    /// </remarks>
    private void PollLowHp(uint currentHp, uint maxHp)
    {
        if (maxHp == 0) return;

        var percent = (int)(currentHp * 100UL / maxHp);
        var threshold = Math.Clamp(config.LowHpThresholdPercent, 1, 99);

        if (percent >= threshold)
        {
            lowHpArmed = true;
            return;
        }

        if (!lowHpArmed) return;
        if (currentHp == 0) return;
        if (!Svc.Condition[ConditionFlag.InCombat]) return;

        lowHpArmed = false;
        Svc.Log.Information($"[TataruPraise] 警示：血量 {percent}%（門檻 {threshold}%）。");

        // 🔴 警示不擲骰：30% 機率的警示比沒有還糟（該喊的時候不喊）。冷卻照走。
        service.TryTrigger(PraiseCategory.LowHp, 100);
    }

    /// <summary>
    /// 掃一次物件表，同時算「被幾個敵對玩家鎖定」與「幾個敵對玩家從背後接近」。
    /// </summary>
    /// <remarks>
    /// 🔴 兩條線<b>共用同一次掃描</b>：物件表最多 200 格，每 250ms 掃兩次沒有必要。
    /// <para>
    /// 敵對判定＝<see cref="Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Hostile"/>
    /// （Dalamud 由 <c>Character.IsHostile</c> 轉出來的）。⚠️ 這個旗標在台服 PvP 裡對敵方玩家是不是
    /// 一定會亮，<b>離線證不了</b>——證不了的後果是「這兩條線不觸發」，不是崩潰。
    /// </para>
    /// <para>
    /// 「從背後接近」的判定式（三個條件同時成立）：
    /// <list type="number">
    /// <item>在背後：以我的 <c>Rotation</c> 為前方，前方向量 <c>(sin r, cos r)</c> 與
    /// 「我→他」的水平向量內積 &lt; 0，等價於夾角大於 90°。</item>
    /// <item>夠近：水平距離 ≤ 設定的碼數。</item>
    /// <item>正在接近：這一輪的距離比<b>上一輪</b>小（至少 0.05 碼，濾掉抖動）。
    /// 上一輪沒看過這個 id 就<b>不算</b>——剛進視野的第一輪沒有比較基準。</item>
    /// </list>
    /// </para>
    /// </remarks>
    private void PollHostilePlayers(ulong myId, Vector3 myPosition, float myRotation)
    {
        var forwardX = MathF.Sin(myRotation);
        var forwardZ = MathF.Cos(myRotation);

        var range = Math.Clamp(config.EnemyBehindRange, 1f, 50f);
        var markedCount = 0;
        var behindCount = 0;

        enemyDistanceScratch.Clear();

        foreach (var obj in Svc.Objects)
        {
            // 🔴 obj 只在這一圈裡用，絕不留到下一幀。
            if (obj is not IPlayerCharacter player) continue;

            var id = player.GameObjectId;
            if (id == myId) continue;
            if ((player.StatusFlags & StatusFlags.Hostile) == 0) continue;

            if (player.TargetObjectId == myId) markedCount++;

            var dx = player.Position.X - myPosition.X;
            var dz = player.Position.Z - myPosition.Z;
            var distance = MathF.Sqrt((dx * dx) + (dz * dz));
            enemyDistanceScratch[id] = distance;

            if (!config.TriggerEnemyBehind) continue;
            if (distance > range) continue;

            // 內積 < 0 ＝ 他在我的後半平面。
            if ((dx * forwardX) + (dz * forwardZ) >= 0f) continue;
            if (!lastEnemyDistance.TryGetValue(id, out var previous)) continue;
            if (distance >= previous - 0.05f) continue;

            behindCount++;
        }

        lastEnemyDistance.Clear();
        foreach (var (id, distance) in enemyDistanceScratch) lastEnemyDistance[id] = distance;

        if (config.TriggerMarkedByMany)
        {
            var need = Math.Max(1, config.MarkedByManyCount);
            if (markedCount >= need)
            {
                Svc.Log.Information($"[TataruPraise] 警示：{markedCount} 個敵對玩家鎖定我（門檻 {need}）。");
                service.TryTrigger(PraiseCategory.MarkedByMany, 100);
            }
        }

        if (!config.TriggerEnemyBehind) return;

        var behindNeed = Math.Max(1, config.EnemyBehindCount);
        if (behindCount < behindNeed) return;

        Svc.Log.Information(
            $"[TataruPraise] 警示：{behindCount} 個敵對玩家從背後接近（門檻 {behindNeed}、{range:F0} 碼內）。");
        service.TryTrigger(PraiseCategory.EnemyBehind, 100);
    }

    private unsafe long ReadGil()
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return -1;
            return manager->GetGil();
        }
        catch (Exception ex)
        {
            gilReadBroken = true;
            Svc.Log.Information(
                $"[TataruPraise] 讀取 Gil 失敗，Gil 里程碑觸發已停用（重載外掛才會再試）：{ex.Message}");
            return -1;
        }
    }
}
