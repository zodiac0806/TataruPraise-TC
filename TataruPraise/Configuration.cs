using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Configuration;

namespace TataruPraise;

/// <summary>文字後端模式。</summary>
/// <remarks>
/// ⚠️ 第一版<b>只實作 <see cref="Pool"/></b>。另外兩個值先佔位，讓設定檔的欄位語意固定下來，
/// 之後補做時不必再改一次設定結構（改列舉的數值＝既有使用者的設定靜默跑到別的模式去）。
/// 📌 列舉刻意從 0 開始且 0 就是預設值——沒有零值的列舉會讓 <c>default</c> 落在無效值上。
/// </remarks>
public enum TextBackend
{
    /// <summary>純池：執行期零 HTTP，只從本機誇獎池挑句、播事先合成好的快取。</summary>
    Pool = 0,

    /// <summary>雲端即時（Gemini）。TODO：尚未實作，選了等同 <see cref="Pool"/>。</summary>
    GeminiLive = 1,

    /// <summary>本機即時（Ollama）。TODO：尚未實作，選了等同 <see cref="Pool"/>。</summary>
    OllamaLive = 2,
}

public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // ── 總開關與節流 ───────────────────────────────────────────────
    /// <summary>總開關。🔴 預設關：安裝完不會突然有聲音。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>全域冷卻秒數。冷卻中的觸發直接丟棄，不排隊。</summary>
    public int CooldownSeconds { get; set; } = 10;

    /// <summary>觸發機率（%）。過了冷卻還要再擲一次骰。</summary>
    public int ChancePercent { get; set; } = 100;

    /// <summary>播放音量（0～1）。</summary>
    public float Volume { get; set; } = 0.8f;

    // ── 逐事件開關（全部預設關）─────────────────────────────────────
    public bool TriggerDutyComplete { get; set; } = false;
    public bool TriggerLevelUp { get; set; } = false;
    public bool TriggerLogin { get; set; } = false;
    public bool TriggerGilMilestone { get; set; } = false;

    /// <summary>Gil 里程碑的間隔：每跨過這個數字的整數倍就算一次里程碑。</summary>
    public long GilMilestoneStep { get; set; } = 1_000_000;

    // ── 戰鬥警示（全部預設關；純讀狀態、純出聲，不做任何遊戲操作）────────────
    /// <summary>血量掉到門檻以下時警示（要在戰鬥中）。</summary>
    public bool TriggerLowHp { get; set; } = false;

    /// <summary>血量警示的門檻（%）。跌破才觸發。</summary>
    public int LowHpThresholdPercent { get; set; } = 30;

    /// <summary>被多個敵對玩家同時鎖定時警示（只在 PvP 區域）。</summary>
    public bool TriggerMarkedByMany { get; set; } = false;

    /// <summary>要幾個敵對玩家同時鎖定我才算「被大量標記」。</summary>
    public int MarkedByManyCount { get; set; } = 3;

    /// <summary>敵對玩家從背後接近時警示（只在 PvP 區域）。</summary>
    public bool TriggerEnemyBehind { get; set; } = false;

    /// <summary>背後警示的距離（碼）。超過這個距離的不算。</summary>
    public float EnemyBehindRange { get; set; } = 15f;

    /// <summary>要幾個敵對玩家同時從背後接近才觸發。</summary>
    public int EnemyBehindCount { get; set; } = 2;

    // ── 內建通知（全部預設關；只讀狀態、純出聲，不做任何遊戲操作）─────────────
    /// <summary>收到密語時出聲（看 <c>XivChatType.TellIncoming</c>，不比對任何文字）。</summary>
    /// <remarks>
    /// 🔴 判準<b>只有聊天類型</b>。台服的中文字面沒辦法離線確定，比對文字一定錯而且錯法是靜默的。
    /// 📌 自己送出去的密語是 <c>TellOutgoing</c>（12），不是 <c>TellIncoming</c>（13），不會觸發。
    /// </remarks>
    public bool TriggerTellReceived { get; set; } = false;

    /// <summary>副本配對排到時出聲（<c>IClientState.CfPop</c>）。</summary>
    /// <remarks>
    /// 📌 情境鍵是既有的「副本排到」，跟 NotificationMaster 走 IPC 叫的<b>是同一個鍵</b>。
    /// 兩邊同時開的話，逐情境冷卻（5 秒）會把第二次吸掉——不會聽到兩聲。
    /// </remarks>
    public bool TriggerDutyPop { get; set; } = false;

    /// <summary>收到組隊邀請時出聲（邀請彈窗 addon 出現）。</summary>
    public bool TriggerPartyInvite { get; set; } = false;

    /// <summary>收到交易請求時出聲（交易視窗 addon 出現）。</summary>
    public bool TriggerTradeRequest { get; set; } = false;

    // ── 觸發前提補強 ───────────────────────────────────────────────
    /// <summary>登入誇獎只在「當天第一次」出聲。</summary>
    /// <remarks>
    /// 📌 判準是<b>本機日期</b>（<see cref="DateTime.Now"/>），不是 UTC——使用者說的「今天」是他桌上那個今天。
    /// <para>
    /// 🔴 <see cref="LastLoginPraiseDate"/> 只在<b>真的出聲之後</b>才寫。冷卻擋掉、機率沒中、
    /// 池裡沒有已合成的句子——這些情況都不算「今天誇過了」，不然一次沒中就整天都沒有了。
    /// </para>
    /// <para>
    /// ⚠️ 關掉就退回舊行為：每次登入都試一次。
    /// </para>
    /// </remarks>
    public bool LoginOncePerDay { get; set; } = true;

    /// <summary>上一次因為登入而出聲的本機日期（<c>yyyy-MM-dd</c>）；還沒有過就是空字串。</summary>
    public string LastLoginPraiseDate { get; set; } = string.Empty;

    /// <summary>
    /// 「首次通關」的觸發機率（%）。
    /// </summary>
    /// <remarks>
    /// 📌 只有在<b>這個副本沒出現在 <see cref="ClearedDuties"/> 裡</b>的時候才用這個數字，
    /// 其他副本照 <see cref="ChancePercent"/> 走。想關掉這個加權就把它調成跟 <see cref="ChancePercent"/> 一樣。
    /// <para>
    /// 🔴 反查不到 ContentFinderCondition（例如那個場景根本不是副本）時<b>一律當一般副本處理</b>，
    /// 也不會記進 <see cref="ClearedDuties"/>。查不到就照常走，不會崩、也不會誤判成首通。
    /// </para>
    /// </remarks>
    public int FirstClearChancePercent { get; set; } = 100;

    /// <summary>
    /// 已經通關過的副本（ContentFinderCondition 的 row id）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這份紀錄是<b>外掛裝了以後才開始累積的</b>，不是遊戲的通關紀錄——
    /// 老角色第一次跑舊副本照樣會被算成「首次通關」。這點在設定視窗的 tooltip 裡寫給使用者看。
    /// <para>
    /// 📌 用 <see cref="List{T}"/> 存（設定檔是 JSON，陣列比集合好讀好手改）；
    /// 執行期的比對走 <see cref="Core.TriggerWatcher"/> 裡那份 HashSet 快取，不對這個清單做線性搜尋。
    /// </para>
    /// </remarks>
    public List<uint> ClearedDuties { get; set; } = [];


    // ── 語音橋接（GPT-SoVITS，預設同機）──────────────────────────────
    /// <summary>TTS 橋接位址。同機就是 127.0.0.1:9882；異機要填區網 IP 且對方要綁 0.0.0.0。也可以填任何相容的 HTTP API。</summary>
    public string TtsHost { get; set; } = "http://127.0.0.1:9882";

    /// <summary>
    /// TTS 橋接的 API Key。非空時每次呼叫都會加上 <c>Authorization: Bearer &lt;key&gt;</c>。
    /// 本機、沒有驗證的橋接留空即可；架在需要驗證的服務或反向代理後面才需要填。
    /// </summary>
    public string TtsApiKey { get; set; } = "";

    /// <summary>聲線 id（橋接 <c>GET /speakers</c> 回的 <c>voice_id</c>）。</summary>
    public string VoiceId { get; set; } = "塔塔露";

    // ── 文字後端 ────────────────────────────────────────────────────
    public TextBackend Backend { get; set; } = TextBackend.Pool;

    /// <summary>Gemini API 金鑰。🔴 存在 Dalamud 的外掛設定檔裡，不進版控、不寫進 log。</summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    /// <summary>Gemini 模型名。可自填；<c>gemini-2.x-flash</c> 系列對新金鑰已停用，別填。</summary>
    public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>按一次「擴充誇獎池」時，每個情境要生幾句。</summary>
    public int GenerateCountPerCategory { get; set; } = 5;

    /// <summary>
    /// 句長上限（字，不含空白；中文標點算在內）。生成回來超過這個長度的句子直接丟掉。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>生成端</b>的閘門，只擋新句子；pool.json 裡既有的長句<b>不會</b>被它動到
    /// （那是使用者的資料）。要清掉舊的長句請按設定視窗裡的「移除超過上限的句子」。
    /// 📌 出廠預設 12（極短提示）。想要長句誇獎就把設定視窗「進階」裡的滑桿推高，
    /// 提示詞跟模型要的字數會跟著自動變長。
    /// </remarks>
    public int MaxPraiseLength { get; set; } = Core.PraiseText.ShortDefaultMaxLength;

    // ── 情境描述 ────────────────────────────────────────────────────
    /// <summary>
    /// 每個情境的「情境描述」——生句時餵給 Gemini 當 situation 的那一段話。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>存在設定檔，不存 pool.json</b>：pool.json 是句子的資料檔，使用者會自己打開來編、也會整份備份／重置，
    /// 把描述混進去會在「重置為預設池」的時候一起被清掉。
    /// <para>
    /// 📌 只放<b>使用者改寫過</b>的；內建情境沒被改寫時這裡沒有鍵，取用時退回
    /// <see cref="Core.PraiseCategory.Situations"/> 的預設描述。
    /// </para>
    /// </remarks>
    public Dictionary<string, string> CategoryDescriptions { get; set; } = [];

    // ── 逐情境的啟用開關 ─────────────────────────────────────────────
    /// <summary>
    /// 每個情境的「要不要出聲」。<b>沒有這個鍵＝啟用</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>缺鍵一律當「啟用」</b>：既有使用者的設定檔裡根本沒有這個字典，
    /// 若把缺鍵當成「關」，升級之後全部情境會一起靜默閉嘴，而且完全沒有錯誤訊息。
    /// <para>
    /// 📌 關掉之後擋的是<b>池播放</b>那條路（見 <see cref="Core.PraiseService"/>）：
    /// 內建觸發與 IPC <c>Praise</c> 都不出聲（<c>Praise</c> 回 <c>false</c>）。
    /// <b>不</b>影響 IPC <c>Speak</c>（呼叫端自己指定句子）與設定視窗的「試播」——
    /// 那兩個是「明確要求念這一句」，不是事件。
    /// </para>
    /// <para>
    /// 📌 只有「關」會寫進字典，改回啟用就把鍵刪掉：「缺鍵＝啟用」要維持成<b>唯一</b>的預設語意，
    /// 兩種寫法並存的話之後改預設會有一半的使用者吃不到。
    /// </para>
    /// </remarks>
    public Dictionary<string, bool> CategoryEnabled { get; set; } = [];

    /// <summary>這個情境現在要不要出聲（沒有這個鍵＝要）。</summary>
    public bool IsCategoryEnabled(string category)
        => !CategoryEnabled.TryGetValue(category, out var enabled) || enabled;

    /// <summary>開關某個情境（啟用＝把鍵刪掉，回到「缺鍵＝啟用」）。</summary>
    public void SetCategoryEnabled(string category, bool enabled)
    {
        if (enabled)
            CategoryEnabled.Remove(category);
        else
            CategoryEnabled[category] = false;

        Save();
    }

    /// <summary>
    /// 每個情境的「句長上限覆寫」（字，不含空白）。
    /// </summary>
    /// <remarks>
    /// 📌 空／0／沒有這個鍵＝<b>用全域的 <see cref="MaxPraiseLength"/></b>；
    /// 沒有自訂時，內建情境還有一層 <see cref="Core.PraiseCategory.MaxLengths"/> 的預設（12／10／8）。
    /// 取用順序寫在 <see cref="MaxLengthOf"/>。
    /// <para>
    /// 🔴 覆寫的下界是 <see cref="Core.PraiseText.MinLength"/>（6），<b>不是</b> UI 全域滑桿的下界 12——
    /// 短通知句本來就要比一般誇獎句短，拿全域滑桿的範圍去夾會讓覆寫根本填不下去。
    /// </para>
    /// </remarks>
    public Dictionary<string, int> CategoryMaxLength { get; set; } = [];

    /// <summary>
    /// 每個情境的「句長<b>下限</b>覆寫」（字，不含空白）。
    /// </summary>
    /// <remarks>
    /// 🔴 全域下限 <see cref="Core.PraiseText.MinLength"/>（6 字）是拿來擋殘句的，對警示／提醒情境
    /// 完全不適用——「後面！」只有 3 個字、「完美收工！」只有 5 個字，正是要的東西。
    /// 不放寬的話那些情境生回來的句子會全部被丟掉。
    /// </remarks>
    public Dictionary<string, int> CategoryMinLength { get; set; } = [];

    /// <summary>某個情境生效的句長下限：自訂覆寫 → 內建覆寫 → 全域下限。</summary>
    public int MinLengthOf(string category)
    {
        if (CategoryMinLength.TryGetValue(category, out var custom) && custom > 0)
            return Math.Clamp(custom, 1, Core.PraiseText.SliderMax);

        var builtin = Core.PraiseCategory.DefaultMinLength(category);
        if (builtin > 0) return builtin;

        return Core.PraiseText.MinLength;
    }

    /// <summary>這個情境的下限是不是「自訂覆寫」來的。</summary>
    public bool HasMinLengthOverride(string category)
        => CategoryMinLength.TryGetValue(category, out var v) && v > 0;

    /// <summary>設定某個情境的句長下限覆寫（0 或負數＝清掉覆寫）。</summary>
    public void SetMinLength(string category, int value)
    {
        if (value <= 0)
            CategoryMinLength.Remove(category);
        else
            CategoryMinLength[category] = Math.Clamp(value, 1, Core.PraiseText.SliderMax);

        Save();
    }

    /// <summary>
    /// 每個情境的「冷卻秒數覆寫」。
    /// </summary>
    /// <remarks>
    /// 🔴 全域冷卻（預設 120 秒）套到通知上會把東西吃掉：AutoRetainer 多角色連跑時，
    /// 後面幾個角色的「潛艇」通知會全部落在冷卻裡靜默消失；警示過了兩分鐘才喊也沒有意義。
    /// <para>
    /// 📌 優先序：<b>自訂覆寫 → 內建覆寫（通知 5 秒、警示 15／10／10）→ 全域 <see cref="CooldownSeconds"/></b>。
    /// 計時器本身是逐情境的（見 <see cref="Core.PraiseService"/>），「潛艇」的冷卻不會擋到「血量低」。
    /// </para>
    /// </remarks>
    public Dictionary<string, int> CategoryCooldownSeconds { get; set; } = [];

    /// <summary>某個情境生效的冷卻秒數：自訂覆寫 → 內建覆寫 → 全域冷卻。</summary>
    public int CooldownOf(string category)
    {
        if (CategoryCooldownSeconds.TryGetValue(category, out var custom) && custom > 0)
            return Math.Clamp(custom, 0, 3600);

        var builtin = Core.PraiseCategory.DefaultCooldownSeconds(category);
        if (builtin > 0) return builtin;

        return Math.Max(0, CooldownSeconds);
    }

    /// <summary>這個情境的冷卻是不是「自訂覆寫」來的。</summary>
    public bool HasCooldownOverride(string category)
        => CategoryCooldownSeconds.TryGetValue(category, out var v) && v > 0;

    /// <summary>設定某個情境的冷卻覆寫（0 或負數＝清掉覆寫，退回內建／全域）。</summary>
    public void SetCooldown(string category, int seconds)
    {
        if (seconds <= 0)
            CategoryCooldownSeconds.Remove(category);
        else
            CategoryCooldownSeconds[category] = Math.Clamp(seconds, 1, 3600);

        Save();
    }

    /// <summary>全域句長上限，夾在 UI 滑桿範圍內（設定檔被手改成離譜的值也不會讓 UI 壞掉）。</summary>
    public int ClampedMaxPraiseLength
        => Math.Clamp(MaxPraiseLength, Core.PraiseText.SliderMin, Core.PraiseText.SliderMax);

    /// <summary>某個情境生效的句長上限：自訂覆寫 → 內建覆寫 → 全域上限。</summary>
    public int MaxLengthOf(string category)
    {
        if (CategoryMaxLength.TryGetValue(category, out var custom) && custom > 0)
            return Math.Clamp(custom, Core.PraiseText.MinLength, Core.PraiseText.SliderMax);

        var builtin = Core.PraiseCategory.DefaultMaxLength(category);
        if (builtin > 0) return builtin;

        return Math.Clamp(MaxPraiseLength, Core.PraiseText.SliderMin, Core.PraiseText.SliderMax);
    }

    /// <summary>這個情境的上限是不是「自訂覆寫」來的（UI 要分得出「沿用」與「填了同一個數字」）。</summary>
    public bool HasMaxLengthOverride(string category)
        => CategoryMaxLength.TryGetValue(category, out var v) && v > 0;

    /// <summary>設定某個情境的句長上限覆寫（0 或負數＝清掉覆寫，退回預設／全域）。</summary>
    public void SetMaxLength(string category, int value)
    {
        if (value <= 0)
            CategoryMaxLength.Remove(category);
        else
            CategoryMaxLength[category] = Math.Clamp(value, Core.PraiseText.MinLength, Core.PraiseText.SliderMax);

        Save();
    }

    /// <summary>某個情境目前生效的描述；沒有自訂也沒有內建預設就回空字串。</summary>
    public string DescriptionOf(string category)
    {
        if (CategoryDescriptions.TryGetValue(category, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom.Trim();

        return Core.PraiseCategory.DefaultDescription(category);
    }

    /// <summary>
    /// 餵給文字後端的情境句：自訂描述 → 內建預設描述 → 用鍵名組一句。
    /// </summary>
    /// <remarks>
    /// 🔴 第三段退路不可以省。使用者自己新增的情境如果沒填描述，這裡回空字串會讓提示詞變成
    /// 「遊戲情境：。」——模型會開始自由發揮，而失敗形狀是「句子看起來沒問題但跟情境無關」。
    /// </remarks>
    public string SituationOf(string category)
    {
        var described = DescriptionOf(category);
        return described.Length > 0 ? described : Core.PraiseCategory.DescribeSituation(category);
    }

    /// <summary>把某個情境的描述寫進設定（空字串＝清掉自訂，退回內建預設）。</summary>
    public void SetDescription(string category, string? description)
    {
        var trimmed = description?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            CategoryDescriptions.Remove(category);
        else
            CategoryDescriptions[category] = trimmed;

        Save();
    }

    /// <summary>今天的本機日期字串（<c>yyyy-MM-dd</c>）——登入誇獎的「當天」判準。</summary>
    public static string TodayStamp() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}
