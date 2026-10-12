# PickupTrackerServer（服务端 mod）

> 作者：**deepseek + keping** ｜ 当前 **v0.2.3**（2026-10-12：SPT 启动列表显示名改为中文 **大肥鱼的拾取记录**；`ModGuid` 与 mod 目录名仍为 `PickupTrackerServer`，**逻辑零变化**。v0.2.2：署名由 `dsh` 改为 `deepseek + keping`）
>
> 元数据：`ModGuid = PickupTrackerServer`、`Name = 大肥鱼的拾取记录`。`ModGuid` 与 mod 目录名是**对外契约**（服务端按目录名加载、其他工具按 GUID 识别），不要改。

记录**每局结束时"局内新到手"的物品**，以 JSONL 追加写入本 mod 目录下的 `data\pickups.json`（只写自己的文件，不改存档、不干扰游戏逻辑）。

需求核对工具 v2 读取该文件，在任务/藏身处需求表中显示"局内已捡 p / 需要 need"。

## 原理（v0.2.1：2026-10-10 实测确定主干，2026-10-12 补充内部槽过滤）

**SPT 局内拾取不经过服务端。** 逐条核对 `user\logs\spt\spt*.log` 可证：整局 raid 期间服务端收到 **0 次** `/client/game/profile/items/moving`（日志里偶尔出现的该请求都在进局之前，属藏身处整理）。客户端在 raid 中本地记账，只有结束对局时通过 `/client/match/local/end` 把**完整 profile** 回传。

因此本 mod：

1. Harmony **Prefix** 钩 `SPTarkov.Server.Core.Controllers.MatchController.EndLocalRaidAsync`
   —— 时机在服务端应用客户端回传数据**之前**，此时磁盘/内存里的 profile 还是 raid 前状态
2. 新到手物品 = 客户端回传 profile 的物品 − 磁盘 `user\profiles\{存档ID}.json` 的物品
   （`characters.pmc` / `characters.scav` 里取与客户端交集更大的那个作基准，故 PMC/Scav 局都适用）
3. 只记录其中**归属玩家装备树**（`Inventory.Equipment` 可达）的物品 → 仓库/藏身处整理自动排除
4. 排除**武器 / 弹药内部槽**的新 itemId：`slotId` 以 `mod_`（改枪配件）或 `patron_`（`patron_in_weapon` 枪膛、`patron_in_mag` 弹匣）开头、或等于 `cartridges`
   —— 局内上膛 / 装弹 / 装配都会让客户端生成新的实例 id，那不属于拾取（实例：SA58 上膛曾多记 1 发 M993）
5. 按 `sessionId:itemId` 去重后追加写入 `data\pickups.json`

**契约：全程只读。** 不修改 profile、不修改请求数据、不写存档。

> v0.1.0 曾假设局内移动会走服务端 `InventoryCallbacks.MoveItem` / `InventoryController.MoveItem`，实测该假设不成立（局内这些方法根本不会被调用），已废弃。

## 目录结构

```
user\mods\PickupTrackerServer\
├── PickupTrackerServer.dll   ← 服务端加载的主程序集（目录名\同名.dll）
├── config.jsonc              ← 配置（当前版本预留，默认值生效）
├── data\pickups.json         ← 运行后自动生成：逐行 JSON（JSONL）
├── data\_raid_diag.jsonl     ← 诊断日志（排查用，可随时删除）
└── （源码 *.cs / *.csproj    ← 便于修改重编译，不影响加载）
```

## 数据格式（pickups.json，每行一条）

```json
{"sessionId":"<存档ID>","tpl":"<物品模板ID>","itemId":"<物品实例ID>","count":1,"timeUtc":"2026-10-10T...Z","map":null,"source":"raidEnd"}
```

- `sessionId`：存档顶层 `_id`（核对工具按它过滤当前角色档）
- `count`：该物品的堆叠数量
- `source`：`raidEnd`（本版只有这一种来源）

## 怎么用

1. 以**管理员身份**启动 `SPT_Runtime\SPT.Server.exe`
2. 进局随便捡东西
3. **正常撤离 / 正常结束对局**（见下方限制）
4. 退出后跑仓库自带的核对工具 `tools\启动_需求核对.bat`（或 `python tools\quest_hideout_checker.py`），看『局内已捡』列

## 环境要求

- SPT 4.1.6（net10.0 服务端；本 mod 以 net9.0 编译，由 net10 运行时承载）
- 依赖程序集（`SPTarkov.*`、`0Harmony.dll`、`SemanticVersioning.dll`）均在 `SPT_Runtime\` 根目录，无需拷贝

## 如何重新编译（可选）

需要 .NET SDK 9+。中间产物由 `src\Directory.Build.props` 重定向到仓库内的 `_scratch\`（已在 .gitignore 中排除），
**不会落进 mod 目录** —— mod 目录会被 SPT 全量扫描，`obj\` / `NuGet\` 留在里面会出问题。

```bat
:: SPT_Runtime 默认 D:\tkf\SPT_Runtime（作者本机值）；换机器用 -p:SptDir=... 覆盖
dotnet build src\PickupTrackerServer.csproj -c Release -p:SptDir="X:\SPT\SPT_Runtime"
copy /Y "..\_scratch\server\out\Release\net9.0\PickupTrackerServer.dll" .
```

离线验证工具（开发期自用，未随本仓库发布）：把 mod 的补丁挂到真实服务端程序集上，
再用一份真实存档合成"客户端回传 profile"跑完整流程自检，跑完自动还原 `pickups.json`。

## 已知限制

- **ALT+F4 强退不触发 `/client/match/local/end`**，客户端来不及回传 profile → 那一局的拾取无法记录（必须正常撤离/死亡结算）
- 局内把同款物品**并入已有堆叠**（不产生新 itemId）不记录；局内拆分堆叠会产生新 itemId，可能记为一件"新到手"
- `map` 恒为 null（回传数据里没有地图名；后续可另行补全）
- 依赖"服务端在 raid 期间不改动 `characters.*.Inventory`"这一前提（实测成立）
