using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace TataruPraise.Core;

/// <summary>
/// 出聲的總機：總開關、冷卻、機率、挑句、播放。
/// </summary>
/// <remarks>
/// 三個入口的規則刻意不一樣，README 與 IPC 契約都照這裡寫：
/// <list type="table">
/// <item><term><see cref="TryTrigger"/>（遊戲事件）</term><description>總開關 → 該事件開關 → 冷卻 → 機率 → 情境開關</description></item>
/// <item><term><see cref="Praise"/>（IPC）</term><description>總開關 → 冷卻 → 情境開關（<b>不看</b>事件開關與機率）</description></item>
/// <item><term><see cref="Speak"/>（IPC）</term><description>總開關（<b>不看</b>冷卻、機率與情境開關——呼叫端是明確要求念這一句）</description></item>
/// </list>
/// 三者都受「同時只播一句」限制，但<b>不是一律丟棄</b>：
/// 事件與 IPC <see cref="Praise"/> 走的池播放路徑有一個<b>單格待播槽</b>，見 <see cref="PriorityOf"/>。
/// <see cref="Speak"/> 與試播<b>不進待播槽</b>——那是呼叫端／使用者明確要求的當下那一句，
/// 遲到兩秒才念出來比不念更奇怪。
/// </remarks>
public sealed class PraiseService : IDisposable
{
    private readonly Configuration config;
    private readonly PraisePool pool;
    private readonly AudioPlayer audio = new();
    private readonly object cooldownGate = new();

    private DateTime lastSpokenUtc = DateTime.MinValue;

    /// <summary>待播槽的鎖。刻意<b>不</b>跟 <c>cooldownGate</c> 共用：兩者的持有時間長短差很多。</summary>
    private readonly object pendingGate = new();

    /// <summary>待播槽：情境鍵。空字串＝槽是空的。</summary>
    /// <remarks>
    /// 🔴 槽裡<b>只放純量</b>（字串與 int）：情境鍵、句子、WAV 的絕對路徑、優先權。
    /// 絕不放原生指標、<c>IGameObject</c> 包裝或任何跨幀會被就地改寫的東西。
    /// </remarks>
    private string pendingCategory = string.Empty;

    /// <summary>待播槽：要念的句子（只拿來寫 log 與 DTR 歷史）。</summary>
    private string pendingText = string.Empty;

    /// <summary>待播槽：WAV 的絕對路徑。</summary>
    private string pendingPath = string.Empty;

    /// <summary>待播槽的優先權（數字小＝優先）。槽空的時候是 <see cref="int.MaxValue"/>。</summary>
    private int pendingPriority = int.MaxValue;

    /// <summary>最近幾次真的出聲的紀錄（DTR 的 tooltip 用）。</summary>
    private readonly List<(DateTime WhenLocal, string Category, string Text)> history = [];

    /// <summary>DTR tooltip 要列幾筆。</summary>
    private const int HistoryLimit = 5;

    public PraiseService(Configuration config, PraisePool pool)
    {
        this.config = config;
        this.pool = pool;

        // 🔴 待播槽要有人來倒。NAudio 沒有給我們「播完了」的回呼（見 AudioPlayer 的註解：
        //    刻意照抄 Saucy 的路徑，不自創），所以只能在 framework tick 上看 IsBusy 有沒有落下來。
        //    這個 tick 什麼都不做除非槽裡有東西，成本是一次布林讀取。
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// 出聲的優先權：<b>數字小的先</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 順序是<b>警示 &gt; 通知 &gt; 誇獎</b>，警示內部再固定成
    /// 血量低 &gt; 敵人從後面來 &gt; 被大量敵人標記。
    /// 理由：警示是「現在不處理就會死」的資訊，誇獎晚兩秒或根本沒念都無所謂。
    /// <para>
    /// 📌 認不得的情境（使用者自訂的、或別的外掛用 IPC 叫的新鍵）一律當<b>通知</b>——
    /// 它們幾乎都是「有事發生了」而不是「你好棒」。當成誇獎會讓自訂通知永遠排在最後面。
    /// </para>
    /// <para>
    /// ⚠️ 數字之間刻意留空隙（0~3 警示、10 通知、20 誇獎），之後要插新的層級不必動既有的值。
    /// </para>
    /// </remarks>
    public static int PriorityOf(string category) => category switch
    {
        PraiseCategory.LowHp => 0,
        PraiseCategory.EnemyBehind => 1,
        PraiseCategory.MarkedByMany => 2,

        PraiseCategory.DutyComplete
            or PraiseCategory.LevelUp
            or PraiseCategory.Login
            or PraiseCategory.GilMilestone => 20,

        _ => 10,
    };

    /// <summary>最近幾次真的出聲的紀錄（新的在前）。回的是複本，呼叫端隨便怎麼用都不會動到內部狀態。</summary>
    public List<(DateTime WhenLocal, string Category, string Text)> RecentHistory()
    {
        lock (pendingGate)
        {
            var copy = new List<(DateTime, string, string)>(history.Count);
            for (var i = history.Count - 1; i >= 0; i--) copy.Add(history[i]);
            return copy;
        }
    }

    /// <summary>待播槽裡現在放著哪個情境（空字串＝沒有）。UI 與診斷用。</summary>
    public string PendingCategory
    {
        get { lock (pendingGate) return pendingCategory; }
    }

    public AudioPlayer Audio => audio;

    /// <summary>距離下次可以出聲還有幾秒（0＝現在就可以）。UI 上要看得見，所以是公開的。</summary>
    public double CooldownRemainingSeconds
    {
        get
        {
            lock (cooldownGate)
            {
                var elapsed = (DateTime.UtcNow - lastSpokenUtc).TotalSeconds;
                var remain = config.CooldownSeconds - elapsed;
                return remain > 0 ? remain : 0;
            }
        }
    }

    /// <summary>
    /// 每個情境上一次出聲的時間（UTC ticks）。
    /// </summary>
    /// <remarks>
    /// 🔴 冷卻計時器是<b>逐情境</b>的。共用一個計時器的話，AutoRetainer 多角色連跑時
    /// 後面幾個角色的「潛艇」通知會被前一個吃掉；戰鬥警示更是完全等不起兩分鐘。
    /// <para>
    /// 📌 「同時只播一句」的限制<b>沒有</b>跟著拆開——那是喇叭的物理限制，不是節流政策。
    /// 正在播的時候來的東西一律丟棄（不排隊），並在 Debug 記一行。
    /// </para>
    /// <para>
    /// 🔴 這個字典會被<b>呼叫端的執行緒</b>碰到（IPC 在對方執行緒上跑），所以跟 cooldownGate 共用同一把鎖。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, long> lastSpokenPerCategory = new(StringComparer.Ordinal);

    /// <summary>某個情境距離下次可以出聲還有幾秒（0＝現在就可以）。</summary>
    public double CooldownRemainingSecondsOf(string category)
    {
        var cooldown = config.CooldownOf(category);
        if (cooldown <= 0) return 0;

        lock (cooldownGate)
        {
            if (!lastSpokenPerCategory.TryGetValue(category, out var ticks)) return 0;

            var elapsed = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
            var remain = cooldown - elapsed;
            return remain > 0 ? remain : 0;
        }
    }

    /// <summary>總開關開著、而且真的有可播的內容。</summary>
    public bool IsAvailable() => config.Enabled && pool.HasAnyCached();

    /// <summary>
    /// <b>這一個情境</b>現在有沒有辦法出聲。
    /// </summary>
    /// <remarks>
    /// 🔴 三個條件缺一不可：總開關、<b>逐情境</b>的啟用開關、以及這個情境真的有已合成的語音。
    /// 這正是 <see cref="PlayFromPool"/> 會擋下來的那三件事，只差冷卻。
    /// <para>
    /// 📌 刻意<b>不</b>看冷卻：冷卻是「這一次剛好不出聲」，下一次就會出聲；
    /// 呼叫端拿這個結果去決定「要不要改走別的通知管道」，把冷卻算進來會讓它在冷卻期間
    /// 多發一次系統匣通知——那是雜訊，不是修好。
    /// </para>
    /// </remarks>
    public bool IsAvailableFor(string category)
        => config.Enabled
        && config.IsCategoryEnabled(category)
        && pool.HasCachedFor(category);

    /// <summary>
    /// 遊戲事件觸發的路徑：吃冷卻也吃機率。
    /// </summary>
    /// <param name="category">情境。</param>
    /// <param name="chanceOverride">
    /// 這一次要用的機率（%）。<c>null</c>＝用 <see cref="Configuration.ChancePercent"/>。
    /// </param>
    /// <remarks>
    /// 📌 <paramref name="chanceOverride"/> 是給「首次通關」用的：同一個事件、同一個情境，
    /// 但這一次的機率不一樣。<b>不要</b>為此另開一個情境——情境是 pool.json 的鍵，
    /// 多開一個等於使用者要多養一池句子。
    /// </remarks>
    public bool TryTrigger(string category, int? chanceOverride = null)
    {
        if (!config.Enabled) return false;
        if (CooldownRemainingSecondsOf(category) > 0) return false;

        var chance = Math.Clamp(chanceOverride ?? config.ChancePercent, 0, 100);
        if (chance <= 0) return false;
        if (chance < 100 && Random.Shared.Next(100) >= chance) return false;

        return PlayFromPool(category);
    }

    /// <summary>這個情境存在嗎（IPC 要分得出「未知情境」與「有情境但沒句子」）。</summary>
    public bool HasCategory(string category) => pool.HasCategory(category);

    /// <summary>
    /// IPC <c>TataruPraise.Praise</c>：無視事件開關與機率，但吃冷卻。
    /// </summary>
    /// <remarks>
    /// 📌 使用者在設定視窗把這個情境的「啟用」關掉時回 <c>false</c>（擋在 <see cref="PlayFromPool"/>），
    /// 呼叫端不必自己判斷——回 <c>false</c> 一律當成「這次沒出聲」就對了。
    /// </remarks>
    public bool Praise(string category)
    {
        if (!config.Enabled) return false;
        if (CooldownRemainingSecondsOf(category) > 0) return false;
        return PlayFromPool(category);
    }

    /// <summary>
    /// IPC <c>TataruPraise.Speak</c>：念指定的句子。
    /// </summary>
    /// <remarks>
    /// 先查語音快取；沒有的話丟一個背景 Task 去 9882 即時合成（逾時 10 秒），合成好順便寫進快取。
    /// 回傳值是「有沒有排進去」——即時合成那條路是<b>非同步</b>的，回 <c>true</c> 只代表已受理，
    /// 不代表真的出得了聲（橋接連不上就只是不出聲）。
    /// </remarks>
    public bool Speak(string text)
    {
        if (!config.Enabled) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (audio.IsBusy) return false;

        var trimmed = text.Trim();
        var cached = pool.CachePathFor(trimmed);
        if (File.Exists(cached))
        {
            var queued = audio.TryPlayFile(cached, config.Volume);
            if (queued) MarkSpoken();
            return queued;
        }

        // 即時合成：不在呼叫端的執行緒上等 HTTP。
        var host = config.TtsHost;
        var apiKey = config.TtsApiKey;
        var voice = config.VoiceId;
        var volume = config.Volume;
        _ = Task.Run(async () =>
        {
            var wav = await TtsBridge.SynthesizeAsync(host, voice, trimmed, 10, apiKey).ConfigureAwait(false);
            if (wav == null) return;

            TryWriteCache(trimmed, wav);
            audio.TryPlay(wav, volume);
        });

        MarkSpoken();
        return true;
    }

    /// <summary>試播：直接從池裡挑一句（總開關關著也可以，因為這是使用者按的按鈕）。</summary>
    public bool PlayTest(out string message)
    {
        foreach (var category in PraiseCategory.All)
        {
            var text = pool.PickCached(category);
            if (text == null) continue;

            var path = pool.CachePathFor(text);
            if (!audio.TryPlayFile(path, config.Volume))
            {
                message = "上一句還在播，等它播完再試。";
                return false;
            }

            message = text;
            return true;
        }

        message = "池裡沒有任何「已經合成好語音」的句子，先按「預合成語音快取」。";
        return false;
    }

    /// <summary>
    /// 試播<b>指定情境</b>的一句（總開關關著、還在冷卻都照播——這是使用者按的按鈕）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不呼叫 <see cref="MarkSpoken"/></b>：試播不可以把真正的冷卻計時器往後推，
    /// 不然使用者在設定視窗按幾下試聽，等一下真的發生事件時就被自己按出來的冷卻擋掉了。
    /// <para>
    /// 📌 「這個情境還沒有語音」與「上一句還在播」是兩件不同的事，訊息要分得開——
    /// 前者要去按「合成」，後者只要再等一下。
    /// </para>
    /// </remarks>
    public bool PlayCategoryTest(string category, out string message)
    {
        var text = pool.PickCached(category);
        if (text == null)
        {
            message = pool.CountOf(category) == 0
                ? $"「{category}」一句都沒有，先在欄位裡打一句短句。"
                : $"「{category}」還沒有語音，先按同一列的「合成」。";
            return false;
        }

        if (!audio.TryPlayFile(pool.CachePathFor(text), config.Volume))
        {
            message = "上一句還在播，等它播完再試。";
            return false;
        }

        message = text;
        return true;
    }

    private bool PlayFromPool(string category)
    {
        // 🔴 逐情境的啟用開關擋在<b>進待播槽之前</b>：
        //    ①<see cref="TryTrigger"/>（遊戲事件）與 <see cref="Praise"/>（IPC）都經過這裡，一個閘門就夠；
        //    ②排進槽裡再丟掉的話，關掉的情境還是會把同優先權、後到的那一句擠掉。
        //    📌 <see cref="Speak"/> 與試播刻意不走這條路，所以不受這個開關影響。
        if (!config.IsCategoryEnabled(category))
        {
            Svc.Log.Debug($"[TataruPraise] 情境「{category}」在設定裡是關的，這次不出聲。");
            return false;
        }

        var text = pool.PickCached(category);
        if (text == null)
        {
            Svc.Log.Information(
                $"[TataruPraise] 情境「{category}」沒有已合成語音的句子，這次不出聲"
                + $"（池裡 {pool.CountOf(category)} 句，已快取 {pool.CachedCountOf(category)} 句）。");
            return false;
        }

        var path = pool.CachePathFor(text);
        if (audio.TryPlayFile(path, config.Volume))
        {
            OnSpoken(category, text);
            Svc.Log.Information($"[TataruPraise] 觸發「{category}」：{text}");
            return true;
        }

        // 上一句還在播。🔴 <b>不打斷正在播的那一句</b>——把話砍在半路比晚一秒更難聽。
        return Enqueue(category, text, path);
    }

    /// <summary>
    /// 把一句放進單格待播槽。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>槽只有一格</b>，而且比較的是優先權不是先來後到：正在播「完美收工！」的時候
    /// 來了「危險！」，等它播完要接的必須是警示，不是排在前面的另一句誇獎。
    /// <para>
    /// 📌 同優先權的<b>先來的贏</b>（比較用 <c>&lt;=</c>）：同一類的兩個通知誰先誰後沒有意義，
    /// 但「後到的一律覆蓋」會讓連續事件時只聽得到最後一個。
    /// </para>
    /// <para>
    /// 📌 回傳 <c>true</c> 代表<b>已排進去</b>，不代表一定播得出來——中途被更高優先權的擠掉就不會播。
    /// 這跟 <see cref="Speak"/> 的回傳語意一致。
    /// </para>
    /// </remarks>
    private bool Enqueue(string category, string text, string path)
    {
        var priority = PriorityOf(category);
        string? dropped = null;
        string? replaced = null;

        lock (pendingGate)
        {
            if (pendingPath.Length > 0 && pendingPriority <= priority)
            {
                dropped = pendingCategory;
            }
            else
            {
                if (pendingPath.Length > 0) replaced = pendingCategory;
                pendingCategory = category;
                pendingText = text;
                pendingPath = path;
                pendingPriority = priority;
            }
        }

        if (dropped != null)
        {
            Svc.Log.Debug(
                $"[TataruPraise] 情境「{category}」（優先權 {priority}）：上一句還在播，"
                + $"而待播槽裡是優先權較高或同級的「{dropped}」，這次丟棄。");
            return false;
        }

        if (replaced != null)
        {
            Svc.Log.Debug(
                $"[TataruPraise] 待播槽換成「{category}」（優先權 {priority}），"
                + $"擠掉優先權較低的「{replaced}」。正在播的那一句不受影響。");
        }
        else
        {
            Svc.Log.Debug($"[TataruPraise] 情境「{category}」進待播槽（優先權 {priority}），等目前這句播完。");
        }

        return true;
    }

    /// <summary>
    /// 每幀看一次：喇叭空了就把待播槽倒出來播。
    /// </summary>
    /// <remarks>
    /// 🔴 槽是空的時候這裡只做一次布林讀取就 return，不碰磁碟、不碰池、不配置任何東西。
    /// 📌 <see cref="AudioPlayer.TryPlayFile"/> 又回 <c>false</c>（剛好被別的路徑搶走）時，
    /// 那一句就<b>真的丟掉</b>——放回去會變成無界的重試迴圈。
    /// </remarks>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (audio.IsBusy) return;

        string category, text, path;
        lock (pendingGate)
        {
            if (pendingPath.Length == 0) return;

            category = pendingCategory;
            text = pendingText;
            path = pendingPath;

            pendingCategory = string.Empty;
            pendingText = string.Empty;
            pendingPath = string.Empty;
            pendingPriority = int.MaxValue;
        }

        if (!audio.TryPlayFile(path, config.Volume))
        {
            Svc.Log.Debug($"[TataruPraise] 待播的「{category}」要播的時候喇叭又忙了，丟棄。");
            return;
        }

        OnSpoken(category, text);
        Svc.Log.Information($"[TataruPraise] 待播的「{category}」接著播：{text}");
    }

    /// <summary>真的出聲了：推冷卻、記進 DTR 歷史。</summary>
    private void OnSpoken(string category, string text)
    {
        MarkSpoken(category);

        lock (pendingGate)
        {
            history.Add((DateTime.Now, category, text));
            while (history.Count > HistoryLimit) history.RemoveAt(0);
        }
    }

    /// <summary>記下「剛剛出聲了」：全域那份給 UI 顯示用，逐情境那份才是真的冷卻閘門。</summary>
    private void MarkSpoken(string category = "")
    {
        var now = DateTime.UtcNow;
        lock (cooldownGate)
        {
            lastSpokenUtc = now;
            if (category.Length > 0) lastSpokenPerCategory[category] = now.Ticks;
        }
    }

    private void TryWriteCache(string text, byte[] wav)
    {
        try
        {
            Directory.CreateDirectory(pool.CacheDirectory);
            var path = pool.CachePathFor(text);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, wav);
            File.Move(tmp, path, overwrite: true);
            pool.SetCachedWav(text, "cache/" + PraisePool.CacheFileName(text));
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 寫入語音快取失敗：{ex.Message}");
        }
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        audio.Dispose();
    }
}
