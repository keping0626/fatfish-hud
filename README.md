# 大肥鱼的hud · SPT 局内拾取提示

> 作者：**deepseek + keping** ｜ 适用 **SPT 4.1.6**（服务端 net10）
> 客户端插件 **v0.1.13** ｜ 服务端 mod **v0.2.3**

在 SPT 里捡起一件东西时，屏幕上立刻告诉你：**哪个藏身处设施、哪个任务需要它，还差多少个**。

```
【大肥鱼提醒您：固体燃料 ×2
藏身处·休息室 级1→2｜需3｜仓0+局2｜还差1｜战局内】
```

外加两块可选的常驻清单：**本局拾取**（出局即清）与**目标清单**（跨局购物清单，自己挑任务/设施盯）。

## 仓库内容

| 目录 | 组件 | 类型 | 装到哪 |
|---|---|---|---|
| `client-hud/` | **大肥鱼的hud** | BepInEx 客户端插件 | `BepInEx\plugins\大肥鱼的hud\大肥鱼的hud.dll` |
| `server-pickup-tracker/` | **大肥鱼的拾取记录**（`PickupTrackerServer`） | SPT 服务端 mod | `user\mods\PickupTrackerServer\` |

两个组件**互相独立**：只装客户端就能用提示（数据来自需求快照）；
服务端 mod 负责把「每局实际捡到了什么」记成 JSONL，供需求核对工具统计，**不装也不影响提示**。

各自的完整文档（配置项、挂钩点、图片自定义、排查、版本变更）：

- 客户端 → [`client-hud/README.md`](client-hud/README.md)
- 服务端 → [`server-pickup-tracker/README.md`](server-pickup-tracker/README.md)

## 安装

### 1. 客户端插件

1. 需要 **BepInEx 5（x64）**；想在游戏里按 F12 调设置就再装 **BepInEx.ConfigurationManager**。
2. 把 `client-hud/大肥鱼的hud.dll` 放进：

   ```
   <游戏根目录>\BepInEx\plugins\大肥鱼的hud\大肥鱼的hud.dll
   ```

3. 启动游戏。日志出现 `大肥鱼的hud v0.1.13 已加载` 即成功；F12 里搜「大肥鱼」，F9 打开目标清单管理面板，F10 弹一条自测提示。

> 插件目录名与 dll 文件名都带中文，BepInEx 能正常加载（已实机验证）。
> 程序集**内部名**是 ASCII 的 `RaidPickupHint`——BepInEx 只按文件加载，两者可以不同。
> GUID `deepseek.keping.raidpickuphint` 决定配置文件名 `BepInEx\config\deepseek.keping.raidpickuphint.cfg`。

### 2. 服务端 mod

把 `server-pickup-tracker/PickupTrackerServer.dll` 与 `config.jsonc` 放进：

```
<SPT_Runtime>\user\mods\PickupTrackerServer\
```

重启服务端，启动列表里出现「**大肥鱼的拾取记录** 版本 0.2.3」即加载成功。
每局正常撤离/结算后，该目录下会生成 `data\pickups.json`（逐行 JSON）。

> `config.jsonc` 必须是**纯 JSON**（SPT 用 `System.Text.Json`，不接受 `//` 注释）。

### 3. 需求快照（客户端的数据来源）

客户端进局后读的是与 dll **同目录**的 `needs_snapshot.json`：一张「物品 → 哪些任务/设施需要它、需要多少、仓库里现有多少」的表。
它由配套的核对工具从**你自己的存档 + 任务库 + 藏身处库**生成，因此**必须自己生成一份**（别人的存档数据对你没用）。

生成方式见下方「配套工具」。

## 依赖

| 组件 | 依赖 |
|---|---|
| 客户端插件 | BepInEx 5.x（x64）；`Assembly-CSharp` / `Comfort` / `Newtonsoft.Json` 等由游戏自带 |
| 服务端 mod | SPT 4.1.6；`SPTarkov.*`、`0Harmony.dll`、`SemanticVersioning.dll` 由 `SPT_Runtime\` 自带 |

本项目不引入任何第三方 mod 依赖，也不新增后台扫描类 mod（塔科夫主线程是单核瓶颈，本项目刻意保持低开销）。

## 配套工具

客户端的提示依赖需求快照，快照由 [`tools/quest_hideout_checker.py`](tools/README.md)（Python 3，**只读**存档）生成：
读你的存档与数据库，产出「物品 → 哪些任务/设施需要它、需要多少、仓库里现有多少」的 `needs_snapshot.json`。

> 快照描述的是**你自己存档的状态**，所以必须自己生成一份（别人的存档数据对你没用）；
> 生成后插件 30 秒内自动读取，不必重启游戏。详见 [`tools/README.md`](tools/README.md)。

## 重新编译

源码在各自的 `src/` 下，**离线编译**，不需要 NuGet（本机没有 NuGet 缓存也能编）。

- 客户端：`netstandard2.0` + `DisableImplicitFrameworkReferences=true`，全部 `<Reference>` 显式指向 `EscapeFromTarkov_Data\Managed\` 与 `BepInEx\core\`；构建产物名 `RaidPickupHint.dll`，部署时改名为 `大肥鱼的hud.dll`。
- 服务端：SDK 9 编译 `net9.0`，由 SPT 的 net10 运行时承载。

两边都**不需要改文件里的路径**：游戏根目录由属性 `GameDir`（客户端）/ `SptDir`（服务端）决定，
默认值是作者本机路径；换机器时命令行覆盖即可：

```powershell
# 客户端（产物：_scratch\out\Release\netstandard2.0\RaidPickupHint.dll → 改名「大肥鱼的hud.dll」）
dotnet build client-hud\src\RaidPickupHint.csproj -c Release -p:GameDir=X:\你的SPT

# 服务端（需要 .NET 10 运行时程序集，见下一行说明）
dotnet build server-pickup-tracker\src\PickupTrackerServer.csproj -c Release -p:SptDir=X:\你的SPT\SPT_Runtime
```

中间产物默认写到仓库内的 `_scratch\`（已 gitignore），**不会落进 `BepInEx\plugins` 或 mod 目录**。

> 服务端 mod 引用的是本机 dotnet 的 `shared\Microsoft.NETCore.App\10.0.9\` 程序集（SPT 4.1.6 是 net10），
> 路径由属性 `NetCoreAppDir` 推导（默认取 `$(NetCoreRoot)`），装了别的补丁版本就用 `-p:NetCoreAppDir=...` 指定。

## 已知限制

- **ALT+F4 强退**不会触发 `/client/match/local/end`，那一局的拾取不会被服务端 mod 记录——必须正常撤离或死亡结算。
- 提示里的「仓库持有量」取自需求快照生成的那一刻；局内新捡到的会实时累加，但**别处新入账**（如跳蚤市场买入后放进仓库）要重跑一次核对工具才会刷新。
- 局内把同款物品**并入已有堆叠**（不产生新实例 ID）不记录；拆分堆叠会产生新实例 ID，可能被记成一件"新到手"。

## 许可

见 [`LICENSE`](LICENSE)。
