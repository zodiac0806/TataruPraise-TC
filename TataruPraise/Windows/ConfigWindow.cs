using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TataruPraise.Core;

namespace TataruPraise.Windows;

/// <summary>
/// 設定視窗。
/// </summary>
/// <remarks>
/// UI 原則（艦隊慣例）：
/// <list type="bullet">
/// <item>「隨時掃視」的資訊放列上，「起疑才查」的放 tooltip。</item>
/// <item>🔴 但<b>「不知道」本身要在列上看得見</b>——橋接沒查過就寫「尚未查詢」，查不到就寫「未連線」，
/// 絕不畫成看起來正常的樣子。</item>
/// </list>
/// </remarks>
public sealed class ConfigWindow : Window
{
    private static readonly Vector4 ColorOk = new(0.36f, 0.83f, 0.45f, 1f);
    private static readonly Vector4 ColorBad = new(0.93f, 0.42f, 0.38f, 1f);
    private static readonly Vector4 ColorUnknown = new(0.65f, 0.65f, 0.65f, 1f);

    private readonly Plugin plugin;

    /// <summary>橋接查詢狀態。<see cref="SpeakerProbeState.NotProbed"/> 與「查了但沒有」是兩件事。</summary>
    private enum SpeakerProbeState
    {
        NotProbed,
        Probing,
        Ok,
        Failed,
    }

    private SpeakerProbeState probeState = SpeakerProbeState.NotProbed;
    private List<SpeakerInfo> speakers = [];
    private string probeMessage = string.Empty;

    public ConfigWindow(Plugin plugin) : base("塔塔露誇獎###TataruPraiseConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            // 🔴 「短句」分頁的表格是 7 欄（啟用／情境／短句／冷卻／語音／合成／試播）。
            //    ImGui 在沒有水平捲軸的表格裡，欄位放不下時是<b>壓縮欄寬</b>而不是切掉，
            //    失敗形狀＝欄位靜默變窄到看不見內容，所以最小寬要跟著欄數走。
            MinimumSize = new Vector2(720, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private Configuration Config => plugin.Config;

    public override void Draw()
    {
        DrawMasterSwitch();

        // 🔴 這一行是「為什麼句子這麼短」的唯一解釋，放在最上面、不藏 tooltip。
        //    使用者找不到長句誇獎在哪裡的時候，會以為外掛壞了而不是以為自己沒展開。
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "預設是每個情境一句極短音效提示；"
            + "要長句誇獎請到「短句」分頁的「進階」調高句長上限並用擴充池。");
        ImGui.PopTextWrapPos();

        DrawStatusLine();

        ImGui.Separator();

        if (ImGui.BeginTabBar("##TataruPraiseTabs"))
        {
            if (ImGui.BeginTabItem("觸發###tab-trigger"))
            {
                DrawTriggerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("語音###tab-voice"))
            {
                DrawVoiceTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("短句###tab-pool"))
            {
                DrawPoolTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }


    /// <summary>
    /// 視窗頂部的狀態列：語音快取進度 ＋ 上一次跟 9882 講話的結果。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>「還沒試過」與「試過但失敗」要分得開</b>，而且兩者都畫在列上。
    /// 把「沒試過」畫成正常的樣子會讓使用者以為橋接是通的——真相是我們根本沒連過。
    /// <para>
    /// 🔴 這一列<b>不會自己去連 9882</b>。它顯示的是「上一次真的送出去的合成」留下的結果，
    /// 由 <see cref="TtsBridge.LastSynthesisOk"/> 記著。要真的連線只有兩條路：
    /// 使用者按「合成」／「預合成全部」，或按「語音」分頁的「測試連線」。
    /// </para>
    /// </remarks>
    private void DrawStatusLine()
    {
        RefreshStatsIfStale();

        // ── 語音快取進度 ──
        if (poolTotalLines == 0)
        {
            ImGui.TextColored(ColorUnknown, "語音快取：池裡一句都沒有");
        }
        else if (poolCachedLines == 0)
        {
            ImGui.TextColored(ColorBad, $"語音快取 0/{poolTotalLines} 已合成（現在觸發不會出聲）");
        }
        else if (poolCachedLines < poolTotalLines)
        {
            ImGui.TextColored(ColorUnknown, $"語音快取 {poolCachedLines}/{poolTotalLines} 已合成");
        }
        else
        {
            ImGui.TextColored(ColorOk, $"語音快取 {poolCachedLines}/{poolTotalLines} 已合成");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "整池「有 WAV 檔的句數／總句數」。" + "\n"
                + "沒有 WAV 的句子永遠不會被挑到——挑句時就先把它們濾掉了，" + "\n"
                + "所以「有句子」跟「會出聲」是兩件事。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("｜");
        ImGui.SameLine();

        // ── 9882 的上次合成結果 ──
        var ok = TtsBridge.LastSynthesisOk;
        if (ok == null)
        {
            ImGui.TextColored(ColorUnknown, "9882：這次啟動後還沒合成過");
        }
        else
        {
            var stamp = TtsBridge.LastSynthesisAtLocal.ToString("HH:mm:ss");
            if (ok == true)
                ImGui.TextColored(ColorOk, $"9882：上次合成成功（{stamp}）");
            else
                ImGui.TextColored(ColorBad, $"9882：上次合成失敗（{stamp}）");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只有你按「合成」／「預合成全部」的時候才會連 9882。" + "\n"
                + "🔴 遊戲中的觸發完全不連網：那條路只播已經存在的 WAV 檔。" + "\n"
                + "（例外是別的外掛用 IPC Speak 念一句池裡沒有的話，那會即時合成一次。）" + "\n"
                + "失敗原因寫在記錄檔：404＝聲線沒設定、502＝橋接背後的 api_v2 連不上。");
        }
    }

    private void DrawMasterSwitch()
    {
        var enabled = Config.Enabled;
        if (ImGui.Checkbox("啟用（總開關）", ref enabled))
        {
            Config.Enabled = enabled;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("關掉的時候，遊戲事件、IPC 呼叫一律不出聲。下面的「試播一句」不受總開關限制。");

        ImGui.SameLine();
        var remaining = plugin.Service.CooldownRemainingSeconds;
        if (remaining > 0)
            ImGui.TextColored(ColorUnknown, $"（冷卻中，還有 {remaining:F0} 秒）");
        else if (plugin.Service.Audio.IsBusy)
            ImGui.TextColored(ColorUnknown, "（正在播放）");
        else
            ImGui.TextDisabled("（隨時可以出聲）");
    }

    private void DrawTriggerTab()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("每個觸發都是獨立開關，預設全部關閉。命中之後還要過全域冷卻與機率。");

        // 🔴 兩層開關要在列上講清楚：這裡開著卻不出聲的時候，使用者第一個念頭是「外掛壞了」。
        ImGui.TextDisabled("這裡是「事件層」；「短句」分頁每一列最左邊還有「情境層」的勾選——兩層都開才會響。");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "事件層＝這個遊戲事件要不要去叫那個情境。\n"
                + "情境層＝那個情境本身要不要出聲（也管別的外掛用 IPC Praise 叫它）。\n"
                + "任一層關著就不會出聲，而且是靜默的——所以兩層都畫在列上。");
        }

        ImGui.Spacing();

        var duty = Config.TriggerDutyComplete;
        if (ImGui.Checkbox("副本完成", ref duty)) { Config.TriggerDutyComplete = duty; Config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("來源是 Dalamud 的 IDutyState.DutyCompleted，不是聊天訊息比對。");

        ImGui.Indent();
        var firstClear = Config.FirstClearChancePercent;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("首次通關機率（%）##firstClear", ref firstClear, 0, 100))
        {
            Config.FirstClearChancePercent = Math.Clamp(firstClear, 0, 100);
            Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "第一次通關某個副本時改用這個機率（其他副本走下面的「觸發機率」）。\n"
                + "想關掉這個加權就把它調成跟「觸發機率」一樣。\n"
                + "🔴 「通關過沒有」是這個外掛自己記的，不是遊戲的通關紀錄——\n"
                + "裝外掛之前跑過的副本，第一次再跑照樣會算成首次通關。\n"
                + "反查不到副本資料的場景一律當一般副本處理。");
        }

        ImGui.TextColored(ColorUnknown, $"已記錄 {Config.ClearedDuties.Count} 個通關過的副本");
        ImGui.Unindent();


        var level = Config.TriggerLevelUp;
        if (ImGui.Checkbox("升等", ref level)) { Config.TriggerLevelUp = level; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只在等級真的往上跳的時候才算。\n"
                + "登入時客戶端會回報一次目前等級、切職業也會回報新職業的等級，那兩種都不會觸發。");
        }

        var login = Config.TriggerLogin;
        if (ImGui.Checkbox("登入", ref login)) { Config.TriggerLogin = login; Config.Save(); }

        ImGui.Indent();
        var loginOnce = Config.LoginOncePerDay;
        if (ImGui.Checkbox("只在當天第一次登入##loginOnce", ref loginOnce))
        {
            Config.LoginOncePerDay = loginOnce;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "以本機日期算「今天」。關掉就退回每次登入都試一次。\n"
                + "📌 日期只在真的出聲之後才記——冷卻擋掉、機率沒中、池裡沒有已合成的句子，\n"
                + "都不算今天用掉了，換角色重登還是有機會聽到。");
        }

        if (Config.LastLoginPraiseDate.Length > 0)
            ImGui.TextColored(ColorUnknown, $"上次登入誇獎：{Config.LastLoginPraiseDate}");
        else
            ImGui.TextColored(ColorUnknown, "上次登入誇獎：（還沒有過）");

        ImGui.Unindent();


        var gil = Config.TriggerGilMilestone;
        if (ImGui.Checkbox("Gil 里程碑", ref gil)) { Config.TriggerGilMilestone = gil; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "每 5 秒讀一次身上的 Gil，跨過設定的整數倍才觸發。\n"
                + "登入後第一次讀到的數字只當基準，不會觸發。");
        }

        ImGui.Indent();
        var step = (int)Math.Clamp(Config.GilMilestoneStep, 10_000, int.MaxValue);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputInt("每跨過多少 Gil 算一次##gilStep", ref step, 100_000, 1_000_000))
        {
            Config.GilMilestoneStep = Math.Clamp(step, 10_000, int.MaxValue);
            Config.Save();
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("戰鬥警示（只讀狀態、只出聲，不會做任何遊戲操作）");

        var lowHp = Config.TriggerLowHp;
        if (ImGui.Checkbox("血量低", ref lowHp)) { Config.TriggerLowHp = lowHp; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "戰鬥中血量跌破門檻時喊一聲。血量回到門檻以上才會重新上膛，\n"
                + "所以在門檻附近抖動不會一直喊。冷卻預設 15 秒（逐情境）。");
        }

        ImGui.Indent();
        var hpThreshold = Config.LowHpThresholdPercent;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("血量門檻（%）##lowHpThreshold", ref hpThreshold, 1, 99))
        {
            Config.LowHpThresholdPercent = Math.Clamp(hpThreshold, 1, 99);
            Config.Save();
        }

        ImGui.Unindent();

        var marked = Config.TriggerMarkedByMany;
        if (ImGui.Checkbox("被大量敵人標記（僅 PvP）", ref marked)) { Config.TriggerMarkedByMany = marked; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只在 PvP 區域跑。每 0.25 秒掃一次物件表，數「正在把我當目標」的敵對玩家。\n"
                + "敵對判定走 Dalamud 的 StatusFlags.Hostile。冷卻預設 10 秒。");
        }

        ImGui.Indent();
        var markedCount = Config.MarkedByManyCount;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("幾個人鎖定我才算##markedCount", ref markedCount, 1, 8))
        {
            Config.MarkedByManyCount = Math.Clamp(markedCount, 1, 8);
            Config.Save();
        }

        ImGui.Unindent();

        var behind = Config.TriggerEnemyBehind;
        if (ImGui.Checkbox("敵人從後面來（僅 PvP）", ref behind)) { Config.TriggerEnemyBehind = behind; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只在 PvP 區域跑。三個條件同時成立才算：在我的後半平面（夾角大於 90 度）、\n"
                + "距離在設定的碼數內、而且比上一次掃描時更近（正在接近）。\n"
                + "剛進視野的第一次不算——沒有比較基準。冷卻預設 10 秒。");
        }

        ImGui.Indent();
        var behindRange = Config.EnemyBehindRange;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat("距離（碼）##behindRange", ref behindRange, 1f, 50f, "%.0f"))
        {
            Config.EnemyBehindRange = Math.Clamp(behindRange, 1f, 50f);
            Config.Save();
        }

        var behindCount = Config.EnemyBehindCount;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("幾個人繞到後面才算##behindCount", ref behindCount, 1, 8))
        {
            Config.EnemyBehindCount = Math.Clamp(behindCount, 1, 8);
            Config.Save();
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("內建通知（只讀狀態、純出聲，不會替你按任何按鈕）");
        ImGui.Spacing();

        var tell = Config.TriggerTellReceived;
        if (ImGui.Checkbox("被密語", ref tell)) { Config.TriggerTellReceived = tell; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "有人傳密語給你的時候出聲。判準只有聊天類型（TellIncoming），不比對任何文字。" + "\n"
                + "自己送出去的密語（TellOutgoing）不算，所以不會自己喊自己。" + "\n"
                + "📌 情境鍵是「被密語」，跟外部外掛叫的「私訊」是兩個鍵——兩邊都開就會聽到兩次。");
        }

        var cfPop = Config.TriggerDutyPop;
        if (ImGui.Checkbox("副本排到（內建）", ref cfPop)) { Config.TriggerDutyPop = cfPop; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "副本配對排到的時候出聲（來源是 IClientState.CfPop，不是聊天訊息比對）。" + "\n"
                + "📌 情境鍵是「副本排到」，跟 NotificationMaster 用 IPC 叫的是同一個鍵。" + "\n"
                + "兩邊同時開也只會聽到一聲：這個情境的冷卻是 5 秒，緊接著的第二次會被吸掉。");
        }

        var partyInvite = Config.TriggerPartyInvite;
        if (ImGui.Checkbox("組隊邀請", ref partyInvite)) { Config.TriggerPartyInvite = partyInvite; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "有人邀你入隊、邀請視窗跳出來的時候出聲。" + "\n"
                + "🔴 只是聽那個視窗有沒有出現，不會替你按同意或拒絕。" + "\n"
                + "⚠️ 沒響的話多半是視窗名字對不上（失敗形狀就是不響、不會有錯誤訊息）。");
        }

        var trade = Config.TriggerTradeRequest;
        if (ImGui.Checkbox("交易請求", ref trade)) { Config.TriggerTradeRequest = trade; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "有人跟你要求交易、交易視窗跳出來的時候出聲。" + "\n"
                + "🔴 只是聽那個視窗有沒有出現，不會替你按任何按鈕。" + "\n"
                + "⚠️ 沒響的話多半是視窗名字對不上（失敗形狀就是不響、不會有錯誤訊息）。");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("這四條與警示一樣不擲機率骰（等同 100%），但照走各自的冷卻（5 秒）。");

        ImGui.Spacing();
        ImGui.TextDisabled("其餘情境（潛艇、製作、宇宙、任務開始…）沒有內建觸發，由別的外掛用 IPC 呼叫。");


        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var cooldown = Config.CooldownSeconds;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderInt("全域冷卻（秒）", ref cooldown, 0, 900))
        {
            Config.CooldownSeconds = cooldown;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("冷卻中的觸發直接丟棄，不會排隊等冷卻結束後補播。");

        var chance = Config.ChancePercent;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderInt("觸發機率（%）", ref chance, 0, 100))
        {
            Config.ChancePercent = chance;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("過了冷卻之後再擲一次骰。設 0 等於全部關掉。");
    }

    private void DrawVoiceTab()
    {
        ImGui.Spacing();

        var host = Config.TtsHost;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("橋接位址##ttsHost", ref host, 256))
        {
            Config.TtsHost = host;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "本機 GPT-SoVITS 橋接（gsv_bridge）的位址，預設 http://127.0.0.1:9882。\n"
                + "如果橋接跑在另一台機器，那邊要綁 0.0.0.0 並開防火牆，這裡填區網 IP。\n"
                + "也可以填任何相容的 HTTP API（需要 GET /speakers、POST / 這兩個接點）。\n"
                + "連不上的時候外掛只是不出聲，不會卡遊戲。");
        }

        var apiKey = Config.TtsApiKey;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("API Key##ttsApiKey", ref apiKey, 256, ImGuiInputTextFlags.Password))
        {
            Config.TtsApiKey = apiKey;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "非必填。有填的話每次請求會加上 Authorization: Bearer <key>。\n"
                + "本機、沒有驗證的橋接留空即可；架在需要驗證的服務或反向代理後面才需要填。\n"
                + "存在本機設定檔裡（明文），跟其他外掛設定一樣不會加密。");
        }

        ImGui.SameLine();
        if (ImGui.Button("測試連線##probe"))
            ProbeSpeakers();

        // 🔴 狀態畫在列上，不藏 tooltip：「不知道」跟「壞了」都要一眼看得出來。
        switch (probeState)
        {
            case SpeakerProbeState.NotProbed:
                ImGui.TextColored(ColorUnknown, "橋接狀態：尚未查詢（按上面的「測試連線」）");
                break;
            case SpeakerProbeState.Probing:
                ImGui.TextColored(ColorUnknown, "橋接狀態：查詢中…");
                break;
            case SpeakerProbeState.Ok:
                ImGui.TextColored(ColorOk, $"橋接狀態：已連線，{speakers.Count} 個聲線");
                break;
            case SpeakerProbeState.Failed:
                ImGui.TextColored(ColorBad, "橋接狀態：未連線");
                if (ImGui.IsItemHovered() && probeMessage.Length > 0)
                    ImGui.SetTooltip(probeMessage);
                break;
        }

        ImGui.Spacing();
        DrawVoicePicker();

        ImGui.Spacing();
        var volume = Config.Volume;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderFloat("音量", ref volume, 0f, 1f, "%.2f"))
        {
            Config.Volume = Math.Clamp(volume, 0f, 1f);
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("試播一句##test"))
            plugin.RunTest();

        ImGui.SameLine();
        ImGui.TextDisabled("（也可以用指令 /tataru test）");

        if (plugin.LastTestMessage.Length > 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(plugin.LastTestMessage);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawVoicePicker()
    {
        var current = Config.VoiceId;

        if (probeState == SpeakerProbeState.Ok && speakers.Count > 0)
        {
            ImGui.SetNextItemWidth(300f);
            if (ImGui.BeginCombo("聲線##voice", current))
            {
                foreach (var speaker in speakers)
                {
                    var id = speaker.VoiceId.Length > 0 ? speaker.VoiceId : speaker.Name;
                    var label = speaker.Name.Length > 0 && speaker.Name != id ? $"{speaker.Name}（{id}）" : id;
                    if (ImGui.Selectable($"{label}##voice-{id}", id == current))
                    {
                        Config.VoiceId = id;
                        Config.Save();
                    }
                }

                ImGui.EndCombo();
            }

            return;
        }

        // 沒有清單就退成手填，並且在列上寫清楚「這是沒查到，不是只有這一個」。
        var voice = current;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("聲線##voiceManual", ref voice, 64))
        {
            Config.VoiceId = voice;
            Config.Save();
        }

        ImGui.TextColored(
            ColorUnknown,
            probeState == SpeakerProbeState.Failed
                ? "聲線清單：未連線，只能手動填（目前的值照樣會拿去用）"
                : "聲線清單：尚未查詢，只能手動填");
    }

    /// <summary>
    /// 「短句」分頁。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>主畫面只有「一個情境一句短句」這件事</b>：改字、看有沒有語音、合成、試播。
    /// 多句、句長上限、Gemini 擴充全部收進<b>預設收合</b>的「進階」——那些是加上去的能力，不是主流程。
    /// 主流程＝<b>裝好 → 按「預合成全部」做語音 → 到「觸發」分頁開想要的事件</b>。
    /// </remarks>
    private void DrawPoolTab()
    {
        ImGui.Spacing();
        RefreshStatsIfStale();

        DrawShortLineTable();

        ImGui.Spacing();
        DrawPrecacheRow();

        ImGui.Spacing();
        DrawResetPool();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("進階：多句、句長上限、用 Gemini 擴充（可選）##advanced"))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("這底下的東西都不是必要的，不碰它也能用。");
            ImGui.Spacing();

            DrawPoolStats();

            ImGui.Spacing();
            DrawExpandRow();

            DrawLengthLimit();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawGeminiSettings();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("池與語音快取的位置");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{plugin.Pool.PoolPath}\n{plugin.Pool.CacheDirectory}");
    }

    /// <summary>
    /// 池統計的快取。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="PraisePool.CachedCountOf"/> 會對每一句做 <see cref="System.IO.File.Exists"/>。
    /// 直接畫在每一幀等於在 UI 執行緒上每秒做上千次磁碟查詢，所以每秒只重算一次。
    /// 副作用是預合成跑的時候數字最多晚一秒才跳——那正好是人看得懂的更新速度。
    /// </remarks>
    private readonly Dictionary<string, (int Total, int Cached)> statsCache = [];
    private DateTime statsRefreshedUtc = DateTime.MinValue;

    /// <summary>
    /// 要畫成表格列的情境清單（跟著統計一起、每秒重取一次）。
    /// </summary>
    /// <remarks>
    /// 📌 來源是 <see cref="PraisePool.Categories"/>，<b>包含 pool.json 裡的自訂鍵</b>——
    /// 內建四個在前，自訂的接在後面。
    /// </remarks>
    private List<string> categoryList = [];

    /// <summary>池裡超過目前句長上限的句數（跟著上面的統計一起、每秒重算一次）。</summary>
    private int overLimitCount;

    /// <summary>整池的句數與已有語音的句數（頂部狀態列用；跟著統計一起每秒重算）。</summary>
    private int poolTotalLines;

    /// <summary>整池已經有 WAV 的句數。</summary>
    private int poolCachedLines;

    /// <summary>「新增情境」輸入框的內容。</summary>
    private string newCategoryName = string.Empty;

    /// <summary>「新增情境」上一次失敗的原因（畫在列上，不藏 tooltip）。</summary>
    private string newCategoryError = string.Empty;

    /// <summary>正在展開編輯哪一個情境的描述（空字串＝沒有展開）。</summary>
    private string editingCategory = string.Empty;

    /// <summary>編輯中的描述草稿。<b>按「儲存」才寫進設定</b>，按「取消」整份丟掉。</summary>
    private string editingDescription = string.Empty;

    /// <summary>編輯中的句長上限覆寫草稿（0＝沿用預設／全域）。</summary>
    private int editingMaxLength;

    /// <summary>編輯中的句長下限覆寫草稿（0＝沿用）。</summary>
    private int editingMinLength;

    // 📌 冷卻沒有草稿欄位：它改在「短句」表格那一列上，改了就立刻寫進設定。
    //    這裡再放一份等於同一個值有兩個編輯入口，兩邊會互相蓋掉。

    /// <summary>
    /// 已經按過第一下「刪除」的情境（空字串＝沒有）。
    /// </summary>
    /// <remarks>
    /// 🔴 刪情境會連它底下的句子一起刪掉，<b>不可回復</b>，所以是兩段式：
    /// 第一下只是把確認列展開，第二下才真的動 pool.json。
    /// </remarks>
    private string pendingDeleteCategory = string.Empty;

    /// <summary>「移除超過上限的句子」按過第一下了嗎（第二下才真的刪）。</summary>
    private bool prunePending;

    /// <summary>「重置為預設池」按過第一下了嗎（第二下才真的重置）。</summary>
    private bool resetPending;

    /// <summary>上一幀有沒有工作在跑（用來在工作剛結束時立刻重算統計）。</summary>
    private bool jobWasRunning;

    /// <summary>
    /// 「短句」欄位的編輯草稿（情境 → 正在打的字）。
    /// </summary>
    /// <remarks>
    /// 🔴 只有<b>按 Enter 或移開焦點</b>才會寫進 pool.json（見 <see cref="CommitShortLine"/>）。
    /// 邊打邊存的話，打到一半的「危」會先變成這個情境唯一的句子，把舊句與它的 WAV 一起刪掉——
    /// 而那是不可回復的。
    /// <para>
    /// 📌 沒有草稿的情境顯示池裡的第一句；寫進去之後草稿就移除，讓顯示回到「池裡真的是什麼」。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, string> shortDrafts = [];

    /// <summary>短句編輯的結果訊息（畫在表格下面的列上，不藏 tooltip）。</summary>
    private string shortLineNotice = string.Empty;

    /// <summary>上面那則訊息是不是錯誤（決定顏色）。</summary>
    private bool shortLineNoticeIsError;

    /// <summary>「每次生成句數」輸入框的下界。</summary>
    private const int GenerateCountMin = 1;

    /// <summary>
    /// 「每次生成句數」輸入框的上界。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這只夾<b>使用者新輸入的值</b>。設定檔裡原本存著更大的數字（舊版滑桿到 50）不會被動到，
    /// <see cref="Core.PoolJobs"/> 那邊的上界仍然是 50——把既有設定靜默改小是回退使用者的選擇。
    /// </remarks>
    private const int GenerateCountMax = 30;

    private void RefreshStatsIfStale()
    {
        var now = DateTime.UtcNow;

        // 工作剛跑完就立刻重算一次：不然數字要等到下一個整秒才跳，看起來像「按了沒反應」。
        var running = plugin.Jobs.IsRunning;
        var justFinished = jobWasRunning && !running;
        jobWasRunning = running;

        if (!justFinished && categoryList.Count > 0 && now - statsRefreshedUtc < TimeSpan.FromSeconds(1)) return;
        statsRefreshedUtc = now;

        categoryList = plugin.Pool.Categories();

        // 🔴 重算前先清掉：自訂情境鍵會因為重置／手改 pool.json 而消失，
        // 只覆寫不清除的話，畫面上會留著一列已經不存在的情境。
        statsCache.Clear();
        poolTotalLines = 0;
        poolCachedLines = 0;
        foreach (var category in categoryList)
        {
            var total = plugin.Pool.CountOf(category);
            var cached = plugin.Pool.CachedCountOf(category);
            statsCache[category] = (total, cached);
            poolTotalLines += total;
            poolCachedLines += cached;
        }

        // 🔴 逐情境問上限：通知情境有自己的（較短的）上限，
        //    拿全域上限量整池會讓「有 N 句超長」跟按下去實際刪的數量對不上。
        overLimitCount = plugin.Pool.CountLongerThan(Config.MaxLengthOf);
    }

    /// <summary>設定裡的句長上限，夾在滑桿範圍內（設定檔被手改成離譜的值也不會讓 UI 壞掉）。</summary>
    private int ClampedMaxLength()
        => Math.Clamp(Config.MaxPraiseLength, PraiseText.SliderMin, PraiseText.SliderMax);

    /// <summary>
    /// 主畫面的短句表格：啟用 ｜ 情境 ｜ 短句（直接編輯）｜ 冷卻（直接編輯）｜ 語音 ｜ 合成 ｜ 試播。
    /// </summary>
    /// <remarks>
    /// 🔴 「這個情境有沒有語音」<b>一定要在列上看得見</b>：沒有 WAV 的情境是靜默不出聲的，
    /// 藏進 tooltip 的話使用者只會看到「開了但沒聲音」，而且完全沒有線索。
    /// <para>
    /// 🔴 情境有多句時<b>不可以只顯示第一句就當沒事</b>——挑句是隨機的，
    /// 只畫第一句會讓使用者以為那就是唯一會播的東西。所以語音欄改印「N/M 句」。
    /// </para>
    /// <para>
    /// 📌 每一列的按鈕在<b>任何</b>工作跑的時候都 disabled：池是整份重寫的，
    /// 兩個工作同時寫等於後寫的把先寫的整個蓋掉。
    /// </para>
    /// </remarks>
    private void DrawShortLineTable()
    {
        var running = plugin.Jobs.IsRunning;
        var runningCategory = plugin.Jobs.RunningCategory;

        ImGui.TextDisabled("每個情境一句短句。改完按 Enter 或點到別的地方就會寫進 pool.json。");
        ImGui.TextDisabled("最左邊的勾選＝這個情境要不要出聲；「冷卻」可以直接在列上改。");

        if (!ImGui.BeginTable("##shortLines", 7, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("啟用");
        ImGui.TableSetupColumn("情境");
        ImGui.TableSetupColumn("短句");

        // 🔴 冷卻欄給固定寬：清除鈕「×」只在「有自訂覆寫」時才出現，讓欄寬自動貼合的話，
        //    改一個數字整張表就會左右跳一下。
        ImGui.TableSetupColumn("冷卻（秒）", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("語音");
        ImGui.TableSetupColumn("合成");
        ImGui.TableSetupColumn("試播");
        ImGui.TableHeadersRow();

        for (var i = 0; i < categoryList.Count; i++)
        {
            var category = categoryList[i];
            var builtIn = PraiseCategory.IsBuiltIn(category);
            var (total, cached) = statsCache.TryGetValue(category, out var s) ? s : (0, 0);

            ImGui.TableNextRow();

            // ── 啟用：關掉的情境連別的外掛用 IPC 叫都不出聲（同一列的「試播」不受影響）──
            ImGui.TableNextColumn();
            var categoryEnabled = Config.IsCategoryEnabled(category);
            if (ImGui.Checkbox($"##enable-{i}", ref categoryEnabled))
                Config.SetCategoryEnabled(category, categoryEnabled);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"「{category}」要不要出聲。\n"
                    + $"關掉之後：內建觸發不出聲，別的外掛用 IPC Praise（「{category}」）叫它也不出聲（回 false）。\n"
                    + "「觸發」分頁的逐觸發勾選是另一層——兩層都開才會響。\n"
                    + "關掉不影響：同一列的「試播」照播，IPC Speak（呼叫端自己指定句子）也照念。");
            }

            // 🔴 關掉的那一列整列畫灰：這是「掃視就要看得見」的狀態，不可以只藏在勾選框裡。
            if (!categoryEnabled) ImGui.PushStyleColor(ImGuiCol.Text, ColorUnknown);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(builtIn ? category : category + " *");
            if (running && runningCategory == category)
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorUnknown, "（進行中）");
            }

            // ── 短句：直接編輯。Enter 與移開焦點都會走 IsItemDeactivatedAfterEdit。──
            ImGui.TableNextColumn();
            var draft = shortDrafts.TryGetValue(category, out var pending)
                ? pending
                : plugin.Pool.FirstTextOf(category) ?? string.Empty;

            ImGui.SetNextItemWidth(200f);
            if (ImGui.InputText($"##short-{i}", ref draft, 128))
                shortDrafts[category] = draft;

            if (ImGui.IsItemDeactivatedAfterEdit())
                CommitShortLine(category, draft);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "這個情境要念的那一句。改完按 Enter 或點到別的地方就會寫進 pool.json。\n"
                    + "🔴 送出之後這個情境就只剩你打的這一句，舊句與它的語音會被刪掉（不可回復）。\n"
                    + "改了字就要重新按「合成」——舊語音對不上新句子。");
            }

            // ── 冷卻：列上直接改。顯示的是<b>生效值</b>（自訂覆寫 → 內建 → 全域）──
            // 🔴 這裡刻意顯示「生效值」而不是「覆寫值」：畫成 0 代表「沒有覆寫」的話，
            //    使用者看到的是「冷卻 0 秒」，那是錯的資訊而不是「不知道」。
            ImGui.TableNextColumn();
            var hasCooldownOverride = Config.HasCooldownOverride(category);
            var builtinCooldown = PraiseCategory.DefaultCooldownSeconds(category);
            var fallbackText = builtinCooldown > 0
                ? $"預設 {builtinCooldown} 秒（來源：內建）"
                : $"預設 {Math.Max(0, Config.CooldownSeconds)} 秒（來源：全域）";
            var cooldown = Config.CooldownOf(category);

            ImGui.SetNextItemWidth(110f);
            if (ImGui.InputInt($"##cd-{i}", ref cooldown, 1, 10))
                Config.SetCooldown(category, Math.Clamp(cooldown, 0, 3600));

            if (ImGui.IsItemHovered())
            {
                var remaining = plugin.Service.CooldownRemainingSecondsOf(category);
                ImGui.SetTooltip(
                    (hasCooldownOverride
                        ? $"這個情境自訂了 {Config.CooldownOf(category)} 秒。"
                        : "沿用預設，沒有自訂。")
                    + $"\n{fallbackText}"
                    + "\n冷卻計時器是逐情境分開算的：這個情境的冷卻不會擋到別的情境。"
                    + $"\n目前狀態：{(remaining > 0 ? $"還要等 {remaining:F0} 秒" : "現在就可以出聲")}"
                    + "\n填 0（或按右邊的 ×）＝清掉自訂，退回上面那個預設。");
            }

            // 只有「真的有自訂覆寫」時才出現清除鈕——沒有覆寫時它按下去什麼都不會做。
            if (hasCooldownOverride)
            {
                ImGui.SameLine(0f, 4f);
                if (ImGui.SmallButton($"×##cdClear-{i}"))
                    Config.SetCooldown(category, 0);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"清掉「{category}」的冷卻自訂，退回{fallbackText}。");
            }

            // ── 語音：有／無要在列上看得見（沒有 WAV ＝ 靜默不出聲）──
            ImGui.TableNextColumn();
            if (total == 0)
            {
                ImGui.TextColored(ColorUnknown, "—");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("這個情境一句都沒有，先在左邊打一句短句。");
            }
            else if (total == 1)
            {
                ImGui.TextColored(
                    !categoryEnabled ? ColorUnknown : cached > 0 ? ColorOk : ColorBad,
                    cached > 0 ? "有" : "無");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        cached > 0
                            ? "這句已經有合成好的 WAV，事件發生時就會出聲。"
                            : "這句還沒有語音，按右邊的「合成」。沒有語音的情境不會出聲，而且是靜默的。");
                }
            }
            else
            {
                var color = !categoryEnabled
                    ? ColorUnknown
                    : cached == 0 ? ColorBad : cached < total ? ColorUnknown : ColorOk;
                ImGui.TextColored(color, $"{cached}/{total} 句");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"這個情境有 {total} 句（其中 {cached} 句有語音），播的時候是隨機挑一句。\n"
                        + "左邊顯示的是第一句。在那裡改字並送出，會把這個情境收成「只有你打的那一句」，\n"
                        + "其他句子與它們的語音一起刪掉（不可回復）。");
                }
            }

            // ── 合成：這個情境還缺語音的句子（出廠狀態下就是那一句）──
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(running || total == 0);
            if (ImGui.Button($"合成##shortSyn-{i}"))
                plugin.Jobs.StartSynthesizeCategory(category);
            ImGui.EndDisabled();

            // 🔴 這顆按鈕在 disabled 狀態下最需要 tooltip（使用者要問的正是「為什麼是灰的」），
            //    而 ImGui 預設不把 hover 算在 disabled 的項目上——一定要帶 AllowWhenDisabled。
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                var missing = total - cached;
                ImGui.SetTooltip(
                    total == 0
                        ? "這個情境一句都沒有，先在左邊打一句短句。"
                        : missing > 0
                            ? $"把「{category}」還缺語音的 {missing} 句送去橋接合成。"
                            : $"「{category}」沒有缺語音的句子，按了也不會做事。");
            }

            // ── 試播：不吃總開關、不吃冷卻，也不會把冷卻往後推 ──
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(cached == 0);
            if (ImGui.Button($"試播##shortTest-{i}"))
                plugin.RunCategoryTest(category);
            ImGui.EndDisabled();

            // 🔴 同上：沒有 WAV 的時候這顆是灰的，而那正是最需要說明的狀態。
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    cached > 0
                        ? $"現在播一次「{category}」目前的語音。總開關與這一列的「啟用」關著也照播，"
                          + "而且不會推遲真正的冷卻。"
                        : "尚未合成——先按左邊的「合成」做出語音才播得了。");
            }

            // 整列灰字的收尾。🔴 Push 與 Pop 必須成對，中途 continue 會讓後面的列全部變灰。
            if (!categoryEnabled) ImGui.PopStyleColor();
        }

        ImGui.EndTable();

        if (shortLineNotice.Length > 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(shortLineNoticeIsError ? ColorBad : ColorUnknown, shortLineNotice);
            ImGui.PopTextWrapPos();
        }

        if (plugin.LastTestMessage.Length > 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(plugin.LastTestMessage);
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    /// 把「短句」欄位的內容寫回 pool.json：這個情境變成<b>只有這一句</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 有工作在跑的時候<b>直接拒絕，而且把草稿留著</b>——池是整份重寫的，
    /// 這時候寫進去會被背景工作蓋掉；把使用者剛打的字丟掉更糟。
    /// <para>
    /// 📌 結果訊息一律寫在表格下面的列上：這是一個<b>破壞性</b>操作，
    /// 「刪了幾句、還要不要重新合成」不可以藏在 tooltip 裡。
    /// </para>
    /// </remarks>
    private void CommitShortLine(string category, string raw)
    {
        var text = PraiseText.Normalize(raw);

        if (text.Length == 0)
        {
            shortDrafts.Remove(category);
            shortLineNotice = $"「{category}」的短句不能是空的，這次沒有改動。";
            shortLineNoticeIsError = true;
            return;
        }

        if (plugin.Jobs.IsRunning)
        {
            // 草稿刻意留著：使用者剛打完的字不可以因為背景在跑就人間蒸發。
            shortLineNotice = "有工作正在跑，這次沒有寫進去。等它跑完再按一次 Enter。";
            shortLineNoticeIsError = true;
            return;
        }

        if (!plugin.Pool.SetSingleLine(category, text, out var deletedWavs, out var error))
        {
            shortDrafts.Remove(category);
            if (error == null) return;   // 內容一樣，什麼都沒動，也沒有話要說。

            shortLineNotice = error;
            shortLineNoticeIsError = true;
            return;
        }

        shortDrafts.Remove(category);
        statsRefreshedUtc = DateTime.MinValue;
        shortLineNoticeIsError = false;

        var hasVoice = plugin.Pool.CachedCountOf(category) > 0;
        shortLineNotice = $"「{category}」已改成「{text}」"
                          + (hasVoice ? "（這句已經有語音了）。" : "——還沒有語音，記得按同一列的「合成」。")
                          + (deletedWavs > 0 ? $"順便刪掉 {deletedWavs} 個沒人在用的舊語音。" : string.Empty);
    }
    /// <summary>
    /// 情境表格：每個情境一列，句數／已合成數／單獨生成／單獨合成。
    /// </summary>
    /// <remarks>
    /// 🔴 每一列的兩顆按鈕在<b>任何</b>工作跑的時候都是 disabled——不是只有同一列的那個。
    /// 池是整份重寫的，兩個工作同時寫等於後寫的把先寫的成果整個蓋掉。
    /// （真正的互斥在 <see cref="Core.PoolJobs"/> 的旗標，這裡的 disabled 只是讓使用者看得出來。）
    /// <para>
    /// 📌 正在處理的那一列會在情境名旁邊標「（進行中）」：整體進度在下面的結果行上，
    /// 但「現在動的是哪一列」要在列上看得見。
    /// </para>
    /// </remarks>
    private void DrawPoolStats()
    {
        RefreshStatsIfStale();

        var running = plugin.Jobs.IsRunning;
        var runningCategory = plugin.Jobs.RunningCategory;
        var effectiveCount = Math.Clamp(Config.GenerateCountPerCategory, 1, 50);

        ImGui.TextDisabled("誇獎池（每個情境：句數／已合成語音的句數）");

        // 「每次生成句數」放在表頭旁邊：它決定的就是下面每一列「生成」按下去會跟模型要幾句。
        ImGui.SameLine();
        var count = Config.GenerateCountPerCategory;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("每次生成句數##genCount", ref count, 1, 5))
        {
            Config.GenerateCountPerCategory = Math.Clamp(count, GenerateCountMin, GenerateCountMax);
            Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"按單一情境的「生成」、或按「全部擴充」時，每個情境跟模型要幾句（{GenerateCountMin}～{GenerateCountMax}）。\n"
                + "實際入池的會比較少：太長、太短、沒標點、跟池裡重複的都會被丟掉，\n"
                + "所以結果行印的是「要 N 句、新增 M 句」。");
        }

        DrawAddCategory();

        if (ImGui.BeginTable("##poolStats", 9, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("情境");
            ImGui.TableSetupColumn("情境描述");
            ImGui.TableSetupColumn("句長上限");
            ImGui.TableSetupColumn("冷卻(秒)");
            ImGui.TableSetupColumn("句數");
            ImGui.TableSetupColumn("已有語音");
            ImGui.TableSetupColumn("生成");
            ImGui.TableSetupColumn("語音");
            ImGui.TableSetupColumn("刪除");
            ImGui.TableHeadersRow();

            var customCount = 0;

            for (var i = 0; i < categoryList.Count; i++)
            {
                var category = categoryList[i];
                var builtIn = PraiseCategory.IsBuiltIn(category);
                if (!builtIn) customCount++;

                var (total, cached) = statsCache.TryGetValue(category, out var s) ? s : (0, 0);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(builtIn ? category : category + " *");
                if (!builtIn && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "自訂情境，沒有內建的遊戲觸發來源。\n"
                        + $"照樣可以生成與合成，也可以用 IPC TataruPraise.Praise(\"{category}\") 指定它播。");
                }

                if (running && runningCategory == category)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColorUnknown, "（進行中）");
                }

                // ── 情境描述：列上截斷、完整放 tooltip、按「編輯」展開多行 ──
                ImGui.TableNextColumn();
                var description = Config.DescriptionOf(category);
                if (description.Length == 0)
                {
                    // 🔴 「沒有描述」要在列上看得見：生句時只能拿鍵名當線索，句子會空泛。
                    ImGui.TextColored(ColorUnknown, "（沒有描述）");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("這個情境沒有描述，生句時只能用情境名當線索。按「編輯」補一段。");
                }
                else
                {
                    ImGui.TextUnformatted(Truncate(description, DescriptionPreviewChars));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
                        ImGui.SetTooltip(description);
                        ImGui.PopTextWrapPos();
                    }
                }

                ImGui.SameLine();
                if (ImGui.Button($"編輯##desc-{i}"))
                    BeginEditCategory(category);

                // ── 句長上限：沿用的畫灰、覆寫過的畫正常 ──
                ImGui.TableNextColumn();
                var effectiveMax = Config.MaxLengthOf(category);
                var overridden = Config.HasMaxLengthOverride(category)
                                 || PraiseCategory.DefaultMaxLength(category) > 0;
                if (overridden)
                    ImGui.TextUnformatted(effectiveMax.ToString());
                else
                    ImGui.TextColored(ColorUnknown, effectiveMax.ToString());

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        overridden
                            ? $"這個情境自己的句長上限是 {effectiveMax} 字（全域是 {Config.ClampedMaxPraiseLength} 字）。\n"
                              + "生成時提示詞要的字數也會跟著這個數字調整。按「編輯」可以改。"
                            : $"沿用全域上限 {effectiveMax} 字。按「編輯」可以只為這個情境設一個不一樣的上限。");
                }

                // ── 冷卻：沿用全域的畫灰，情境自己有的畫正常 ──
                ImGui.TableNextColumn();
                var effectiveCooldown = Config.CooldownOf(category);
                var ownCooldown = Config.HasCooldownOverride(category)
                                  || PraiseCategory.DefaultCooldownSeconds(category) > 0;
                if (ownCooldown)
                    ImGui.TextUnformatted(effectiveCooldown.ToString());
                else
                    ImGui.TextColored(ColorUnknown, effectiveCooldown.ToString());

                if (ImGui.IsItemHovered())
                {
                    var remaining = plugin.Service.CooldownRemainingSecondsOf(category);
                    ImGui.SetTooltip(
                        (ownCooldown
                            ? $"這個情境自己的冷卻是 {effectiveCooldown} 秒（全域是 {Config.CooldownSeconds} 秒）。"
                            : $"沿用全域冷卻 {effectiveCooldown} 秒。")
                        + "\n冷卻計時器是逐情境分開算的：這個情境的冷卻不會擋到別的情境。"
                        + $"\n目前狀態：{(remaining > 0 ? $"還要等 {remaining:F0} 秒" : "現在就可以出聲")}"
                        + "\n要改的話：在上面的短句表格那一列的「冷卻（秒）」直接改。");
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(total.ToString());

                ImGui.TableNextColumn();
                if (cached == 0 && total > 0)
                    ImGui.TextColored(ColorBad, "0");
                else if (cached < total)
                    ImGui.TextColored(ColorUnknown, cached.ToString());
                else
                    ImGui.TextColored(ColorOk, cached.ToString());

                ImGui.TableNextColumn();
                ImGui.BeginDisabled(running);
                if (ImGui.Button($"生成##gen-{i}"))
                    plugin.Jobs.StartExpandCategory(category);
                ImGui.EndDisabled();

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"只對「{category}」發一次 Gemini 請求，要 {effectiveCount} 句，只寫這一個情境。\n"
                        + $"上限 {effectiveMax} 字，提示詞會照這個上限要短句。\n"
                        + "其他情境完全不動。新句子還沒有語音，要再按同一列的「合成」。");
                }

                ImGui.TableNextColumn();
                ImGui.BeginDisabled(running);
                if (ImGui.Button($"合成##syn-{i}"))
                    plugin.Jobs.StartSynthesizeCategory(category);
                ImGui.EndDisabled();

                if (ImGui.IsItemHovered())
                {
                    var missing = total - cached;
                    ImGui.SetTooltip(
                        missing > 0
                            ? $"把「{category}」還缺語音的 {missing} 句送去橋接合成（可能要跑好幾分鐘）。"
                            : $"「{category}」目前沒有缺語音的句子。");
                }

                // ── 刪除：內建情境沒有這顆按鈕 ──
                ImGui.TableNextColumn();
                if (builtIn)
                {
                    ImGui.TextDisabled("內建");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "內建情境刪不掉：下次啟動會再被補回來（連同內建句），刪了等於白刪。\n"
                            + "不想聽到它的話，把對應的觸發關掉、或把句子清空就好。");
                    }
                }
                else
                {
                    ImGui.BeginDisabled(running);
                    if (ImGui.Button($"刪除##del-{i}"))
                    {
                        pendingDeleteCategory = category;
                        editingCategory = string.Empty;
                    }

                    ImGui.EndDisabled();

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"刪掉「{category}」與它底下的 {total} 句。按下去之後還會再確認一次。");
                }
            }

            ImGui.EndTable();

            if (customCount > 0)
                ImGui.TextDisabled("* ＝ 自訂情境（沒有內建觸發來源，靠 IPC 或手動叫）。");
        }

        DrawDeleteCategoryConfirm();
        DrawCategoryEditor();
    }

    /// <summary>情境描述在表格列上最多顯示幾個字（其餘收進 tooltip）。</summary>
    private const int DescriptionPreviewChars = 14;

    /// <summary>情境名的長度上限（避免有人貼一整段話進去把表格撐爆）。</summary>
    private const int CategoryNameMaxChars = 20;

    /// <summary>把長字串截短成「前 N 個字…」。</summary>
    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "…";

    /// <summary>
    /// 「新增情境」：輸入鍵名 → 建一個空情境。
    /// </summary>
    /// <remarks>
    /// 🔴 情境名同時是 <c>pool.json</c> 的鍵與 IPC <c>Praise</c> 的參數，所以：
    /// ①比對是 ordinal 完全相同（不做大小寫寬鬆）②不可以跟既有的重複
    /// ③失敗原因寫在列上，不藏 tooltip——按了沒反應是最糟的回饋。
    /// </remarks>
    private void DrawAddCategory()
    {
        var running = plugin.Jobs.IsRunning;

        var name = newCategoryName;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputText("##newCategory", ref name, 64))
        {
            newCategoryName = name;
            newCategoryError = string.Empty;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(running);
        if (ImGui.Button("新增情境##addCategory"))
            TryAddCategory();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "建一個新的（空的）情境。建好之後用同一列的「生成」生句、「合成」做語音。\n"
                + "別的外掛可以用 IPC TataruPraise.Praise(\"情境名\") 指定播這個情境。\n"
                + "情境名就是 pool.json 的鍵，大小寫與空白都算數。");
        }

        if (newCategoryError.Length > 0)
            ImGui.TextColored(ColorBad, newCategoryError);
    }

    private void TryAddCategory()
    {
        var name = newCategoryName.Trim();
        if (name.Length == 0)
        {
            newCategoryError = "情境名不能是空的。";
            return;
        }

        if (name.Length > CategoryNameMaxChars)
        {
            newCategoryError = $"情境名太長了（最多 {CategoryNameMaxChars} 個字）。";
            return;
        }

        if (plugin.Pool.HasCategory(name))
        {
            newCategoryError = $"「{name}」已經有了，不能重複。";
            return;
        }

        if (!plugin.Pool.AddCategory(name))
        {
            newCategoryError = $"新增「{name}」失敗（詳見記錄檔）。";
            return;
        }

        newCategoryName = string.Empty;
        newCategoryError = string.Empty;
        statsRefreshedUtc = DateTime.MinValue;
        BeginEditCategory(name);
    }

    /// <summary>刪除情境的第二段確認（第一段是表格列上那顆「刪除」）。</summary>
    /// <remarks>
    /// 📌 確認文字把<b>會刪掉幾句</b>寫在列上——這是使用者按下去之前唯一的煞車。
    /// 🔴 語音快取<b>不刪</b>：WAV 是用句子雜湊命名的，同一句可能還掛在別的情境底下。
    /// </remarks>
    private void DrawDeleteCategoryConfirm()
    {
        if (pendingDeleteCategory.Length == 0) return;

        var category = pendingDeleteCategory;
        if (!plugin.Pool.HasCategory(category))
        {
            pendingDeleteCategory = string.Empty;
            return;
        }

        var count = statsCache.TryGetValue(category, out var s) ? s.Total : plugin.Pool.CountOf(category);

        ImGui.Spacing();
        ImGui.TextColored(ColorBad, $"再按一次確認：會刪掉情境「{category}」與它底下的 {count} 句，不可回復。");
        ImGui.TextColored(ColorUnknown, "已經合成好的語音快取檔會留著（同一句可能還掛在別的情境底下）。");

        ImGui.BeginDisabled(plugin.Jobs.IsRunning);
        if (ImGui.Button($"確定刪除「{category}」##delConfirm"))
        {
            if (plugin.Pool.RemoveCategory(category, out var removed))
            {
                Config.CategoryDescriptions.Remove(category);
                Config.CategoryMaxLength.Remove(category);
                Config.CategoryMinLength.Remove(category);
                Config.CategoryCooldownSeconds.Remove(category);
                Config.CategoryEnabled.Remove(category);
                Config.Save();
                Svc.Log.Information($"[TataruPraise] 已刪除情境「{category}」（連同 {removed} 句）。");
            }

            if (string.Equals(editingCategory, category, StringComparison.Ordinal))
                editingCategory = string.Empty;

            pendingDeleteCategory = string.Empty;
            statsRefreshedUtc = DateTime.MinValue;
        }

        ImGui.SameLine();
        if (ImGui.Button("先不要##delCancel"))
            pendingDeleteCategory = string.Empty;

        ImGui.EndDisabled();
    }

    private void BeginEditCategory(string category)
    {
        editingCategory = category;
        editingDescription = Config.DescriptionOf(category);
        editingMaxLength = Config.HasMaxLengthOverride(category) ? Config.MaxLengthOf(category) : 0;
        editingMinLength = Config.HasMinLengthOverride(category) ? Config.MinLengthOf(category) : 0;
        pendingDeleteCategory = string.Empty;
    }

    /// <summary>
    /// 展開的情境編輯器：多行描述 ＋ 句長上限覆寫。
    /// </summary>
    /// <remarks>
    /// 🔴 草稿<b>按「儲存」才寫進設定</b>。邊打邊存會讓「打到一半的描述」變成生句時真的用的那一段。
    /// 📌 描述存在<b>設定檔</b>不是 pool.json——pool.json 只放句子，重置池的時候描述不會跟著被清掉。
    /// </remarks>
    private void DrawCategoryEditor()
    {
        if (editingCategory.Length == 0) return;

        var category = editingCategory;
        if (!plugin.Pool.HasCategory(category))
        {
            editingCategory = string.Empty;
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"編輯情境「{category}」");

        var description = editingDescription;
        ImGui.TextDisabled("情境描述（生句時餵給模型的那一段話）");
        if (ImGui.InputTextMultiline(
                "##editDescription", ref description, 1000,
                new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 4f)))
        {
            editingDescription = description;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "會被寫成「遊戲情境：<這段話>。」送進模型。\n"
                + "寫清楚「發生了什麼事」與「要多短」，生出來的句子才不會空泛。\n"
                + "留空＝退回內建的預設描述；自訂情境留空就只能用情境名當線索。");
        }

        var maxLength = editingMaxLength;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputInt("句長上限覆寫（0＝沿用）##editMaxLength", ref maxLength, 1, 5))
            editingMaxLength = Math.Clamp(maxLength, 0, PraiseText.SliderMax);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"只給這個情境用的句長上限（字，不含空白）。0＝沿用全域的 {Config.ClampedMaxPraiseLength} 字。\n"
                + "生成時提示詞要的字數範圍會跟著這個數字調整，硬過濾也照它走。\n"
                + "通知型的情境（潛艇／製作／宇宙）內建就是 16 字，因為那是短通知不是誇獎。");
        }

        var minLength = editingMinLength;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputInt("句長下限覆寫（0＝沿用）##editMinLength", ref minLength, 1, 5))
            editingMinLength = Math.Clamp(minLength, 0, PraiseText.SliderMax);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"只給這個情境用的句長下限。0＝沿用全域的 {PraiseText.MinLength} 字。\n"
                + "全域下限是拿來擋模型吐出來的殘句的；警示／通知情境的句子本來就很短，\n"
                + "不放寬下限的話生回來的東西會全部被當成殘句丟掉。");
        }

        // 📌 冷卻<b>不在這裡改</b>：它在「短句」表格那一列上直接編輯（改了就立刻生效）。
        //    同一個值開兩個編輯入口的話，這裡按「儲存」會把使用者剛在列上改的值蓋回草稿的舊值。
        ImGui.TextDisabled($"冷卻：{Config.CooldownOf(category)} 秒（在上面的短句表格那一列直接改）");

        var effective = editingMaxLength > 0
            ? Math.Clamp(editingMaxLength, PraiseText.MinLength, PraiseText.SliderMax)
            : Config.MaxLengthOf(category);
        var effectiveMin = editingMinLength > 0
            ? Math.Clamp(editingMinLength, 1, PraiseText.SliderMax)
            : Config.MinLengthOf(category);
        var (hintMin, hintMax) = PraiseText.LengthHint(effectiveMin, effective);
        ImGui.TextColored(ColorUnknown, $"生成時會跟模型要 {hintMin}～{hintMax} 字，超過 {effective} 字的直接丟掉。");

        if (ImGui.Button("儲存##editSave"))
        {
            Config.SetDescription(category, editingDescription);
            Config.SetMaxLength(category, editingMaxLength);
            Config.SetMinLength(category, editingMinLength);
            editingCategory = string.Empty;
            statsRefreshedUtc = DateTime.MinValue;
        }

        ImGui.SameLine();
        if (ImGui.Button("取消##editCancel"))
            editingCategory = string.Empty;

        ImGui.SameLine();
        if (ImGui.Button("還原成預設描述##editReset"))
        {
            editingDescription = PraiseCategory.DefaultDescription(category);
            editingMaxLength = 0;
            editingMinLength = 0;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "把草稿換成內建的預設描述、上限改回沿用。\n"
                + "還要按「儲存」才會生效。自訂情境沒有內建描述，會變成空的。");
        }

        ImGui.Separator();
    }

    /// <summary>
    /// 「重置為預設池」：兩段式確認。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不可回復</b>，而且動到的是使用者自己攢出來的整池句子。所以：
    /// ①第一下只是展開確認，第二下才真的執行；②執行前一定先備份 <c>pool.json</c>；
    /// ③備份失敗就整個中止（見 <see cref="PraisePool.ResetToDefault"/>）。
    /// <para>
    /// 📌 確認文字把<b>會刪什麼</b>寫在列上，不藏 tooltip——這是使用者按下去之前唯一的煞車。
    /// </para>
    /// </remarks>
    private void DrawResetPool()
    {
        var defaultCount = PraisePool.DefaultLineCount();

        ImGui.BeginDisabled(plugin.Jobs.IsRunning);

        if (!resetPending)
        {
            if (ImGui.Button("重置為預設池##resetPool"))
                resetPending = true;

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"把整池丟掉，回到內建的 {defaultCount} 句。\n"
                    + "🔴 不可回復。按下去之後還會再確認一次才真的執行。\n"
                    + "執行前會先把現在的 pool.json 備份成同一個資料夾裡的 pool.backup-<日期-時間>.json。");
            }
        }
        else
        {
            ImGui.TextColored(
                ColorBad,
                $"再按一次確認：會刪除目前池內所有句子與對應 WAV 快取，回到內建 {defaultCount} 句。");
            ImGui.TextColored(
                ColorUnknown,
                "執行前會先把現在的 pool.json 備份到同一個資料夾（pool.backup-<日期-時間>.json）。"
                + $"內建那 {defaultCount} 句重置後也沒有語音，要重新按一次「預合成語音快取」。");

            if (ImGui.Button("確定重置##resetConfirm"))
            {
                plugin.Jobs.StartResetPool();
                resetPending = false;
                statsRefreshedUtc = DateTime.MinValue;
            }

            ImGui.SameLine();
            if (ImGui.Button("先不要##resetCancel"))
                resetPending = false;
        }

        ImGui.EndDisabled();
    }

    private void DrawGeminiSettings()
    {
        ImGui.TextDisabled("Gemini（只在按「擴充誇獎池」時才會連網；遊戲中完全不用）");

        var key = Config.GeminiApiKey;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("API 金鑰##geminiKey", ref key, 256, ImGuiInputTextFlags.Password))
        {
            Config.GeminiApiKey = key;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("存在這個外掛的設定檔裡，不會進版控、不會寫進記錄檔。");

        // 🔴 遮罩過的欄位裡，「空的」與「填了但看不見」長得一模一樣。
        //    所以狀態要寫在列上——而且只寫「有沒有」，絕不回顯金鑰本身（連前幾碼都不行）。
        ImGui.SameLine();
        if (Config.GeminiApiKey.Length > 0)
            ImGui.TextColored(ColorOk, "（已設定）");
        else
            ImGui.TextColored(ColorUnknown, "（未設定）");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("金鑰不會顯示出來，也不會寫進記錄檔。要換就直接在左邊重打一次。");

        var model = Config.GeminiModel;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("模型##geminiModel", ref model, 128))
        {
            Config.GeminiModel = model;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設 gemini-3.5-flash-lite（快、省、額度高）。\n"
                + "其他可用：gemini-flash-lite-latest、gemini-3.6-flash。\n"
                + "gemini-2.x-flash 系列對新金鑰已停用，填了會回 404。");
        }

        // 📌 「每個情境生幾句」搬到上面的情境表格表頭旁邊了（同一個設定，沒有換欄位）：
        //    那顆數字直接決定每一列「生成」按下去的行為，放在按鈕旁邊才看得懂。
        ImGui.TextDisabled("（每次生成句數在上面的情境表格旁邊）");
    }

    /// <summary>
    /// 「全部擴充」（Gemini）——<b>只畫在「進階」收合區裡面</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 這顆按鈕會連網、要金鑰，而且生出來的是<b>多句</b>——與主畫面「一個情境一句」的形態相反。
    /// 所以它不可以出現在主畫面上：使用者按下去之後每個情境會多出好幾句，
    /// 而挑句是隨機的，聽起來就會變成「怎麼每次講的都不一樣」。
    /// </remarks>
    private void DrawExpandRow()
    {
        var running = plugin.Jobs.IsRunning;

        ImGui.BeginDisabled(running);
        if (ImGui.Button("全部擴充##expand"))
            plugin.Jobs.StartExpandPool();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "用 Gemini 擴充所有情境，寫進 pool.json。新句子還沒有語音。\n"
                + "📌 是「每個情境各發一次請求、各要一樣多句」，順序跑、彼此獨立——\n"
                + "某一個情境失敗不會影響其他情境。\n"
                + "各情境實際入池的數量還是會有差（過濾與重複的良率不同），\n"
                + "落後的那一類用上面表格那一列的「生成」單獨補就好。");
        }

    }

    /// <summary>
    /// 「預合成全部」＋取消＋進度／上次結果——<b>主畫面</b>那一排。
    /// </summary>
    /// <remarks>
    /// 📌 進度與結果刻意留在主畫面：「進階」裡的生成工作跑起來時，
    /// 使用者可能已經把收合區關掉了，把唯一的進度顯示藏在裡面等於「按了沒反應」。
    /// </remarks>
    private void DrawPrecacheRow()
    {
        var running = plugin.Jobs.IsRunning;

        ImGui.BeginDisabled(running);
        if (ImGui.Button("預合成全部##precache"))
            plugin.Jobs.StartPrecacheAudio();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把池裡還沒有語音的句子逐句送去橋接合成，存成 WAV 快取。可能要跑好幾分鐘。");

        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("取消##cancelJob"))
                plugin.Jobs.Cancel();
        }

        // 進度與結果都在列上（長文字才收進 tooltip）。
        if (running)
        {
            var progress = plugin.Jobs.Progress;
            ImGui.TextColored(
                ColorUnknown,
                progress.Length > 0 ? $"{plugin.Jobs.JobName}　進行中 {progress}" : $"{plugin.Jobs.JobName}　進行中…");
        }
        else
        {
            var last = plugin.Jobs.LastResult;
            if (last.Length == 0)
            {
                ImGui.TextDisabled("上次結果：（這次啟動後還沒跑過）");
            }
            else
            {
                const int maxOnRow = 48;
                var shown = last.Length > maxOnRow ? last[..maxOnRow] + "…" : last;
                ImGui.TextUnformatted($"上次結果：{shown}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(last);
            }
        }
    }

    /// <summary>
    /// 句長上限：生成端的硬過濾，外加一顆手動清理既有長句的按鈕。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>滑桿只影響「之後生的句子」。</b>pool.json 裡已經存在的長句是使用者的資料，
    /// 調滑桿不會動到它們——要清掉只能按下面那顆按鈕，而且按了還要再確認一次。
    /// 📌 「有幾句超過上限」畫在列上（不藏 tooltip）：使用者調完滑桿的下一秒就要看得到影響範圍。
    /// </remarks>
    private void DrawLengthLimit()
    {
        ImGui.Spacing();

        var maxLength = ClampedMaxLength();
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("句長上限（字）##maxPraiseLength", ref maxLength, PraiseText.SliderMin, PraiseText.SliderMax))
        {
            Config.MaxPraiseLength = Math.Clamp(maxLength, PraiseText.SliderMin, PraiseText.SliderMax);
            Config.Save();
            statsRefreshedUtc = DateTime.MinValue;
            prunePending = false;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "「擴充誇獎池」生回來的句子，超過這個字數就直接丟掉，不會進池（丟了幾句會寫在下面的結果與記錄檔裡）。\n"
                + "📌 這是「全域」上限：情境表格上每一列都可以自己覆寫（通知 16／10／12 字、警示 8 字）。\n"
                + "字數不含空白，中文標點算在內。提示詞要模型生幾個字會跟著有效上限自動算，\n"
                + "所以調這個滑桿也會改變生出來的句子有多長。\n"
                + "🔴 這個上限只擋新生成的句子；pool.json 裡已經有的長句不會被動到。");
        }

        var overLimit = overLimitCount;
        if (overLimit <= 0)
        {
            prunePending = false;
            ImGui.TextDisabled("池裡沒有超過各情境上限的句子。");
            return;
        }

        ImGui.TextColored(ColorUnknown, $"池裡有 {overLimit} 句超過該情境的上限（既有句子不會自動刪掉）");

        ImGui.BeginDisabled(plugin.Jobs.IsRunning);
        if (!prunePending)
        {
            if (ImGui.Button($"移除超過各情境上限的句子（{overLimit} 句）##prune"))
                prunePending = true;

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "把池裡超過上限的句子從 pool.json 刪掉，連同它們已經合成好的 WAV 快取一起刪。\n"
                    + "🔴 不可回復。按下去之後還會再確認一次才真的執行。");
            }
        }
        else
        {
            ImGui.TextColored(ColorBad, $"確定要刪掉這 {overLimit} 句與它們的語音快取嗎？不可回復。");
            if (ImGui.Button("確定移除##pruneConfirm"))
            {
                plugin.Jobs.StartPruneLongLines();
                prunePending = false;
                statsRefreshedUtc = DateTime.MinValue;
            }

            ImGui.SameLine();
            if (ImGui.Button("先不要##pruneCancel"))
                prunePending = false;
        }

        ImGui.EndDisabled();
    }

    private void ProbeSpeakers()
    {
        if (probeState == SpeakerProbeState.Probing) return;

        probeState = SpeakerProbeState.Probing;
        probeMessage = string.Empty;

        var host = Config.TtsHost;
        var apiKey = Config.TtsApiKey;
        _ = Task.Run(async () =>
        {
            var result = await TtsBridge.GetSpeakersAsync(host, apiKey).ConfigureAwait(false);
            if (result == null)
            {
                speakers = [];
                probeMessage = $"連不上 {host}/speakers。橋接沒開、位址不對或防火牆擋住都會這樣。詳細原因見記錄檔。";
                probeState = SpeakerProbeState.Failed;
                return;
            }

            speakers = result;
            probeState = SpeakerProbeState.Ok;
        });
    }
}
