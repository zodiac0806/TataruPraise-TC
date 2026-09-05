# TataruPraise IPC 契約

給**其他插件作者**看的。使用者不需要讀這一份。


其他插件可以直接呼叫。契約名逐字定義在 `TataruPraise/IpcContract.cs`。

> 🔴 **這四個名字不會改。** Dalamud 的 CallGate 是純字串比對，改名不會有錯誤訊息，
> 呼叫端只會拿到「沒有人註冊」——靜默斷線。要換語意會開新名字，舊的留著。

| 契約名 | 簽章 | 行為 |
|---|---|---|
| `TataruPraise.Speak` | `Func<string, bool>` | 念這一句。先查語音快取，沒有就背景送 9882 即時合成（逾時 10 秒）並順便存進快取。**不吃冷卻、不吃機率**，但吃總開關與「同時只播一句」。**不進待播槽**：喇叭忙就直接回 `false`。回傳是「有沒有排進去」，不代表真的出得了聲。 |
| `TataruPraise.Praise` | `Func<string, bool>` | 從指定情境的句子裡挑一句已合成的來播（喇叭忙就依優先權進待播槽）。**不看事件開關、不看機率，但吃冷卻**與總開關。情境字串＝`pool.json` 的鍵（見下）。**未知情境回 `false`**，並在記錄檔印一次 `Information`（同一個情境只印一次，不會洗 log）。 |
| `TataruPraise.IsAvailable` | `Func<bool>` | 總開關開著**而且**池裡真的有已合成語音的句子。 |
| `TataruPraise.IsAvailableFor` | `Func<string, bool>` | **指定情境**現在有沒有辦法出聲：總開關開著＋該情境沒被使用者關掉＋該情境至少有一句已合成語音（不看冷卻）。用來補 `IsAvailable` 只看「整池有沒有任何情境能播」的洞。 |

呼叫範例：

```csharp
var speak = pluginInterface.GetIpcSubscriber<string, bool>("TataruPraise.Speak");
try
{
    speak.InvokeFunc("前輩，這一手漂亮！");
}
catch (Exception)
{
    // 對方沒安裝／沒載入時 InvokeFunc 會擲 IpcNotReadyError，呼叫端自己要接。
}
```

回 `false` 有幾種情形，都不是錯誤：總開關關著、還在冷卻、上一句還在播、
這個情境沒有任何**已合成語音**的句子、或者**情境名不存在**（最後這種會在記錄檔印一次
`未知情境「X」`，同一個情境只印一次）。

情境鍵名見 [README 的「情境」表](../README.md#情境)；給呼叫端的另一份範例與補充說明在 [DESIGN.md](DESIGN.md#ipc-契約補充情境鍵完整索引與範例)。
