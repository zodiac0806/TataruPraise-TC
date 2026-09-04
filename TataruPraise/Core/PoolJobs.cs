using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TataruPraise.Core;

/// <summary>
/// 設定 UI 那些按鈕背後的長時間工作：擴充誇獎池（Gemini）、預合成語音快取（9882）、
/// 移除超長句子、重置為預設池。
/// </summary>
/// <remarks>
/// 所有工作都在背景 Task 上跑，<b>同時只准跑一個</b>。進度與最後結果放在欄位裡讓 UI 每幀讀
/// （<c>volatile</c>／簡單型別，沒有鎖——UI 讀到中間狀態最多就是慢一幀）。
/// <para>
/// 🔴 互斥不能只靠「UI 按鈕在跑的時候是 disabled」：情境表格上有一整排按鈕，
/// 而且 <c>pool.json</c> 是<b>整份重寫</b>的（見 <see cref="PraisePool.Save"/>），
/// 兩個工作同時寫等於後寫的把先寫的成果整個蓋掉。這裡用
/// <see cref="Interlocked"/> 搶旗標，搶輸的完全不動任何欄位。
/// </para>
/// <para>
/// 📌 <b>「上一次做了什麼」要留在列上看得見</b>，不能只寫進 log：使用者按了按鈕之後，
/// 唯一能判斷「是成功了還是根本沒動」的地方就是那一行。
/// </para>
/// </remarks>
public sealed class PoolJobs : IDisposable
{
    private readonly Configuration config;
    private readonly PraisePool pool;

    private CancellationTokenSource? cts;

    /// <summary>0＝閒置、1＝有工作在跑。只透過 <see cref="Interlocked"/>／<see cref="Volatile"/> 動它。</summary>
    private int runningFlag;

    private volatile string jobName = string.Empty;
    private volatile string runningCategory = string.Empty;
    private volatile string progress = string.Empty;
    private volatile string lastResult = string.Empty;

    public PoolJobs(Configuration config, PraisePool pool)
    {
        this.config = config;
        this.pool = pool;
    }

    public bool IsRunning => Volatile.Read(ref runningFlag) != 0;

    /// <summary>正在跑的工作名稱（沒在跑就是空字串）。</summary>
    public string JobName => jobName;

    /// <summary>
    /// 正在處理哪一個情境（沒在跑、或這個工作不屬於單一情境時是空字串）。
    /// </summary>
    /// <remarks>📌 情境表格靠它決定「進行中」要標在哪一列。</remarks>
    public string RunningCategory => runningCategory;

    /// <summary>進度短句，例如「3/28」。</summary>
    public string Progress => progress;

    /// <summary>上一次的結果（可能很長，UI 上截短、完整放 tooltip）。</summary>
    public string LastResult => lastResult;

    public void Cancel() => cts?.Cancel();

    /// <summary>
    /// 擴充誇獎池：對<b>每一個</b>情境各發一次請求，每個情境都要
    /// <see cref="Configuration.GenerateCountPerCategory"/> 句。
    /// </summary>
    public bool StartExpandPool()
    {
        if (IsRunning) return false;
        if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
        {
            lastResult = "沒有填 Gemini API 金鑰，沒有東西可以擴充。";
            return false;
        }

        return Start("擴充誇獎池", string.Empty, ExpandPoolAsync);
    }

    /// <summary>
    /// 只擴充一個情境（情境表格上那一列的「生成」）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只發一次請求、只寫這一個情境</b>。實機回饋是各情境的句數長歪了，
    /// 使用者要能單獨把落後的那一類補起來，而不是整批重跑一次、把已經夠多的也一起加。
    /// </remarks>
    public bool StartExpandCategory(string category)
    {
        if (IsRunning) return false;
        if (string.IsNullOrWhiteSpace(category)) return false;
        if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
        {
            lastResult = "沒有填 Gemini API 金鑰，沒有東西可以擴充。";
            return false;
        }

        var count = ClampedGenerateCount();
        var maxLength = config.MaxLengthOf(category);

        return Start($"生成「{category}」", category, async token =>
        {
            var stats = new GenerateStats();
            var added = await ExpandCategoryAsync(category, count, maxLength, stats, token).ConfigureAwait(false);
            return DescribeOne(category, count, added, stats, maxLength);
        });
    }

    /// <summary>預合成語音快取：把池裡還沒有 WAV 的句子逐句送去 9882。</summary>
    public bool StartPrecacheAudio()
    {
        if (IsRunning) return false;
        return Start("預合成語音快取", string.Empty, PrecacheAudioAsync);
    }

    /// <summary>只合成一個情境裡還缺 WAV 的句子（情境表格上那一列的「合成」）。</summary>
    public bool StartSynthesizeCategory(string category)
    {
        if (IsRunning) return false;
        if (string.IsNullOrWhiteSpace(category)) return false;

        return Start($"合成「{category}」", category, token => SynthesizeCategoryAsync(category, token));
    }

    /// <summary>
    /// 移除池裡超過句長上限的句子（連同語音快取）。
    /// </summary>
    /// <remarks>
    /// 🔴 只能由使用者在設定視窗明確按下去才會跑：刪的是使用者自己的 pool.json 內容，不可回復。
    /// 改滑桿、載入外掛、擴充池都<b>不會</b>順手清舊句子。
    /// </remarks>
    public bool StartPruneLongLines()
    {
        if (IsRunning) return false;

        // 🔴 上限逐情境問：UI 顯示「有 N 句超長」用的是同一個 resolver，
        //    這裡換一把尺就會變成「顯示 3 句、刪掉 5 句」。
        var max = config.ClampedMaxPraiseLength;
        return Start("移除超長句子", string.Empty, _ =>
        {
            var removed = pool.RemoveLongerThan(config.MaxLengthOf, out var wavs);
            return Task.FromResult(
                removed == 0
                    ? $"移除超長句子：池裡沒有超過各情境上限的句子，什麼都沒動（全域上限 {max} 字）。"
                    : $"移除超長句子：刪掉 {removed} 句、連帶刪掉 {wavs} 個語音快取（全域上限 {max} 字，通知／警示情境另有自己的上限）。");
        });
    }

    /// <summary>
    /// 重置為內建預設池。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不可回復</b>：清掉整池（含自訂情境鍵）、刪掉那些句子的 WAV 快取、寫回內建的預設句。
    /// 只能由使用者在設定視窗做完兩段式確認之後按下去。
    /// <para>
    /// 🔴 走 <see cref="Start"/> 是為了<b>跟生成／合成共用同一把互斥鎖</b>——
    /// 合成到一半被重置洗掉整份 pool.json 的話，那些剛寫好的 WAV 路徑會靜默消失。
    /// </para>
    /// <para>
    /// 📌 備份與刪檔的範圍寫在 <see cref="PraisePool.ResetToDefault"/> 的註解裡（備份失敗＝整個中止）。
    /// </para>
    /// </remarks>
    public bool StartResetPool()
    {
        if (IsRunning) return false;

        return Start("重置為預設池", string.Empty, _ =>
        {
            var ok = pool.ResetToDefault(out var backup, out var removed, out var wavs, out var error);
            if (!ok) return Task.FromResult($"重置為預設池：{error}");

            var where = backup.Length > 0 ? backup : "（原本就沒有 pool.json，所以沒有備份）";
            return Task.FromResult(
                $"已重置為內建 {PraisePool.DefaultLineCount()} 句：清掉舊池 {removed} 句、"
                + $"刪掉 {wavs} 個語音快取，舊池備份於 {where}。"
                + "內建句子還沒有語音，記得按「預合成語音快取」。");
        });
    }

    /// <summary>
    /// 每個情境要生幾句。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這裡的上界刻意留在 50，跟 UI 輸入框的 1~30 不一樣：設定檔裡本來就可能存著 50，
    /// 把它一起收成 30 等於<b>靜默改掉既有使用者的設定</b>。UI 只夾使用者「新輸入」的值。
    /// </remarks>
    private int ClampedGenerateCount() => Math.Clamp(config.GenerateCountPerCategory, 1, 50);

    /// <summary>
    /// 搶下互斥旗標並在背景跑一個工作。搶輸（已經有工作在跑）就回 <c>false</c> 且什麼都不做。
    /// </summary>
    private bool Start(string name, string category, Func<CancellationToken, Task<string>> work)
    {
        // 🔴 先搶旗標再碰任何欄位：搶輸的路徑必須完全沒有副作用，
        // 否則同一幀連按兩顆按鈕會把正在跑的那個工作的 cts 換掉。
        if (Interlocked.CompareExchange(ref runningFlag, 1, 0) != 0) return false;

        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        jobName = name;
        runningCategory = category;
        progress = string.Empty;
        lastResult = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
                lastResult = await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lastResult = $"{name}：已取消。";
            }
            catch (Exception ex)
            {
                lastResult = $"{name}：失敗（{ex.Message}）。";
                Svc.Log.Information($"[TataruPraise] {name} 失敗：{ex}");
            }
            finally
            {
                jobName = string.Empty;
                runningCategory = string.Empty;
                progress = string.Empty;
                Svc.Log.Information($"[TataruPraise] {name} 結束：{lastResult}");

                // 🔴 旗標最後才放：放掉的下一刻 UI 就可能按下一顆按鈕，
                // 上面那些欄位必須已經收乾淨了。
                Volatile.Write(ref runningFlag, 0);
            }
        }, token);

        return true;
    }

    /// <summary>
    /// 對一個情境發一次 Gemini 請求並把結果寫進池。回傳實際新增幾句。
    /// </summary>
    /// <remarks>
    /// 📌 <see cref="GeminiClient.GenerateAsync"/> 自己把 HTTP 錯誤、金鑰錯、重試耗盡都吞成空清單，
    /// 所以這裡「失敗」的正常形狀是<b>回 0</b>，不是擲例外。只有取消會往外丟。
    /// </remarks>
    private async Task<int> ExpandCategoryAsync(
        string category, int count, int maxLength, GenerateStats stats, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // 📌 situation ＝ 使用者在設定視窗填的「情境描述」（沒填就退回內建預設／鍵名）。
        var lines = await GeminiClient
            .GenerateAsync(
                config.GeminiApiKey, config.GeminiModel, category, config.SituationOf(category),
                count, config.MinLengthOf(category), maxLength, token, stats)
            .ConfigureAwait(false);

        var added = pool.AddLines(category, lines, out var duplicates);
        stats.Duplicate += duplicates;
        return added;
    }

    /// <summary>逐情境生成的結果行。</summary>
    /// <remarks>
    /// 🔴 「丟棄超長 N 句」<b>一律印出來，即使 N 是 0</b>：使用者要能分辨
    /// 「模型根本沒回東西」與「生了一堆但全都太長」，這兩件事的處置完全不同。
    /// </remarks>
    private static string DescribeOne(string category, int requested, int added, GenerateStats stats, int maxLength)
    {
        var head = $"情境「{category}」：要 {requested} 句、新增 {added} 句，{stats.DescribeAlways()}（上限 {maxLength} 字）。";

        if (added > 0)
            return head + "新句子還沒有語音，記得按同一列的「合成」。";

        return stats.AnyDropped
            ? head + "生回來的全被過濾掉了，把「句長上限」調大一點可能會好一些。"
            : head + "模型什麼都沒回（金鑰、模型名或額度的問題，詳見記錄檔）。";
    }

    /// <summary>
    /// 「全部擴充」：<b>逐情境各發一次請求</b>，順序執行、彼此獨立。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意<b>不</b>寫成「一次請求、請模型自己把 N 句分配到各情境」：那樣分出來的量是模型說了算，
    /// 而且一次失敗就整批沒有。逐情境各發一次的話，每個情境要的句數一樣，某一個情境失敗
    /// （429 重試耗盡、模型回垃圾）也只影響它自己。
    /// <para>
    /// 📌 各情境最後的<b>入池數還是會不一樣</b>——長度／標點／重複的過濾是逐句判的，良率本來就有差。
    /// 所以結果行印的是「要 N 句、實際 +M」，讓落差看得見；落後的那一類再用表格上的「生成」單獨補。
    /// </para>
    /// </remarks>
    private async Task<string> ExpandPoolAsync(CancellationToken token)
    {
        var count = ClampedGenerateCount();
        var categories = pool.Categories();

        var total = 0;
        var failures = 0;
        var details = new List<string>(categories.Count);
        var stats = new GenerateStats();

        for (var i = 0; i < categories.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var category = categories[i];
            progress = $"{i + 1}/{categories.Count}　{category}";
            runningCategory = category;

            var one = new GenerateStats();
            int added;
            try
            {
                added = await ExpandCategoryAsync(category, count, config.MaxLengthOf(category), one, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 🔴 單一情境炸掉不可以中斷整批：前面幾個情境已經寫進 pool.json 了。
                failures++;
                details.Add($"{category} 失敗");
                Svc.Log.Information($"[TataruPraise] 擴充情境「{category}」失敗：{ex}");
                continue;
            }

            stats.Add(one);
            total += added;
            details.Add($"{category} +{added}/{count}");
        }

        runningCategory = string.Empty;

        // 🔴 被過濾掉的數字要跟著結果一起回去：全部被丟掉的時候，「一句都沒加」與
        // 「生了 40 句但全都太長」是完全不同的兩件事，使用者要能分得出來。
        var dropped = stats.Describe();
        var droppedSuffix = dropped.Length > 0 ? $"（{dropped}；上限逐情境，全域 {config.ClampedMaxPraiseLength} 字）" : string.Empty;
        var failureSuffix = failures > 0 ? $"　有 {failures} 個情境整個失敗（詳見記錄檔）。" : string.Empty;

        if (total == 0)
        {
            return (stats.AnyDropped
                ? $"擴充誇獎池：一句都沒有加進去{droppedSuffix}。上限太緊的話可以把「句長上限」調大一點。"
                : "擴充誇獎池：一句都沒有加進去（金鑰、模型名或額度的問題，詳見記錄檔）。")
                + failureSuffix;
        }

        return $"擴充誇獎池：新增 {total} 句（{string.Join("、", details)}）{droppedSuffix}。"
             + "新句子還沒有語音，記得接著按「預合成語音快取」。"
             + failureSuffix;
    }

    private async Task<string> PrecacheAudioAsync(CancellationToken token)
    {
        var all = pool.Snapshot();

        // 同一句話可以同時掛在兩個情境底下，而快取檔名是句子的雜湊——去重之後才不會白合成兩次。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (var item in all)
        {
            if (!seen.Add(item.Text)) continue;
            if (!File.Exists(pool.CachePathFor(item.Text))) pending.Add(item.Text);
        }

        if (pending.Count == 0)
            return $"預合成語音快取：池裡 {all.Count} 句全部都已經有語音了，沒有要做的事。";

        var (ok, failed, error) = await SynthesizeTextsAsync(pending, token).ConfigureAwait(false);
        if (error != null) return $"預合成語音快取：{error}";

        var result = $"預合成語音快取：成功 {ok} 句";
        if (failed > 0)
            result += $"，失敗 {failed} 句（橋接連不上或聲線沒設定，詳見記錄檔）";
        return result + "。";
    }

    /// <summary>只合成一個情境裡缺 WAV 的句子。</summary>
    private async Task<string> SynthesizeCategoryAsync(string category, CancellationToken token)
    {
        var texts = pool.TextsOf(category);
        if (texts.Count == 0)
            return $"合成「{category}」：這個情境一句都沒有，沒有要做的事。";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (var text in texts)
        {
            if (!seen.Add(text)) continue;
            if (!File.Exists(pool.CachePathFor(text))) pending.Add(text);
        }

        if (pending.Count == 0)
            return $"合成「{category}」：{texts.Count} 句全部都已經有語音了，沒有要做的事。";

        var (ok, failed, error) = await SynthesizeTextsAsync(pending, token).ConfigureAwait(false);
        if (error != null) return $"合成「{category}」：{error}";

        var result = $"合成「{category}」：成功 {ok} 句";
        if (failed > 0)
            result += $"，失敗 {failed} 句（橋接連不上或聲線沒設定，詳見記錄檔）";
        return result + "。";
    }

    /// <summary>
    /// 把一批句子逐句送去橋接合成、寫成 WAV 快取。
    /// </summary>
    /// <remarks>
    /// 🔴 預合成用 60 秒逾時：一句可能要跑好幾秒，而這是使用者主動按的批次工作，不是遊戲中的即時路徑。
    /// </remarks>
    /// <returns>成功句數、失敗句數；<c>Error</c> 非 <c>null</c> 代表一句都還沒開始跑就中止了。</returns>
    private async Task<(int Ok, int Failed, string? Error)> SynthesizeTextsAsync(
        List<string> pending, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(pool.CacheDirectory);
        }
        catch (Exception ex)
        {
            return (0, 0, $"建立快取資料夾失敗（{ex.Message}）。");
        }

        var host = config.TtsHost;
        var apiKey = config.TtsApiKey;
        var voice = config.VoiceId;
        var ok = 0;
        var failed = 0;

        for (var i = 0; i < pending.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            progress = $"{i + 1}/{pending.Count}";

            var text = pending[i];

            var wav = await TtsBridge.SynthesizeAsync(host, voice, text, 60, apiKey).ConfigureAwait(false);
            if (wav == null)
            {
                failed++;
                continue;
            }

            try
            {
                var path = pool.CachePathFor(text);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, wav, token).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                pool.SetCachedWav(text, "cache/" + PraisePool.CacheFileName(text));
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Svc.Log.Information($"[TataruPraise] 寫入語音快取失敗：{ex.Message}");
            }
        }

        return (ok, failed, null);
    }

    public void Dispose()
    {
        try { cts?.Cancel(); } catch (Exception ex) { Svc.Log.Information($"[TataruPraise] 取消背景工作失敗：{ex.Message}"); }
        cts?.Dispose();
        cts = null;
    }
}
