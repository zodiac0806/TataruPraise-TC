using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TataruPraise.Core;

/// <summary>pool.json 裡的一句誇獎。</summary>
public sealed class PraiseLine
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;

    /// <summary>相對於外掛設定資料夾的語音快取路徑，例如 <c>cache/9f3a1c….wav</c>；還沒合成就是空字串。</summary>
    [JsonPropertyName("wav")] public string Wav { get; set; } = string.Empty;
}

/// <summary>
/// 誇獎池：<c>pool.json</c> 的讀寫、內建種子、挑句。
/// </summary>
/// <remarks>
/// 檔案放在 <c>Svc.PluginInterface.GetPluginConfigDirectory()</c> 底下，語音快取在其中的 <c>cache/</c>。
/// <para>
/// 🔴 所有公開成員都在同一把鎖底下：擴充池／預合成是背景 Task，而 UI 每幀都在讀同一份資料。
/// </para>
/// <para>
/// 📌 讀檔用的字典<b>保留不認得的鍵</b>（規格書列過「成就」「採集製作大成功」「連續登入」等
/// 這一版還沒有觸發來源的情境）——使用者自己加的東西不會在下一次存檔時被吃掉。
/// </para>
/// </remarks>
public sealed class PraisePool
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 不轉義 CJK：pool.json 是使用者會自己打開來看／編輯的檔案。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object gate = new();
    private readonly Random random = new();

    private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromSeconds(2);
    private readonly object availabilityGate = new();
    private DateTime availabilityCheckedUtc = DateTime.MinValue;
    private bool availabilityCache;

    /// <summary>
    /// 逐情境的「有沒有可播內容」快取（值＝上次檢查時間、結果）。
    /// </summary>
    /// <remarks>
    /// 🔴 跟 <see cref="availabilityCache"/> 分開存，快取期間共用
    /// <see cref="AvailabilityCacheDuration"/>：<see cref="HasCachedFor"/> 是<b>公開的 IPC 端點</b>，
    /// 呼叫端很可能在每幀路徑上問它，而每次問都要對那個情境的整串句子做 <see cref="File.Exists"/>。
    /// <para>
    /// 📌 用 <see cref="availabilityGate"/> 這同一把鎖，不另開一把：兩者都只是短暫的字典存取，
    /// 而多一把鎖就多一個上鎖順序要維護。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, (DateTime CheckedUtc, bool Result)> perCategoryAvailability
        = new(StringComparer.Ordinal);
    private readonly string dataDir;
    private readonly string poolPath;
    private readonly string cacheDir;

    private Dictionary<string, List<PraiseLine>> pool = [];

    public PraisePool()
    {
        dataDir = Svc.PluginInterface.GetPluginConfigDirectory();
        poolPath = Path.Combine(dataDir, "pool.json");
        cacheDir = Path.Combine(dataDir, "cache");
    }

    public string PoolPath => poolPath;

    public string CacheDirectory => cacheDir;

    /// <summary>句子 → 快取檔名（sha1 十六進位小寫 + .wav）。</summary>
    public static string CacheFileName(string text)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant() + ".wav";
    }

    /// <summary>某句話的語音快取絕對路徑（不保證存在）。</summary>
    public string CachePathFor(string text) => Path.Combine(cacheDir, CacheFileName(text));

    /// <summary>讀檔；讀不到或壞掉就當成空池（不擲例外，外掛照樣載入）。</summary>
    public void Load()
    {
        Dictionary<string, List<PraiseLine>>? loaded = null;
        try
        {
            if (File.Exists(poolPath))
            {
                // utf-8-sig：手動編輯過的檔常常帶 BOM，JsonSerializer 對 BOM 會直接擲例外。
                var json = File.ReadAllText(poolPath, Encoding.UTF8);
                loaded = JsonSerializer.Deserialize<Dictionary<string, List<PraiseLine>>>(json, JsonOpts);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 讀取誇獎池失敗，改用空池：{ex.Message}");
        }

        var seededKeys = new List<string>();
        var seededLines = 0;
        lock (gate)
        {
            pool = loaded ?? [];

            // 🔴 內建情境缺席時要「帶著內建句」補進來，不是補一個空陣列。
            //    版本更新新增的內建情境（潛艇／製作／宇宙）在既有使用者的 pool.json 裡沒有這個鍵，
            //    補成空陣列的話 IPC 呼叫端永遠拿到 false，而且完全沒有錯誤訊息——是靜默的沒作用。
            foreach (var key in PraiseCategory.All)
            {
                if (pool.ContainsKey(key)) continue;

                var seeded = new List<PraiseLine>();
                if (DefaultPool.Lines.TryGetValue(key, out var defaults))
                {
                    foreach (var text in defaults) seeded.Add(new PraiseLine { Text = text });
                }

                pool[key] = seeded;
                seededKeys.Add(key);
                seededLines += seeded.Count;
            }
        }

        if (seededKeys.Count == 0) return;

        // 🔴 立刻寫回去：不存的話，使用者這一輪按「生成」以外的任何操作都可能把新鍵洗掉，
        //    而且下次啟動又要再補一次（看起來像沒生效）。
        Save();
        Svc.Log.Information(
            $"[TataruPraise] 誇獎池補進新的內建情境：{string.Join("、", seededKeys)}"
            + $"（共 {seededLines} 句內建句）。這些句子還沒有語音，要按一次「合成」。");
    }

    /// <summary>存檔。失敗只寫 Information，不擲例外。</summary>
    public void Save()
    {
        string json;
        lock (gate)
        {
            json = JsonSerializer.Serialize(pool, JsonOpts);
        }

        try
        {
            Directory.CreateDirectory(dataDir);
            // 先寫暫存檔再換名：中途出錯不會把既有的池截成 0 bytes。
            var tmp = poolPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, poolPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 寫入誇獎池失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 池是空的時候灌進內建預設句。
    /// </summary>
    /// <remarks>
    /// 🔴 判準是「<b>整個池</b>一句都沒有」，不是「這個情境沒有句子」——後者會在使用者刻意清空某一類之後
    /// 每次啟動又偷偷長回來。回傳有沒有真的灌。
    /// </remarks>
    public bool SeedIfEmpty()
    {
        lock (gate)
        {
            foreach (var list in pool.Values)
            {
                if (list.Count > 0) return false;
            }

            foreach (var (category, lines) in DefaultPool.Lines)
            {
                if (!pool.TryGetValue(category, out var target))
                {
                    target = [];
                    pool[category] = target;
                }

                foreach (var text in lines)
                    target.Add(new PraiseLine { Text = text });
            }
        }

        Save();
        return true;
    }

    /// <summary>加句子進某個情境；已經有一模一樣的句子就跳過。回傳實際新增幾句。</summary>
    public int AddLines(string category, IEnumerable<string> texts) => AddLines(category, texts, out _);

    /// <summary>
    /// 加句子進某個情境；已經有一模一樣的句子就跳過。回傳實際新增幾句，
    /// <paramref name="duplicates"/> 回傳因為重複而沒有入池的句數。
    /// </summary>
    /// <remarks>
    /// 📌 重複的判準是<b>同一個情境裡</b>的完全相同字串（頭尾空白與引號已由
    /// <see cref="PraiseText.Normalize"/> 剝掉）。跨情境的相同句子刻意不擋——
    /// 同一句話在「升等」與「登入」都合用是正常的，而且語音快取是用句子雜湊命名的，不會多合成一次。
    /// </remarks>
    public int AddLines(string category, IEnumerable<string> texts, out int duplicates)
    {
        var added = 0;
        var skipped = 0;
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list))
            {
                list = [];
                pool[category] = list;
            }

            var seen = new HashSet<string>();
            foreach (var existing in list) seen.Add(existing.Text);

            foreach (var text in texts)
            {
                var trimmed = PraiseText.Normalize(text);
                if (trimmed.Length == 0) continue;
                if (!seen.Add(trimmed))
                {
                    skipped++;
                    continue;
                }

                list.Add(new PraiseLine { Text = trimmed });
                added++;
            }
        }

        duplicates = skipped;
        if (added > 0) Save();
        return added;
    }

    /// <summary>
    /// 匯出「已經合成好語音」的句子與對應的 wav 檔，打包成一個 zip：
    /// <c>manifest.json</c>（情境 → 句子清單）＋ <c>cache/&lt;sha1&gt;.wav</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 判準是<b>句子在池裡有紀錄，而且雜湊出來的 wav 檔真的存在</b>——不看 <see cref="PraiseLine.Wav"/>
    /// 那個字串欄位，跟 <see cref="TryTrigger"/> 選句時用的判準完全一致（見 <c>PraisePool.cs</c> 別處的
    /// <c>File.Exists(CachePathFor(text))</c>），這樣匯出的東西保證對方拿去用一定播得出來。
    /// <para>
    /// 📌 同一句話在多個情境重複用，雜湊檔名一樣，zip 裡只打包一份，不重複佔空間。
    /// </para>
    /// </remarks>
    /// <returns>成功回傳（匯出幾句、幾個不重複的音檔），失敗回傳 <see langword="null"/>（已經寫記錄檔）。</returns>
    public (int Lines, int Files)? ExportSynthesized(string zipPath)
    {
        Dictionary<string, List<string>> ready;
        lock (gate)
        {
            ready = pool
                .Select(kv => (kv.Key, Texts: kv.Value.Select(l => l.Text).Where(t => File.Exists(CachePathFor(t))).ToList()))
                .Where(x => x.Texts.Count > 0)
                .ToDictionary(x => x.Key, x => x.Texts);
        }

        if (ready.Count == 0)
        {
            Svc.Log.Information("[TataruPraise] 匯出誇獎池：目前沒有任何一句已經合成好語音，取消匯出。");
            return null;
        }

        try
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(ready, JsonOpts));
            }

            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            var lineCount = 0;
            foreach (var texts in ready.Values)
            {
                foreach (var text in texts)
                {
                    lineCount++;
                    var fileName = CacheFileName(text);
                    if (!seenFiles.Add(fileName)) continue;
                    zip.CreateEntryFromFile(CachePathFor(text), $"cache/{fileName}");
                }
            }

            return (lineCount, seenFiles.Count);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 匯出誇獎池失敗：{ex.Message}（{zipPath}）");
            return null;
        }
    }

    /// <summary>
    /// 從 <see cref="ExportSynthesized"/> 匯出的 zip 匯入。<b>只新增，不覆蓋、不刪除既有內容</b>：
    /// 句子走 <see cref="AddLines(string,System.Collections.Generic.IEnumerable{string},out int)"/>
    /// 既有的「同情境內完全相同的句子就跳過」邏輯；wav 檔案名是內容雜湊，本地已經有的就跳過，
    /// 不會有「該覆蓋哪一份」的問題（檔名相同＝內容相同）。
    /// </summary>
    /// <returns>
    /// 成功回傳（新增幾句、新增幾個音檔、因重複而跳過幾句），失敗（檔案不存在／不是本外掛匯出的格式／
    /// 讀取或寫入出錯）回傳 <see langword="null"/>（已經寫記錄檔）。
    /// </returns>
    public (int AddedLines, int AddedFiles, int SkippedLines)? ImportFrom(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            Svc.Log.Information($"[TataruPraise] 匯入誇獎池失敗：找不到檔案（{zipPath}）");
            return null;
        }

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry == null)
            {
                Svc.Log.Information("[TataruPraise] 匯入誇獎池失敗：這個 zip 不是本外掛匯出的誇獎池（缺少 manifest.json）。");
                return null;
            }

            Dictionary<string, List<string>>? manifest;
            using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            {
                manifest = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(reader.ReadToEnd(), JsonOpts);
            }

            if (manifest == null || manifest.Count == 0)
            {
                Svc.Log.Information("[TataruPraise] 匯入誇獎池失敗：manifest.json 是空的或格式不對。");
                return null;
            }

            Directory.CreateDirectory(cacheDir);

            var addedFiles = 0;
            foreach (var texts in manifest.Values)
            {
                foreach (var text in texts)
                {
                    var fileName = CacheFileName(text);
                    var destPath = Path.Combine(cacheDir, fileName);
                    if (File.Exists(destPath)) continue;

                    var wavEntry = zip.GetEntry($"cache/{fileName}");
                    if (wavEntry == null) continue;
                    wavEntry.ExtractToFile(destPath, overwrite: false);
                    addedFiles++;
                }
            }

            var addedLines = 0;
            var skippedLines = 0;
            foreach (var (category, texts) in manifest)
            {
                addedLines += AddLines(category, texts, out var duplicates);
                skippedLines += duplicates;
            }

            return (addedLines, addedFiles, skippedLines);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 匯入誇獎池失敗：{ex.Message}（{zipPath}）");
            return null;
        }
    }

    /// <summary>
    /// 整池有幾句超過上限（不含空白）。
    /// </summary>
    /// <remarks>
    /// 🔴 上限是<b>逐情境</b>問出來的（<paramref name="maxForCategory"/>）：情境可以各自覆寫上限，
    /// 拿全域上限一把尺量整池，會讓 UI 顯示的「有 N 句超長」跟按下去實際會刪的句數對不上。
    /// 顯示與刪除必須用同一個 resolver。
    /// </remarks>
    public int CountLongerThan(Func<string, int> maxForCategory)
    {
        lock (gate)
        {
            var n = 0;
            foreach (var (category, list) in pool)
            {
                var max = maxForCategory(category);
                foreach (var line in list)
                {
                    if (PraiseText.CountChars(line.Text) > max) n++;
                }
            }

            return n;
        }
    }

    /// <summary>
    /// 把整池裡超過<b>該情境上限</b>的句子刪掉，連同它們的語音快取。
    /// 回傳刪掉幾句，<paramref name="deletedWavs"/> 回傳實際刪掉幾個 WAV 檔。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>不可回復</b>的操作，而且動到的是使用者自己的資料——只能由使用者在設定視窗按按鈕觸發，
    /// 絕不可以在載入時、或改滑桿時自動跑。
    /// <para>
    /// 🔴 刪 WAV 之前要先確認整池真的沒有那句話了：同一句可以同時掛在兩個情境底下，而快取檔名是
    /// 句子的雜湊——只按「我剛刪了這筆」就去刪檔，會把另一個情境還在用的語音一起刪掉（而且是靜默的，
    /// 只有到播不出聲的時候才發現）。
    /// </para>
    /// </remarks>
    public int RemoveLongerThan(Func<string, int> maxForCategory, out int deletedWavs)
    {
        var removedTexts = new HashSet<string>();
        var removedCount = 0;
        var remaining = new HashSet<string>();

        lock (gate)
        {
            foreach (var (category, list) in pool)
            {
                var max = maxForCategory(category);
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (PraiseText.CountChars(list[i].Text) <= max) continue;
                    removedTexts.Add(list[i].Text);
                    list.RemoveAt(i);
                    removedCount++;
                }
            }

            foreach (var list in pool.Values)
            {
                foreach (var line in list) remaining.Add(line.Text);
            }
        }

        deletedWavs = 0;
        if (removedCount == 0) return 0;

        Save();

        foreach (var text in removedTexts)
        {
            if (remaining.Contains(text)) continue;

            var path = CachePathFor(text);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deletedWavs++;
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] 刪除語音快取失敗（{path}）：{ex.Message}");
            }
        }

        Svc.Log.Information($"[TataruPraise] 移除超過各情境上限的句子 {removedCount} 句，刪掉 {deletedWavs} 個語音快取。");
        return removedCount;
    }

    /// <summary>把某句話的快取路徑寫回池裡。</summary>
    public void SetCachedWav(string text, string relativePath)
    {
        var changed = false;
        lock (gate)
        {
            foreach (var list in pool.Values)
            {
                foreach (var line in list)
                {
                    if (line.Text != text || line.Wav == relativePath) continue;
                    line.Wav = relativePath;
                    changed = true;
                }
            }
        }

        if (changed) Save();
    }

    /// <summary>這個情境存在嗎（IPC 收到未知情境時要分得出「沒有這個鍵」與「有鍵但沒句子」）。</summary>
    public bool HasCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        lock (gate) return pool.ContainsKey(category);
    }

    /// <summary>
    /// 新增一個自訂情境（空的）。已經有同名的鍵就回 <c>false</c> 且什麼都不動。
    /// </summary>
    /// <remarks>
    /// 🔴 比對是 <b>ordinal 完全相同</b>：情境名同時是 pool.json 的鍵與 IPC 的參數，
    /// 「大小寫不同算同一個」在那兩個地方都不成立，這裡放寬只會製造對不上的鍵。
    /// </remarks>
    public bool AddCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;

        var name = category.Trim();
        lock (gate)
        {
            if (pool.ContainsKey(name)) return false;
            pool[name] = [];
        }

        Save();
        Svc.Log.Information($"[TataruPraise] 新增情境「{name}」。");
        return true;
    }

    /// <summary>
    /// 刪掉一個自訂情境，連同它底下的句子。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>內建情境刪不掉</b>（回 <c>false</c>）：<see cref="Load"/> 下次啟動又會把它補回來，
    /// 對使用者是「刪了又出現」的鬼打牆。UI 那邊也不畫刪除鈕，這裡是第二道閘門。
    /// <para>
    /// 🔴 <b>刻意不刪語音快取。</b>WAV 是用句子雜湊命名的，同一句可能還掛在別的情境底下；
    /// 而且使用者把情境刪掉再加回來的時候，留著的快取可以直接重用。
    /// 孤兒 WAV 由「重置為預設池」或使用者自己清。
    /// </para>
    /// </remarks>
    public bool RemoveCategory(string category, out int removedLines)
    {
        removedLines = 0;
        if (string.IsNullOrWhiteSpace(category)) return false;
        if (PraiseCategory.IsBuiltIn(category)) return false;

        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list)) return false;
            removedLines = list.Count;
            pool.Remove(category);
        }

        Save();
        Svc.Log.Information($"[TataruPraise] 刪除情境「{category}」（連同 {removedLines} 句；語音快取保留）。");
        return true;
    }

    /// <summary>某個情境目前有幾句。</summary>
    public int CountOf(string category)
    {
        lock (gate)
        {
            return pool.TryGetValue(category, out var list) ? list.Count : 0;
        }
    }

    /// <summary>
    /// 要列在 UI 上、也是「全部擴充」要跑的情境清單。
    /// </summary>
    /// <remarks>
    /// 內建情境在前（順序＝<see cref="PraiseCategory.All"/>），後面接 <c>pool.json</c> 裡出現過但
    /// 不在內建清單裡的自訂鍵。
    /// <para>
    /// 🔴 自訂鍵一定要列出來：<see cref="Load"/> 刻意保留不認得的鍵，如果 UI 只畫內建的四個，
    /// 使用者自己加的情境就會變成「檔案裡有、畫面上沒有」——那比沒有這個功能還糟。
    /// </para>
    /// <para>
    /// 📌 自訂鍵用 ordinal 排序，不用字典的列舉順序：<see cref="Dictionary{TKey,TValue}"/> 的列舉順序
    /// 沒有保證，拿它當 UI 順序會讓列偶爾跳動。
    /// </para>
    /// </remarks>
    public List<string> Categories()
    {
        var extras = new List<string>();
        lock (gate)
        {
            foreach (var key in pool.Keys)
            {
                if (Array.IndexOf(PraiseCategory.All, key) >= 0) continue;
                extras.Add(key);
            }
        }

        extras.Sort(StringComparer.Ordinal);

        var result = new List<string>(PraiseCategory.All.Length + extras.Count);
        result.AddRange(PraiseCategory.All);
        result.AddRange(extras);
        return result;
    }

    /// <summary>某個情境目前的所有句子（快照；情境不存在就回空清單）。</summary>
    public List<string> TextsOf(string category)
    {
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list)) return [];

            var texts = new List<string>(list.Count);
            foreach (var line in list) texts.Add(line.Text);
            return texts;
        }
    }

    /// <summary>某個情境的第一句（＝設定視窗「短句」欄位顯示的那一句）；沒有就回 <c>null</c>。</summary>
    /// <remarks>
    /// 📌 出廠狀態下每個情境只有一句，所以「第一句」就是「那一句」。
    /// 使用者用「進階」生了多句之後這裡回的仍是第一句，但<b>挑句是隨機的</b>
    /// （見 <see cref="PickCached"/>）——所以 UI 上要把「這個情境還有 N 句」寫在列上，
    /// 不然使用者會以為編輯框裡那句就是唯一會播的東西。
    /// </remarks>
    public string? FirstTextOf(string category)
    {
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list) || list.Count == 0) return null;
            return list[0].Text;
        }
    }

    /// <summary>
    /// 把某個情境換成「只有這一句」。
    /// </summary>
    /// <remarks>
    /// 🔴 這會<b>刪掉這個情境原本的其他句子</b>——設定視窗那個「短句」欄位就是這個語意：
    /// 一個情境一句提示。只能由使用者在欄位裡打完字按 Enter／移開焦點才會跑，
    /// 絕不可以在載入時或任何自動路徑上呼叫。
    /// <para>
    /// 🔴 刪 WAV 之前一定要確認<b>整池</b>真的沒有那句話了：同一句可以同時掛在兩個情境底下
    /// （出廠的「到旗標」與「抵達」就都是「到了！」），而快取檔名是句子的雜湊——
    /// 只按「我剛換掉這一筆」就去刪檔，會把另一個情境還在用的語音一起刪掉，而且是靜默的，
    /// 要到那個情境播不出聲才會發現。
    /// </para>
    /// <para>
    /// 📌 新句子如果<b>原本就在這個情境裡</b>，它的 <c>wav</c> 欄位會留著，不必重新合成。
    /// </para>
    /// </remarks>
    /// <param name="deletedWavs">實際刪掉幾個 WAV 檔。</param>
    /// <param name="error">失敗原因；成功或「本來就一樣」是 <c>null</c>。</param>
    /// <returns>有沒有真的改動。回 <c>false</c> 且 <paramref name="error"/> 是 <c>null</c> ＝內容一樣，什麼都沒動。</returns>
    public bool SetSingleLine(string category, string text, out int deletedWavs, out string? error)
    {
        deletedWavs = 0;
        error = null;

        var trimmed = PraiseText.Normalize(text);
        if (trimmed.Length == 0)
        {
            error = "短句不能是空的。";
            return false;
        }

        var removedTexts = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new HashSet<string>(StringComparer.Ordinal);
        var removedCount = 0;

        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list))
            {
                error = $"情境「{category}」不存在。";
                return false;
            }

            // 內容一模一樣就不要白寫一次檔（每幀失焦都會呼叫到這裡）。
            if (list.Count == 1 && string.Equals(list[0].Text, trimmed, StringComparison.Ordinal))
                return false;

            string? keptWav = null;
            foreach (var line in list)
            {
                if (string.Equals(line.Text, trimmed, StringComparison.Ordinal))
                {
                    keptWav = line.Wav;
                    continue;
                }

                removedTexts.Add(line.Text);
                removedCount++;
            }

            list.Clear();
            list.Add(new PraiseLine { Text = trimmed, Wav = keptWav ?? string.Empty });

            foreach (var other in pool.Values)
            {
                foreach (var line in other) remaining.Add(line.Text);
            }
        }

        Save();

        foreach (var removed in removedTexts)
        {
            if (remaining.Contains(removed)) continue;

            var path = CachePathFor(removed);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deletedWavs++;
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] 刪除語音快取失敗（{path}）：{ex.Message}");
            }
        }

        Svc.Log.Information(
            $"[TataruPraise] 情境「{category}」的短句改成「{trimmed}」"
            + $"（移除舊句 {removedCount} 句、刪掉 {deletedWavs} 個語音快取）。");
        return true;
    }

    /// <summary>
    /// 把整池重置回 <see cref="DefaultPool"/> 的內建句子。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不可回復的破壞性操作</b>，只能由使用者在設定視窗做完兩段式確認之後觸發，
    /// 絕不可以在載入時、改設定時、或任何自動路徑上跑。
    /// <para>
    /// 🔴 <b>備份不成功就整個中止，一個位元組都不動。</b>刪的是使用者自己攢了很久的整池句子，
    /// 沒有備份就不准動手。備份＝把現有的 <c>pool.json</c> 原封不動複製成
    /// <c>pool.backup-yyyyMMdd-HHmmss.json</c> 放在同一個資料夾；<b>不清舊備份、不輪替</b>
    /// （那也是使用者的資料，要刪由他自己刪）。
    /// </para>
    /// <para>
    /// 🔴 刪 WAV 的範圍<b>只有「重置前池裡真的存在的句子」的雜湊檔名</b>——
    /// <b>不掃 <c>cache/</c> 目錄</b>。掃目錄刪檔會把使用者手動放進去的東西一起清掉，而且是靜默的。
    /// </para>
    /// <para>
    /// ⚠️ 內建那些句子的 WAV <b>也會一起被刪掉</b>（它們本來就在舊池裡）：重置之後寫回去的是
    /// <b>沒有語音</b>的預設池，要重新按一次「預合成」。這是刻意的——
    /// 讓「重置完的狀態」永遠是同一個，不會因為使用者以前合成過什麼而長得不一樣。
    /// </para>
    /// </remarks>
    /// <param name="backupPath">舊池的備份絕對路徑；原本就沒有 <c>pool.json</c> 時是空字串。</param>
    /// <param name="removedLines">重置前池裡有幾句（重置中止時是 0）。</param>
    /// <param name="deletedWavs">實際刪掉幾個 WAV 檔。</param>
    /// <param name="error">中止原因；成功是 <c>null</c>。</param>
    /// <returns>有沒有真的重置。</returns>
    public bool ResetToDefault(out string backupPath, out int removedLines, out int deletedWavs, out string? error)
    {
        backupPath = string.Empty;
        removedLines = 0;
        deletedWavs = 0;
        error = null;

        // ① 先抄下「重置前有哪些句子」——等一下就照這份清單刪 WAV，不掃目錄。
        var oldTexts = new HashSet<string>(StringComparer.Ordinal);
        var oldCount = 0;
        lock (gate)
        {
            foreach (var list in pool.Values)
            {
                foreach (var line in list)
                {
                    oldCount++;
                    oldTexts.Add(line.Text);
                }
            }
        }

        // ② 備份。失敗就直接放棄，不進第三步。
        try
        {
            Directory.CreateDirectory(dataDir);
            if (File.Exists(poolPath))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var candidate = Path.Combine(dataDir, $"pool.backup-{stamp}.json");

                // 同一秒內按第二次也不可以蓋掉上一份備份。
                for (var i = 2; i < 100 && File.Exists(candidate); i++)
                    candidate = Path.Combine(dataDir, $"pool.backup-{stamp}-{i}.json");

                File.Copy(poolPath, candidate, overwrite: false);
                backupPath = candidate;
            }
        }
        catch (Exception ex)
        {
            error = $"備份舊池失敗（{ex.Message}），什麼都沒有動。";
            Svc.Log.Information($"[TataruPraise] 重置誇獎池中止：{error}");
            return false;
        }

        // ③ 換成內建預設池（含自訂鍵在內，整個字典重建）。
        lock (gate)
        {
            pool = [];
            foreach (var (category, lines) in DefaultPool.Lines)
            {
                var target = new List<PraiseLine>(lines.Count);
                foreach (var text in lines) target.Add(new PraiseLine { Text = text });
                pool[category] = target;
            }

            foreach (var key in PraiseCategory.All) pool.TryAdd(key, []);
        }

        Save();
        removedLines = oldCount;

        // ④ 刪 WAV：只刪剛才抄下來那份清單裡的句子。
        foreach (var text in oldTexts)
        {
            var path = CachePathFor(text);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deletedWavs++;
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] 刪除語音快取失敗（{path}）：{ex.Message}");
            }
        }

        var where = backupPath.Length > 0 ? backupPath : "（原本就沒有 pool.json，沒有備份）";
        Svc.Log.Information(
            $"[TataruPraise] 重置誇獎池：清掉 {removedLines} 句、刪掉 {deletedWavs} 個語音快取，舊池備份於 {where}。");
        return true;
    }

    /// <summary>內建預設池一共有幾句（UI 的確認文字要寫得出這個數字）。</summary>
    public static int DefaultLineCount()
    {
        var n = 0;
        foreach (var lines in DefaultPool.Lines.Values) n += lines.Count;
        return n;
    }

    /// <summary>某個情境有幾句「語音快取檔真的在磁碟上」。</summary>
    public int CachedCountOf(string category)
    {
        List<string> texts;
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list)) return 0;
            texts = new List<string>(list.Count);
            foreach (var line in list) texts.Add(line.Text);
        }

        var n = 0;
        foreach (var text in texts)
        {
            if (File.Exists(CachePathFor(text))) n++;
        }

        return n;
    }

    /// <summary>整池所有句子的快照（擴充池／預合成用）。</summary>
    public List<(string Category, string Text)> Snapshot()
    {
        var result = new List<(string, string)>();
        lock (gate)
        {
            foreach (var (category, list) in pool)
            {
                foreach (var line in list) result.Add((category, line.Text));
            }
        }

        return result;
    }

    /// <summary>
    /// 從某個情境挑一句「語音快取真的存在」的話。挑不到回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 只挑有快取的：純池模式的整個賣點就是執行期零 HTTP，挑到沒合成的句子只會靜默不出聲，
    /// 使用者會以為外掛壞了。這裡直接把沒快取的濾掉，讓「有東西可播」與「沒東西可播」分得開。
    /// </remarks>
    public string? PickCached(string category)
    {
        List<string> candidates = [];
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list) || list.Count == 0) return null;
            foreach (var line in list) candidates.Add(line.Text);
        }

        // 過濾與挑選都在鎖外做：File.Exists 是磁碟 I/O，不要拿著鎖去等它。
        var playable = new List<string>(candidates.Count);
        foreach (var text in candidates)
        {
            if (File.Exists(CachePathFor(text))) playable.Add(text);
        }

        if (playable.Count == 0) return null;
        lock (gate)
        {
            return playable[random.Next(playable.Count)];
        }
    }

    /// <summary>
    /// 任何一個情境有可播的內容嗎（IPC 的 <c>IsAvailable</c> 用）。
    /// </summary>
    /// <remarks>
    /// 🔴 結果快取 2 秒。這個方法會對整池做 <see cref="File.Exists"/>，而它是<b>公開的 IPC 端點</b>——
    /// 呼叫端很可能在自己的每幀迴圈裡問它，沒有快取的話等於幫別人的外掛裝了一台磁碟壓力機。
    /// 2 秒的誤差對「現在能不能出聲」這個問題沒有意義（預合成本來就要跑好幾分鐘）。
    /// </remarks>
    public bool HasAnyCached()
    {
        lock (availabilityGate)
        {
            var now = DateTime.UtcNow;
            if (now - availabilityCheckedUtc < AvailabilityCacheDuration) return availabilityCache;
            availabilityCheckedUtc = now;
        }

        // 📌 走 Categories() 而不是 PraiseCategory.All：自訂情境也算「有東西可播」，
        //    只數內建的會讓「整池只有自訂情境有語音」的人拿到 IsAvailable=false。
        var any = false;
        foreach (var category in Categories())
        {
            if (PickCached(category) == null) continue;
            any = true;
            break;
        }

        lock (availabilityGate) availabilityCache = any;
        return any;
    }

    /// <summary>
    /// <b>這一個情境</b>有沒有可播的內容（IPC 的 <c>IsAvailableFor</c> 用）。
    /// </summary>
    /// <remarks>
    /// 🔴 結果逐情境快取 <see cref="AvailabilityCacheDuration"/>，理由同 <see cref="HasAnyCached"/>：
    /// 這條路徑會被別的外掛從自己的每幀迴圈上叫。
    /// <para>
    /// 📌 情境不存在（呼叫端把鍵名打錯、或使用者還沒建那個情境）時回 <c>false</c>——
    /// 對呼叫端而言「沒有這個池」跟「池是空的」要做的事情一樣：不要走這條通知路徑。
    /// </para>
    /// <para>
    /// ⚠️ 快取<b>不會</b>因為預合成完成而立刻失效，最多晚 2 秒才看得到新合出來的語音。
    /// 那 2 秒對「現在能不能出聲」沒有意義（合成本來就要跑好幾分鐘）。
    /// </para>
    /// </remarks>
    public bool HasCachedFor(string category)
    {
        if (string.IsNullOrEmpty(category)) return false;

        var now = DateTime.UtcNow;
        lock (availabilityGate)
        {
            if (perCategoryAvailability.TryGetValue(category, out var entry)
                && now - entry.CheckedUtc < AvailabilityCacheDuration)
            {
                return entry.Result;
            }
        }

        // 🔴 PickCached 會做磁碟 I/O，刻意在鎖外跑（它自己有 gate 保護 pool 字典）。
        var has = PickCached(category) != null;

        lock (availabilityGate) perCategoryAvailability[category] = (now, has);
        return has;
    }
}
