using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TataruPraise.Core;

/// <summary>橋接回報的一個聲線。</summary>
public sealed class SpeakerInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("voice_id")] public string VoiceId { get; set; } = string.Empty;
}

/// <summary>
/// GPT-SoVITS 橋接（預設 <c>http://127.0.0.1:9882</c>）的用戶端。
/// </summary>
/// <remarks>
/// 接點只有兩個：
/// <list type="bullet">
/// <item><c>GET /speakers</c> → <c>[{"name":…,"voice_id":…}]</c>（唯讀）</item>
/// <item><c>POST /</c> 帶 <c>{"text","text_lang":"zh","ref_audio_path"}</c> → body 就是 WAV bytes</item>
/// </list>
/// <para>
/// 📌 <b>只送這三個欄位</b>。橋接會依 <c>ref_audio_path</c> 的檔名對到聲線，自動補真實參考音訊、
/// 日文逐字稿、<c>prompt_lang</c> 以及全部穩定化參數（切段、temperature、repetition_penalty…都在 server 端）。
/// 在插件這邊多送參數只會和 server 端的調校打架。
/// </para>
/// <para>
/// 🔴 <b>送去的中文必須含自然標點。</b> 無標點的長句會讓聲線越念越高變成怪腔（實測結論）。
/// 這條約束落在文字來源那一端（<see cref="DefaultPool"/> 的內建句、<see cref="GeminiClient"/> 的提示詞），
/// 這裡只轉發。
/// </para>
/// <para>
/// 🔴 <b>連不上就只是不出聲。</b> 所有方法都不擲例外給呼叫端，失敗回 <c>null</c> 並寫一行 Information。
/// 錯誤語意：<c>404</c>＝聲線沒設定、<c>502</c>＝橋接背後的 api_v2 連不上。
/// </para>
/// </remarks>
public static class TtsBridge
{
    /// <summary>
    /// 全外掛共用一個 <see cref="HttpClient"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>Timeout</c> 刻意設得很寬，<b>真正的逾時一律由呼叫端的 <see cref="CancellationTokenSource"/> 決定</b>——
    /// 因為即時合成（10 秒）與預合成（60 秒）需要不同的耐心，而 <c>HttpClient.Timeout</c> 是整個實例共用的。
    /// </remarks>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把設定裡的 host 正規化成沒有結尾斜線的 base URL。</summary>
    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().TrimEnd('/');
        if (trimmed.Length == 0) return "http://127.0.0.1:9882";
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed;
    }

    /// <summary>
    /// 塔塔露這一類聲線的參考音訊路徑。
    /// </summary>
    /// <remarks>
    /// 🔴 資料夾名是<b>簡體</b>的「参考音频」——那是橋接那台機器上的實際目錄名，不是筆誤，
    /// 不要「順手改成繁體」，改了橋接會回 404。
    /// </remarks>
    public static string RefAudioPathFor(string voiceId)
    {
        var id = string.IsNullOrWhiteSpace(voiceId) ? "塔塔露" : voiceId.Trim();
        return $"./参考音频/{id}.wav";
    }

    /// <summary>
    /// 非空時，幫這次請求加上 <c>Authorization: Bearer &lt;key&gt;</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意每次請求各自建一個 <see cref="HttpRequestMessage"/>，不動 <see cref="Http"/> 的
    /// <c>DefaultRequestHeaders</c>。</b><see cref="Http"/> 是整個外掛共用的靜態實例，
    /// 若把 API Key 寫進預設標頭，使用者在設定視窗改了 Key 或清空之後，
    /// 舊值還是會留在下一次請求裡——這裡才是唯一會被讀到「當下設定值」的地方。
    /// </remarks>
    private static void ApplyAuth(HttpRequestMessage request, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    /// <summary>列出聲線。失敗回 <c>null</c>（與「回了空陣列」分得開）。</summary>
    public static async Task<List<SpeakerInfo>?> GetSpeakersAsync(string host, string apiKey = "", int timeoutSeconds = 10)
    {
        var url = NormalizeHost(host) + "/speakers";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, apiKey);
            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var hint = code is 401 or 403 ? "（API Key 不對或沒填）" : string.Empty;
                Svc.Log.Information($"[TataruPraise] 取得聲線清單失敗：HTTP {code}{hint}（{url}）");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<SpeakerInfo>>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 取得聲線清單失敗：{ex.Message}（{url}）");
            return null;
        }
    }

    /// <summary>上一次合成的結果：<c>null</c>＝這次啟動後<b>還沒試過</b>。</summary>
    /// <remarks>
    /// 🔴 「還沒試過」與「試過但失敗」必須分得開。設定視窗上那一列若把「沒試過」畫成
    /// 看起來正常的樣子，使用者會以為橋接是通的——而真相是我們根本沒連過。
    /// <para>
    /// 📌 這裡記的是<b>合成</b>（<see cref="SynthesizeAsync"/>），不含 <c>/speakers</c> 探測。
    /// 兩者失敗的原因不一樣（探測通了不代表合成得出來，例如聲線沒設定就是 404）。
    /// </para>
    /// <para>
    /// ⚠️ 只有靜態欄位、沒有鎖：寫入端是背景合成工作，讀取端是 UI 執行緒。
    /// 讀到上一瞬間的值對「顯示上次結果」沒有影響，加鎖反而讓 UI 有機會等背景 I/O。
    /// </para>
    /// </remarks>
    public static bool? LastSynthesisOk { get; private set; }

    /// <summary>上一次合成的<b>本機</b>時間；還沒試過就是 <see cref="DateTime.MinValue"/>。</summary>
    public static DateTime LastSynthesisAtLocal { get; private set; } = DateTime.MinValue;

    private static void RecordSynthesis(bool ok)
    {
        LastSynthesisOk = ok;
        LastSynthesisAtLocal = DateTime.Now;
    }

    /// <summary>
    /// 合成一句話，回 WAV bytes。失敗回 <c>null</c>。
    /// </summary>
    /// <param name="host">橋接位址。</param>
    /// <param name="voiceId">聲線 id。</param>
    /// <param name="text">要念的中文（<b>必須含自然標點</b>）。</param>
    /// <param name="timeoutSeconds">即時合成建議 10 秒；預合成按鈕用 60 秒。</param>
    /// <param name="apiKey">非空時加上 <c>Authorization: Bearer</c>，見 <see cref="ApplyAuth"/>。</param>
    public static async Task<byte[]?> SynthesizeAsync(string host, string voiceId, string text, int timeoutSeconds, string apiKey = "")
    {
        var url = NormalizeHost(host) + "/";
        var payload = new Dictionary<string, string>
        {
            ["text"] = text,
            ["text_lang"] = "zh",
            ["ref_audio_path"] = RefAudioPathFor(voiceId),
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
            };
            ApplyAuth(request, apiKey);
            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var hint = code switch
                {
                    401 or 403 => "（API Key 不對或沒填）",
                    404 => "（聲線沒有設定）",
                    502 => "（橋接背後的 api_v2 連不上）",
                    _ => string.Empty,
                };
                Svc.Log.Information($"[TataruPraise] 語音合成失敗：HTTP {code}{hint}（{url}）");
                RecordSynthesis(false);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (bytes.Length < 44)
            {
                // WAV 檔頭至少 44 bytes。比這短一定不是音訊，不要送進 NAudio。
                Svc.Log.Information($"[TataruPraise] 語音合成回傳的內容太短（{bytes.Length} bytes），當成失敗。");
                RecordSynthesis(false);
                return null;
            }

            RecordSynthesis(true);
            return bytes;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 語音合成失敗：{ex.Message}（{url}）");
            RecordSynthesis(false);
            return null;
        }
    }
}
