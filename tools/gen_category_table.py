"""從 Core/PraiseCategory.cs 逐字產生 README 的情境表。

用法（repo 根）：
    python tools/gen_category_table.py          # 印出表格
    python tools/gen_category_table.py --write  # 直接寫回 README.md 的情境表區塊

為什麼要有這個腳本：情境清單是 IPC 呼叫端的契約，README 上那張表過期的失敗形狀是
「消費端照 README 只接了 7 個鍵，其他 13 個永遠不會被呼叫」——而且完全沒有錯誤訊息。
所以表格改成從原始碼產生，而且 SOURCES 少一筆就直接擲例外，不會靜默印出殘缺的表。
"""

import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__))).replace(os.sep, "/")
CS = REPO + "/TataruPraise/Core/PraiseCategory.cs"
README = REPO + "/README.md"

BEGIN = "<!-- BEGIN 情境表 -->"
END = "<!-- END 情境表 -->"

# 每個情境的「類型」與「誰觸發」。⚠️ 新增情境時這裡也要補一筆，否則腳本會擲例外。
SOURCES = {
    "DutyComplete": ("誇獎", "內建：`IDutyState.DutyCompleted`（首次通關另有機率）"),
    "LevelUp": ("誇獎", "內建：`IClientState.LevelChanged`"),
    "Login": ("誇獎", "內建：`IClientState.Login`（預設每天第一次）"),
    "GilMilestone": ("誇獎", "內建：每 5 秒輪詢 Gil"),
    "Submarine": ("通知", "AutoRetainer：潛艇整隊回港／僱員探險全收"),
    "Retainer": ("通知", "AutoRetainer：僱員探險完成"),
    "ExpertDelivery": ("通知", "AutoRetainer：稀有品繳交循環全部角色跑完"),
    "Market": ("通知", "Marketbuddy：市場重掛整輪跑完"),
    "Crafting": ("通知", "Artisan：整份清單製作完成"),
    "Cosmic": ("通知", "ICE：宇宙探索任務金評"),
    "LowHp": ("警示", "內建：戰鬥中血量跌破門檻"),
    "MarkedByMany": ("警示", "內建：PvP，多個敵對玩家同時鎖定我"),
    "EnemyBehind": ("警示", "內建：PvP，敵對玩家從背後接近"),
    "DutyStart": ("通知", "NotificationMaster：任務／戰鬥開始"),
    "ReadyCheck": ("通知", "NotificationMaster：準備確認"),
    "CutsceneEnd": ("通知", "NotificationMaster：過場結束"),
    "DutyPop": ("通知", "內建：`IClientState.CfPop`（外部呼叫端也用同一個鍵）"),
    "FlagArrived": ("通知", "外部呼叫：走到旗標"),
    "Tell": ("通知", "外部呼叫：收到私訊"),
    "Arrived": ("通知", "外部呼叫：抵達目的地"),
    "Jackpot": ("通知", "外部呼叫：中獎"),
    "NeedHelp": ("通知", "外部呼叫：自動化卡住需要人來看"),
    "PlayerAlert": ("通知", "外部呼叫：附近出現要注意的玩家"),
    "BeingWatched": ("通知", "PeepingTom：有人把我設成目標"),
    "TellReceived": ("通知", "內建：`IChatGui.ChatMessage` 的 `TellIncoming` 類型"),
    "PartyInvite": ("通知", "內建：組隊邀請彈窗 addon 出現（`IAddonLifecycle`）"),
    "TradeRequest": ("通知", "內建：交易視窗 addon 出現（`IAddonLifecycle`）"),
    "DutyRunStopped": ("通知", "AutoDuty：自動跑本停下來"),
    "GatherStopped": ("通知", "GatherbuddyReborn：自動採集停下來"),
    "RareFish": ("通知", "AutoHook：釣到稀有魚"),
    "HuntFound": ("通知", "HuntHelper：A／B／S 級魔物出現"),
    "BagAlmostFull": ("通知", "InventoryTools：背包快滿"),
    "DailyReset": ("通知", "DailyDuty：每日重置"),
}


def parse():
    text = open(CS, "rb").read().decode("utf-8")

    consts = dict(re.findall(r'public const string (\w+) = "([^"]+)";', text))

    order_block = re.search(r"public static readonly string\[\] All =\s*\[(.*?)\];", text, re.S)
    if order_block is None:
        raise SystemExit("找不到 PraiseCategory.All——原始碼結構變了，請先修這個腳本。")
    order = re.findall(r"^\s*(\w+),", order_block.group(1), re.M)

    def table(name, default):
        block = re.search(
            r"public static readonly Dictionary<string, int> " + name + r" = new\(\)\s*\{(.*?)\};",
            text, re.S)
        found = {}
        if block is not None:
            for key, value in re.findall(r"\[(\w+)\]\s*=\s*(\d+),", block.group(1)):
                found[key] = int(value)
        return found, default

    max_lengths, _ = table("MaxLengths", 0)
    min_lengths, _ = table("MinLengths", 0)
    cooldowns, _ = table("Cooldowns", 0)
    return consts, order, max_lengths, min_lengths, cooldowns


def build_table():
    consts, order, max_lengths, min_lengths, cooldowns = parse()

    missing = [c for c in order if c not in SOURCES]
    if missing:
        raise SystemExit(
            "SOURCES 少了這些情境：" + "、".join(missing)
            + "。補上再跑，不然表格會少列而且沒有人會發現。")

    lines = [
        BEGIN,
        "",
        "> **權威來源＝`TataruPraise/Core/PraiseCategory.cs`。**這張表由 `tools/gen_category_table.py`",
        "> 從原始碼逐字產生，**隨版本更新**；抄鍵名請以原始碼為準。",
        "",
        "| 鍵名（＝`pool.json` 的鍵、也是 IPC 參數） | 類型 | 冷卻 | 句長 | 觸發來源 |",
        "|---|---|---|---|---|",
    ]

    for const in order:
        key = consts[const]
        kind, source = SOURCES[const]
        cd = cooldowns.get(const)
        cd_text = f"{cd} 秒" if cd else "全域"
        lo = min_lengths.get(const)
        hi = max_lengths.get(const)
        lo_text = str(lo) if lo else "全域(6)"
        hi_text = str(hi) if hi else "全域(12)"
        lines.append(f"| `{key}` | {kind} | {cd_text} | {lo_text}～{hi_text} 字 | {source} |")

    lines += [
        "",
        f"共 {len(order)} 個內建情境。「全域」＝跟著設定視窗裡的全域冷卻／句長上限走；",
        "冷卻在「短句」分頁的表格列上直接改（旁邊的 × ＝清掉自訂）；句長上下限與情境描述在同一頁的「進階」裡改。",
        "",
        END,
    ]
    return "\n".join(lines)


def main():
    table = build_table()
    if "--write" not in sys.argv:
        print(table)
        return

    text = open(README, "rb").read().decode("utf-8")
    if BEGIN not in text or END not in text:
        raise SystemExit("README 裡找不到情境表的標記，請先手動放 " + BEGIN + " / " + END + "。")

    start = text.index(BEGIN)
    end = text.index(END) + len(END)
    out = (text[:start] + table + text[end:]).encode("utf-8")
    assert out.count(bytes([0])) == 0
    tmp = README + ".tmp"
    open(tmp, "wb").write(out)
    os.replace(tmp, README)
    print("README 情境表已更新。")


if __name__ == "__main__":
    main()
