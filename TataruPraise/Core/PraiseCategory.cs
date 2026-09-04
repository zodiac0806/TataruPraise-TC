using System;
using System.Collections.Generic;

namespace TataruPraise.Core;

/// <summary>
/// 誇獎池的情境分類。
/// </summary>
/// <remarks>
/// 🔴 這些字串同時是 <c>pool.json</c> 的鍵、IPC <c>TataruPraise.Praise(category)</c> 的參數，
/// 以及 Gemini 生句時餵進去的情境描述來源——<b>改字面等於把既有使用者的整池對不上</b>。
/// <para>
/// 🔴 <see cref="Submarine"/>、<see cref="Crafting"/>、<see cref="Cosmic"/> 這三個是給<b>別的外掛</b>
/// 透過 IPC 呼叫用的（AutoRetainer 潛艇回港、Artisan 清單製作完成、ICE 宇宙探索金評）。
/// 呼叫端是拿字面字串來叫的，<b>鍵名逐字固定，一個字都不能改</b>。
/// </para>
/// <para>
/// 📌 <see cref="PraisePool"/> 讀檔時<b>不會丟掉不認得的鍵</b>：使用者自己在設定視窗加的情境、
/// 或手動寫進 pool.json 的鍵，存檔時會原樣寫回去。
/// </para>
/// </remarks>
public static class PraiseCategory
{
    public const string DutyComplete = "副本完成";
    public const string LevelUp = "升等";
    public const string Login = "登入";
    public const string GilMilestone = "Gil里程碑";

    /// <summary>AutoRetainer：潛水艇整隊回港／僱員探險全部收完。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Submarine = "潛艇";

    /// <summary>
    /// AutoRetainer：僱員探險完成。<b>鍵名由 IPC 呼叫端逐字使用。</b>
    /// </summary>
    /// <remarks>
    /// 📌 跟 <see cref="Submarine"/> 是<b>兩個不同的鍵</b>，刻意的：潛艇回港是「整隊都回來了」，
    /// 僱員探險是頻率高得多的日常事件。合成一個鍵的話，想只聽潛艇的人沒辦法單獨關掉僱員。
    /// </remarks>
    public const string Retainer = "僱員";

    /// <summary>
    /// AutoRetainer：稀有品繳交循環把所有角色都跑完了。<b>鍵名由 IPC 呼叫端逐字使用。</b>
    /// </summary>
    /// <remarks>
    /// 📌 跟 <see cref="Retainer"/> 是<b>兩個不同的鍵</b>，刻意的：僱員探險是<b>單一角色</b>的日常事件，
    /// 稀有品繳交是<b>整輪多角色跑完</b>的收尾。合成一個鍵的話，只想知道「整輪結束了」的人會被每個角色各響一次洗版。
    /// </remarks>
    public const string ExpertDelivery = "稀有品";

    /// <summary>
    /// Marketbuddy：市場重掛（重新上架）把整輪跑完了。<b>鍵名由 IPC 呼叫端逐字使用。</b>
    /// </summary>
    /// <remarks>
    /// 📌 這個鍵沒有內建觸發，只由 Marketbuddy 用 IPC 叫，而且是<b>整輪重掛跑完</b>才響一次，
    /// 不是每件商品各響一次——後者在幾十件商品的攤位上會變成洗版。
    /// </remarks>
    public const string Market = "市場";

    /// <summary>Artisan：整份製作清單做完。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Crafting = "製作";

    /// <summary>ICE：宇宙探索任務拿到金評價。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Cosmic = "宇宙";

    /// <summary>戰鬥警示：自己的血量掉到門檻以下。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string LowHp = "血量低";

    /// <summary>戰鬥警示：同時被多個敵對玩家鎖定（PvP）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string MarkedByMany = "被大量敵人標記";

    /// <summary>戰鬥警示：有敵對玩家從背後接近（PvP）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string EnemyBehind = "敵人從後面來";

    /// <summary>提醒：任務／戰鬥開始（NotificationMaster 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string DutyStart = "任務開始";

    /// <summary>提醒：出現準備確認（NotificationMaster 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string ReadyCheck = "準備確認";

    /// <summary>提醒：過場動畫結束（NotificationMaster 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string CutsceneEnd = "過場結束";

    /// <summary>通知：副本排到。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string DutyPop = "副本排到";

    /// <summary>通知：到旗標。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string FlagArrived = "到旗標";

    /// <summary>通知：私訊。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Tell = "私訊";

    /// <summary>通知：抵達。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Arrived = "抵達";

    /// <summary>通知：中獎。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Jackpot = "中獎";

    /// <summary>通知：需要幫忙。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string NeedHelp = "需要幫忙";

    /// <summary>通知：玩家警示。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string PlayerAlert = "玩家警示";

    /// <summary>通知：被盯著（PeepingTom：有人把我設成目標）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string BeingWatched = "被盯著";

    /// <summary>
    /// 通知：收到密語（內建觸發，看 <c>XivChatType.TellIncoming</c>）。
    /// <b>鍵名由 IPC 呼叫端逐字使用。</b>
    /// </summary>
    /// <remarks>
    /// ⚠️ 這個跟 <see cref="Tell"/>（「私訊」）是<b>兩個不同的鍵</b>，刻意的：
    /// 「私訊」沒有內建觸發、留給 NotificationMaster 之類的外部呼叫端；
    /// 「被密語」是這個外掛<b>自己</b>訂閱聊天事件觸發的。兩邊同時裝的話會聽到兩次，
    /// 這是使用者可以自己在觸發分頁關掉一邊的事——把兩者合併反而會讓外部呼叫端無法單獨關閉。
    /// </remarks>
    public const string TellReceived = "被密語";

    /// <summary>通知：收到組隊邀請（內建觸發，看邀請彈窗 addon 出現）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string PartyInvite = "組隊邀請";

    /// <summary>通知：收到交易請求（內建觸發，看交易視窗 addon 出現）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string TradeRequest = "交易請求";

    /// <summary>通知：自動跑本停下來（AutoDuty 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    /// <remarks>
    /// 📌 「停下來」<b>不等於「跑完了」</b>：正常結束與中途卡住都會走這個鍵。
    /// 想知道「是不是出事了」請聽 <see cref="NeedHelp"/>，那是呼叫端明確判定成卡住才叫的。
    /// </remarks>
    public const string DutyRunStopped = "跑本停止";

    /// <summary>通知：自動採集停下來（GatherbuddyReborn 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string GatherStopped = "採集停止";

    /// <summary>通知：釣到稀有魚（AutoHook 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string RareFish = "稀有魚";

    /// <summary>通知：附近出現 A／B／S 級魔物（HuntHelper 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string HuntFound = "發現魔物";

    /// <summary>通知：背包快滿了（InventoryTools 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string BagAlmostFull = "背包快滿";

    /// <summary>通知：每日重置（DailyDuty 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string DailyReset = "每日重置";

    /// <summary>
    /// 內建情境，順序即 UI 上的顯示順序。
    /// </summary>
    /// <remarks>
    /// 🔴 內建情境<b>不可以在設定視窗刪掉</b>（刪了 <see cref="PraisePool.Load"/> 下次啟動又會補回來，
    /// 對使用者是「刪不掉」的鬼打牆）。自訂情境才有刪除鈕。
    /// <para>
    /// 📌 有些情境有內建的遊戲事件觸發來源（見設定視窗的「觸發」分頁與 README 的情境表），
    /// 其餘的沒有，靠別的外掛用 IPC 叫。
    /// </para>
    /// </remarks>
    public static readonly string[] All =
    [
        DutyComplete,
        LevelUp,
        Login,
        GilMilestone,
        Submarine,
        Retainer,
        ExpertDelivery,
        Market,
        Crafting,
        Cosmic,
        LowHp,
        MarkedByMany,
        EnemyBehind,
        DutyStart,
        ReadyCheck,
        CutsceneEnd,
        DutyPop,
        FlagArrived,
        Tell,
        Arrived,
        Jackpot,
        NeedHelp,
        PlayerAlert,
        BeingWatched,
        TellReceived,
        PartyInvite,
        TradeRequest,
        DutyRunStopped,
        GatherStopped,
        RareFish,
        HuntFound,
        BagAlmostFull,
        DailyReset,
    ];

    /// <summary>內建情境的預設「情境描述」（餵給文字後端，比分類名多一點上下文）。</summary>
    /// <remarks>
    /// 📌 使用者可以在設定視窗改寫，改寫後的值存在 <see cref="Configuration.CategoryDescriptions"/>，
    /// <b>不會</b>動到這裡。這裡是「沒有自訂描述時用的預設」。
    /// </remarks>
    public static readonly Dictionary<string, string> Situations = new()
    {
        [DutyComplete] = "這是提示音，不是對話：前輩剛剛順利通關了一個副本。只輸出一句極短提示（2~12 字），像脫口而出的一句話；不要鋪陳、不要說明、不要接第二個子句。",
        [LevelUp] = "這是提示音，不是對話：前輩剛剛升等了。只輸出一句極短提示（2~12 字），像脫口而出的一句話；不要鋪陳、不要說明、不要接第二個子句。",
        [Login] = "這是提示音，不是對話：前輩剛登入遊戲。只輸出一句極短提示（2~12 字），像脫口而出的一句話；不要鋪陳、不要說明、不要接第二個子句。",
        [GilMilestone] = "這是提示音，不是對話：前輩存的 Gil 剛跨過一個新的里程碑。只輸出一句極短提示（2~12 字），像脫口而出的一句話；不要鋪陳、不要說明、不要接第二個子句。",
        [Submarine] = "這是通知，不是誇獎：前輩派出去的潛水艇整隊平安回港了（或僱員的探險全部收完了）。只輸出一句 2~12 字的極短提示，像喊出來的一樣；不要說明、不要鋪陳。",
        [Retainer] = "這是通知，不是誇獎：前輩的僱員探險完成了，東西可以收了。只輸出一句 2~12 字的極短提示，像喊出來的一樣；不要說明、不要鋪陳。",
        [ExpertDelivery] = "這是通知，不是誇獎：前輩的稀有品繳交循環把所有角色都跑完了。只輸出一句 2~12 字的極短提示，像喊出來的一樣；不要說明、不要鋪陳。",
        [Market] = "這是通知，不是誇獎：前輩的市場重掛全部跑完了。只輸出一句 2~12 字的極短提示，像喊出來的一樣；不要說明、不要鋪陳。",
        [Crafting] = "這是通知，不是誇獎：前輩把整份製作清單做完了。只輸出一句 2~12 字的極短提示，像喊出來的一樣；不要說明、不要鋪陳。",
        [Cosmic] = "這是通知，不是誇獎：前輩在宇宙探索的任務拿到了金評價。只輸出一句 2~12 字的極短提示，像喊出來的一樣；不要說明、不要鋪陳。",
        [LowHp] = "這是戰鬥警示，不是誇獎：前輩的血量掉到危險線以下了，正在戰鬥中。只輸出一句 2~6 字的極短句，像喊出來的一樣；不要稱讚、不要說明、不要鋪陳。",
        [MarkedByMany] = "這是戰鬥警示，不是誇獎：好幾個敵對玩家同時鎖定了前輩。只輸出一句 2~6 字的極短句，像喊出來的一樣；不要稱讚、不要說明、不要鋪陳。",
        [EnemyBehind] = "這是戰鬥警示，不是誇獎：有敵對玩家從前輩的背後接近。只輸出一句 2~6 字的極短句，像喊出來的一樣；不要稱讚、不要說明、不要鋪陳。",
        [DutyStart] = "這是提醒，不是誇獎：任務／戰鬥開始了。只輸出 2~6 字的極短句（最多 8 字），像喊出來的一樣；不要說明、不要鋪陳。",
        [ReadyCheck] = "這是提醒，不是誇獎：跳出了準備確認，前輩要按確認。只輸出 2~6 字的極短句（最多 8 字），像喊出來的一樣；不要說明、不要鋪陳。",
        [CutsceneEnd] = "這是提醒，不是誇獎：過場動畫結束了，要開打了。只輸出 2~6 字的極短句（最多 8 字），像喊出來的一樣；不要說明、不要鋪陳。",
        [DutyPop] = "這是通知，不是誇獎：副本配對排到了，要按確認才進得去。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [FlagArrived] = "這是通知，不是誇獎：前輩走到地圖上的旗標位置了。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [Tell] = "這是通知，不是誇獎：有人傳了私訊給前輩。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [Arrived] = "這是通知，不是誇獎：前輩抵達了目的地（傳送、乘騎或跑路結束）。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [Jackpot] = "這是通知，不是誇獎：前輩中獎了（抽選、隨機獎勵之類）。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [NeedHelp] = "這是通知，不是誇獎：自動化卡住了，需要前輩過來看一下。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [PlayerAlert] = "這是通知，不是誇獎：附近出現了要注意的玩家。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [BeingWatched] = "這是通知，不是誇獎：有人把前輩設成了目標，正在盯著前輩看。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [TellReceived] = "這是通知，不是誇獎：有人傳密語給前輩了。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [PartyInvite] = "這是通知，不是誇獎：有人邀請前輩加入隊伍，畫面上跳出了邀請視窗。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [TradeRequest] = "這是通知，不是誇獎：有人要跟前輩交易，畫面上跳出了交易視窗。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [DutyRunStopped] = "這是通知，不是誇獎：前輩的自動跑本停下來了（跑完或中途停住都算）。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [GatherStopped] = "這是通知，不是誇獎：前輩的自動採集停下來了。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [RareFish] = "這是通知，不是誇獎：前輩釣到了一條稀有的魚。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [HuntFound] = "這是通知，不是誇獎：附近出現了正在找的稀有魔物。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [BagAlmostFull] = "這是通知，不是誇獎：前輩的背包快要塞滿了。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [DailyReset] = "這是通知，不是誇獎：每日的重置時間到了，新的一輪可以開始。只輸出一句 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
    };

    /// <summary>
    /// 內建情境的「句長上限覆寫」。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>每一個內建情境都是「極短提示」</b>（≤12 字）——這是實機定調的形態：
    /// 「不是要太多對話，只是要有聲音」。長句念起來像唸稿，而且在通知情境會變成
    /// 「通知念了五秒才講完」。
    /// <para>
    /// ⚠️ 這裡的值同時決定<b>提示詞跟模型要幾個字</b>（見 <see cref="PraiseText.LengthHint"/>）。
    /// 改了這裡就要跟著改 <see cref="Situations"/> 裡那個情境的描述文字，
    /// 兩邊對不起來的失敗形狀是<b>良率掉到接近 0，而且看起來像模型壞掉</b>。
    /// </para>
    /// <para>
    /// 📌 沒列在這裡的情境（使用者自訂的）回 0，代表<b>用全域上限</b>（預設也是 12）。
    /// 使用者在設定視窗填的覆寫存在 <see cref="Configuration.CategoryMaxLength"/>，優先於這裡。
    /// </para>
    /// </remarks>
    public static readonly Dictionary<string, int> MaxLengths = new()
    {
        [DutyComplete] = 12,
        [LevelUp] = 12,
        [Login] = 12,
        [GilMilestone] = 12,
        [Submarine] = 12,
        [Retainer] = 12,
        [ExpertDelivery] = 12,
        [Market] = 12,
        [Crafting] = 12,
        [Cosmic] = 12,
        [LowHp] = 8,
        [MarkedByMany] = 8,
        [EnemyBehind] = 8,
        [DutyStart] = 10,
        [ReadyCheck] = 10,
        [CutsceneEnd] = 10,
        [DutyPop] = 12,
        [FlagArrived] = 12,
        [Tell] = 12,
        [Arrived] = 12,
        [Jackpot] = 12,
        [NeedHelp] = 12,
        [PlayerAlert] = 12,
        [BeingWatched] = 12,
        [TellReceived] = 12,
        [PartyInvite] = 12,
        [TradeRequest] = 12,
        [DutyRunStopped] = 12,
        [GatherStopped] = 12,
        [RareFish] = 12,
        [HuntFound] = 12,
        [BagAlmostFull] = 12,
        [DailyReset] = 12,
    };

    /// <summary>
    /// 內建情境的「句長<b>下限</b>覆寫」。
    /// </summary>
    /// <remarks>
    /// 🔴 全域下限 <see cref="PraiseText.MinLength"/>（6 字）是拿來擋「模型吐出來的殘句」的，
    /// 對<b>所有內建情境</b>都不適用——「後面！」只有 3 個字、「完美收工！」只有 5 個字，
    /// 正是我們要的東西。不放寬下限的話，這些情境生回來的句子會<b>全部被當成殘句丟掉</b>，
    /// 而且看起來像模型壞掉。
    /// </remarks>
    public static readonly Dictionary<string, int> MinLengths = new()
    {
        [DutyComplete] = 2,
        [LevelUp] = 2,
        [Login] = 2,
        [GilMilestone] = 2,
        [Submarine] = 2,
        [Retainer] = 2,
        [ExpertDelivery] = 2,
        [Market] = 2,
        [Crafting] = 2,
        [Cosmic] = 2,
        [LowHp] = 2,
        [MarkedByMany] = 2,
        [EnemyBehind] = 2,
        [DutyStart] = 2,
        [ReadyCheck] = 2,
        [CutsceneEnd] = 2,
        [DutyPop] = 2,
        [FlagArrived] = 2,
        [Tell] = 2,
        [Arrived] = 2,
        [Jackpot] = 2,
        [NeedHelp] = 2,
        [PlayerAlert] = 2,
        [BeingWatched] = 2,
        [TellReceived] = 2,
        [PartyInvite] = 2,
        [TradeRequest] = 2,
        [DutyRunStopped] = 2,
        [GatherStopped] = 2,
        [RareFish] = 2,
        [HuntFound] = 2,
        [BagAlmostFull] = 2,
        [DailyReset] = 2,
    };

    /// <summary>
    /// 內建情境的「冷卻秒數覆寫」。
    /// </summary>
    /// <remarks>
    /// 🔴 全域冷卻（預設 120 秒）是為「偶爾誇一下」設計的，套到<b>通知</b>上會把東西吃掉：
    /// AutoRetainer 多角色連跑時，後面幾個角色的「潛艇」通知會全部落在冷卻裡靜默消失。
    /// 警示更不用說——過了兩分鐘才喊「後面！」沒有任何意義。
    /// <para>
    /// 📌 冷卻計時器是<b>逐情境</b>的（見 <see cref="PraiseService"/>）：「潛艇」的冷卻不會擋到「血量低」。
    /// 沒列在這裡的情境（原本那四個誇獎情境、還有自訂的）回 0，代表用全域冷卻。
    /// </para>
    /// </remarks>
    public static readonly Dictionary<string, int> Cooldowns = new()
    {
        [Submarine] = 5,
        [Retainer] = 5,
        [ExpertDelivery] = 5,
        [Market] = 5,
        [Crafting] = 5,
        [Cosmic] = 5,
        [LowHp] = 15,
        [MarkedByMany] = 10,
        [EnemyBehind] = 10,
        [DutyStart] = 5,
        [ReadyCheck] = 5,
        [CutsceneEnd] = 5,
        [DutyPop] = 5,
        [FlagArrived] = 5,
        [Tell] = 5,
        [Arrived] = 5,
        [Jackpot] = 5,
        [NeedHelp] = 5,
        [PlayerAlert] = 5,
        [BeingWatched] = 5,
        [TellReceived] = 5,
        [PartyInvite] = 5,
        [TradeRequest] = 5,
        [DutyRunStopped] = 5,
        [GatherStopped] = 5,
        [RareFish] = 5,
        [HuntFound] = 5,
        [BagAlmostFull] = 5,
        [DailyReset] = 5,
    };

    /// <summary>內建的句長下限覆寫；沒有就回 0（＝用全域下限）。</summary>
    public static int DefaultMinLength(string category)
        => MinLengths.TryGetValue(category, out var n) ? n : 0;

    /// <summary>內建的冷卻秒數覆寫；沒有就回 0（＝用全域冷卻）。</summary>
    public static int DefaultCooldownSeconds(string category)
        => Cooldowns.TryGetValue(category, out var n) ? n : 0;

    /// <summary>內建的句長上限覆寫；沒有就回 0（＝用全域上限）。</summary>
    public static int DefaultMaxLength(string category)
        => MaxLengths.TryGetValue(category, out var n) ? n : 0;

    /// <summary>這個情境是不是內建的（內建的不可刪、而且一定有預設描述）。</summary>
    public static bool IsBuiltIn(string category) => Array.IndexOf(All, category) >= 0;

    /// <summary>內建情境的預設描述；不是內建情境就回空字串。</summary>
    public static string DefaultDescription(string category)
        => Situations.TryGetValue(category, out var s) ? s : string.Empty;

    /// <summary>
    /// 沒有任何描述時，退回用「鍵名」組出來的情境句。
    /// </summary>
    /// <remarks>
    /// 📌 這是<b>最後的退路</b>：使用者自己新增的情境如果沒填描述，就只能拿鍵名當線索。
    /// 真正的取用順序在 <see cref="Configuration.SituationOf"/>：自訂描述 → 內建預設描述 → 這裡。
    /// </remarks>
    public static string DescribeSituation(string category)
        => Situations.TryGetValue(category, out var s) ? s : $"前輩剛剛達成了「{category}」";
}
