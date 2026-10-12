# 需求核对工具 `quest_hideout_checker.py`

离线、**只读**的小工具：读你的 SPT 存档与数据库，产出两样东西 ——

1. 给人看的核对报告 `output\需求核对_<日期>_<时间>.md`：任务与藏身处各缺哪些物品；
2. 给 **大肥鱼的hud** 客户端插件用的需求快照 `needs_snapshot.json`：局内捡到东西时提示的
   「谁需要它、还差几个」，就来自这张表。

**全程只读**：不改存档、不碰任何 mod 数据；唯一的写操作就是生成上面这两个文件。

## 用法

```bat
python quest_hideout_checker.py            :: 自动定位 SPT_Runtime + 自动选最近使用的存档
python quest_hideout_checker.py --pick     :: 交互式挑角色档
```

Windows 上也可以直接双击 `启动_需求核对.bat`（会带你交互选档）。

| 参数 | 说明 |
|---|---|
| `--base <路径>` | SPT_Runtime 目录。定位顺序：`--base` ＞ 环境变量 `SPT_BASE` ＞ 从脚本所在位置逐级向上自动探测（本级目录与它下面的 `SPT_Runtime` 都会查） |
| `--profile <路径>` | 指定存档 JSON；不指定则取 `user\profiles` 下**最后修改**的那个档 |
| `--pick` | 交互式列出所有角色档供选择 |
| `--out <目录>` | 报告输出目录（默认脚本旁的 `output\`） |
| `--pickups <路径>` | 局内拾取记录 JSONL（默认 `user\mods\PickupTrackerServer\data\pickups.json`） |
| `--export-needs <路径>` | 需求快照输出路径（默认 `<SPT_Runtime 的同级>\BepInEx\plugins\大肥鱼的hud\needs_snapshot.json`） |
| `--no-export` | 只出报告，不生成快照 |

## 为什么快照必须自己生成

快照描述的是**你自己存档的状态**：哪些任务正在进行、藏身处建到几级、仓库里各有多少。
别人的快照对你没有意义，所以作者也无法预置一份放进仓库。

生成后**不必重启游戏**：插件每 30 秒重新读一次快照文件。

## 联动的另一块：局内拾取记录

装上服务端 mod **大肥鱼的拾取记录**（见 `server-pickup-tracker\`）后，每局正常撤离会在
`user\mods\PickupTrackerServer\data\pickups.json` 追加本局新到手的物品。

本工具读这个文件，报告里就多一列「局内已捡」，缺口会随实际捡到的东西实时收敛。
没有这个文件也能照常跑，只是没有那一列。

## 环境要求

- Python 3.8+（只用标准库，无需 pip 安装任何东西）
- `output\` 目录需要可写

## 说明与限制

- 物品名取 locale 的 `ch.json`（缺失时回退 `en.json`）；个别无翻译物品会显示游戏内原名。
- 藏身处设施名以 locale 的 `hideout_area_<N>_name` 为准，不依赖网上流传的旧对照表。
- 任务只统计 status=2（进行中）与 status=3（待交）。
- 每次运行都重新读一遍存档 ⇒ 接了新任务 / 藏身处升级 / 打完一局后，重跑即可。
- 局内拾取记录依赖「正常撤离或死亡结算」才会产生；**ALT+F4 强退那一局不会入账**。
