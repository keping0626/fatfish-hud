# -*- coding: utf-8 -*-
r"""
SPT 离线版 · 任务 & 藏身处物品需求核对工具 v2（局内捡起实时监测联动）
========================================================================
功能:
  1. 读取当前主存档里"已接任务"(进行中/待交) 的物品需求 (FindItem/HandoverItem)
  2. 读取"藏身处"各区域下一级的物品需求 (含卢布/欧元)
  3. 统计仓库(整棵物品树)对应物品的持有数量
  4. 【v2 新增】读取 PickupTrackerServer mod 的局内拾取记录 (user\mods\PickupTrackerServer\data\pickups.json)
     在需求表中显示"局内已捡 p / 需要 need"，缺口实时收敛，并给出"还需捡 N 个"提示
  5. 生成"需求 vs 持有 vs 局内捡起"清单: 已满足 ✅ / 局内捡够 ✅ / 缺口 ❌(缺 N)
  6. 【v3 新增】导出需求快照 JSON（默认同时生成），供 BepInEx 客户端插件
     RaidPickupHint 在局内拾取物品时提示"这个物品被哪个任务/藏身处需要、还差几个"

特点: 每次运行重新读取存档、数据库与拾取记录, 接新任务/藏身处升级/打完一局后重跑即可得到最新清单。

用法:
  python quest_hideout_checker.py [--base <SPT_Runtime 路径>] [--pick] [--profile <存档路径>]
                                  [--out <目录>] [--pickups <jsonl路径>]
                                  [--export-needs <快照路径>] [--no-export]

SPT_Runtime 的定位顺序: --base 参数 > 环境变量 SPT_BASE > 从本脚本位置逐级向上自动探测。
未指定存档时默认取 user\profiles 下"最后修改"的那个档（加 --pick 可交互选择）。
"""
import json
import os
import sys
import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding="utf-8")

def _find_base():
    """定位 SPT_Runtime 目录（发布版不硬编码任何本机路径）。

    优先级: --base 参数 > 环境变量 SPT_BASE > 从脚本所在目录逐级向上探测
    （每级都同时检查该目录自身与它下面的 SPT_Runtime）。
    """
    argv = sys.argv[1:]
    if "--base" in argv:
        return os.path.abspath(argv[argv.index("--base") + 1])
    env = os.environ.get("SPT_BASE")
    if env:
        return os.path.abspath(env)
    here = os.path.dirname(os.path.abspath(__file__))
    cands, cur = [], here
    for _ in range(6):
        cands.append(cur)
        cands.append(os.path.join(cur, "SPT_Runtime"))
        parent = os.path.dirname(cur)
        if parent == cur:
            break
        cur = parent
    for c in cands:
        if os.path.isfile(os.path.join(c, "SPT_Data", "database", "templates", "quests.json")):
            return c
    return here          # 兜底：稍后读文件时会明确报出缺的是哪个路径


BASE = _find_base()
# 不硬编码存档 ID：未指定 --profile / --pick 时，自动取 profiles 目录下最后修改的那个档
DEFAULT_PROFILE = None
QDB_PATH = os.path.join(BASE, "SPT_Data", "database", "templates", "quests.json")
AREAS_PATH = os.path.join(BASE, "SPT_Data", "database", "hideout", "areas.json")
CAREAS_PATH = os.path.join(BASE, "SPT_Data", "database", "hideout", "customAreas.json")
CH_LOC = os.path.join(BASE, "SPT_Data", "database", "locales", "global", "ch.json")
EN_LOC = os.path.join(BASE, "SPT_Data", "database", "locales", "global", "en.json")
# v2: PickupTrackerServer mod 实时写入的局内拾取记录 (JSONL)
PICKUPS_DEFAULT = os.path.join(BASE, "user", "mods", "PickupTrackerServer", "data", "pickups.json")

# v3: 局内拾取提示插件（BepInEx 客户端插件）读取的需求快照
EXPORT_DEFAULT = os.path.join(os.path.dirname(BASE), "BepInEx", "plugins", "大肥鱼的hud",
                              "needs_snapshot.json")

RUBLE_TPL = "5449016a4bdc2d6f028b456f"   # 卢布
EURO_TPL = "569668774bdc2da2298b4568"   # 欧元
QUEST_STATUS = {1: "可接", 2: "进行中", 3: "待交", 4: "已完成", 5: "失败"}

# 藏身处区域 type -> 中文名。
#
# 权威来源＝游戏 locale 的 "hideout_area_<N>_name" 键（ch.json 里如 "hideout_area_12_name": "靶场"）。
# main() 加载 locale 后会调用 build_area_names() 填充 AREA_NAMES；下面的 FALLBACK 仅在 locale 缺该 type 时兜底。
#
# 2026-10-12 修正：原内置表**整体错位**（12 写成「集水器」而真名是「靶场」、16 写成「射击场」
# 而真名是「荣耀展柜」、0 写成「发电机」而真名是「通风」……27 项全错），导致玩家已建好靶场一级
# （type=12, level=1），却因脚本把 type=16（荣耀展柜, level=0）误认成射击场而收到
# 「射击场 级0→1」的假提示。现改为以游戏 locale 为准。
AREA_NAMES = {}

AREA_NAMES_FALLBACK = {
    0: "通风", 1: "安保", 2: "卫生间", 3: "仓库", 4: "发电机", 5: "供暖",
    6: "集水器", 7: "医疗站", 8: "营养部", 9: "休息区", 10: "工作台", 11: "情报中心",
    12: "靶场", 13: "图书馆", 14: "Scav宝箱", 15: "照明", 16: "荣耀展柜",
    17: "空气过滤单元", 18: "太阳能", 19: "酿酒处", 20: "比特币矿场", 21: "圣诞树",
    22: "易碎墙", 23: "健身区", 24: "武器架", 25: "区域25", 26: "装备架", 27: "仪式圈",
}


def build_area_names(*locs):
    """从 locale 的 hideout_area_<N>_name 键构建 {type: 名字}；靠前的 locale 优先。"""
    names = {}
    prefix, suffix = "hideout_area_", "_name"
    for loc in locs:
        if not isinstance(loc, dict):
            continue
        for k, v in loc.items():
            if not v or not isinstance(k, str):
                continue
            if not (k.startswith(prefix) and k.endswith(suffix)):
                continue
            mid = k[len(prefix):-len(suffix)]
            if mid.isdigit():
                names.setdefault(int(mid), v)
    return names


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def sget(loc, *keys):
    for k in keys:
        if k in loc:
            return loc[k]
    return None


def item_name(tpl, loc_ch, loc_en):
    return (sget(loc_ch, tpl + " ShortName") or sget(loc_en, tpl + " ShortName")
            or sget(loc_ch, tpl + " Name") or sget(loc_en, tpl + " Name") or tpl)


def quest_name(qid, qdef, loc_ch, loc_en):
    n = sget(loc_ch, qid + " name") or sget(loc_en, qid + " name")
    if n:
        return n
    qn = qdef.get("QuestName") if isinstance(qdef, dict) else None
    return qn or qid


def area_name(atype):
    try:
        key = int(atype)
    except (TypeError, ValueError):
        key = atype
    return (AREA_NAMES.get(key) or AREA_NAMES_FALLBACK.get(key)
            or "区域 type=%s" % atype)


def collect_quest_rows(pmc, qdb, loc_ch, loc_en):
    """返回需求行 (source_key=任务qid; tpls 列表表示任选组)"""
    rows = []
    for q in pmc.get("Quests", []):
        status = q.get("status")
        if status not in (2, 3):
            continue
        qid = q.get("qid")
        qdef = qdb.get(qid) or {}
        conds = ((qdef.get("conditions") or {}).get("AvailableForFinish") or [])
        for c in conds:
            if not isinstance(c, dict) or c.get("conditionType") not in ("FindItem", "HandoverItem"):
                continue
            if c.get("parentId"):
                continue
            tpls = [t for t in (c.get("target") or []) if t]
            if not tpls:
                continue
            rows.append({
                "kind": "任务",
                "source": quest_name(qid, qdef, loc_ch, loc_en),
                "source_key": qid,
                "status_tag": QUEST_STATUS.get(status, str(status)),
                "cond": "递交" if c.get("conditionType") == "HandoverItem" else "持有",
                "tpls": tuple(tpls),
                "need": max(1, int(c.get("value", 1) or 1)),
                "found_in_raid": bool(c.get("onlyFoundInRaid") or c.get("countInRaid")),
            })
    return rows


def collect_hideout_rows(pmc, area_db, loc_ch, loc_en):
    rows = []
    for a in pmc.get("Hideout", {}).get("Areas", []):
        atype = a.get("type")
        cur = int(a.get("level", 0) or 0)
        db = area_db.get(atype)
        if not db:
            continue
        nxt = (db.get("stages") or {}).get(str(cur + 1))
        if not nxt:
            continue
        for r in (nxt.get("requirements") or []):
            if not isinstance(r, dict) or r.get("type") != "Item":
                continue
            tpl = r.get("templateId")
            rows.append({
                "kind": "藏身处",
                "source": area_name(atype),
                "source_key": atype,
                "status_tag": "级%s→%s" % (cur, cur + 1),
                "cond": "资金" if tpl in (RUBLE_TPL, EURO_TPL) else "投入",
                "tpls": (tpl,),
                "need": max(1, int(r.get("count", 1) or 1)),
                "found_in_raid": bool(r.get("isSpawnedInSession")),
            })
    return rows


def merge_rows(rows):
    """同来源且同物品组(任选组按相同 tpls)合并: 持有+递交 -> 一行"""
    merged = {}
    for r in rows:
        key = (r["kind"], r["source_key"], r["tpls"])
        if key not in merged:
            merged[key] = dict(r)
            continue
        m = merged[key]
        conds = {m["cond"], r["cond"]}
        if "持有" in conds and "递交" in conds:
            m["cond"] = "持有+递交"
        m["need"] = max(m["need"], r["need"])
        m["found_in_raid"] = m["found_in_raid"] or r["found_in_raid"]
    return list(merged.values())


def count_held(pmc):
    held = defaultdict(int)
    for it in pmc.get("Inventory", {}).get("items", []):
        tpl = it.get("_tpl")
        if not tpl:
            continue
        n = (it.get("upd") or {}).get("StackObjectsCount", 1)
        held[tpl] += int(n) if isinstance(n, (int, float)) else 1
    return held


def load_pickups(path, profile_id):
    """v2: 读取 PickupTrackerServer 的 JSONL 拾取记录。

    返回 (sums, record_count, exists):
      sums: {tpl: 局内捡起件数}（按数量字段累加；mod 已按 itemId 去重）
      record_count: 参与统计的记录条数（用于报告摘要）
      exists: 文件是否存在（用于提示"尚未有拾取数据"）
    仅统计与当前主档 sessionId(=存档顶层 _id) 一致的记录，避免混入其他角色档。
    """
    sums = defaultdict(int)
    record_count = 0
    if profile_id is None:
        profile_id = ""
    if not os.path.isfile(path):
        return sums, record_count, False
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip().lstrip("\ufeff")  # 容忍写入方可能带的 UTF-8 BOM
            if not line or not line.startswith("{"):
                continue
            try:
                rec = json.loads(line)
            except Exception:
                continue
            sid = rec.get("sessionId")
            if sid and profile_id and sid != profile_id:
                continue
            tpl = rec.get("tpl")
            if not tpl:
                continue
            try:
                c = int(rec.get("count") or 1)
            except Exception:
                c = 1
            sums[tpl] += max(1, c)
            record_count += 1
    return sums, record_count, True


def group_held(tpls, held):
    """任选组: 列表内所有物品持有数合计"""
    return sum(held.get(t, 0) for t in tpls)


def group_picked(tpls, pick_sums):
    """任选组: 列表内所有物品局内捡起数合计"""
    return sum(pick_sums.get(t, 0) for t in tpls)


def fmt_items(tpls, loc_ch, loc_en):
    if len(tpls) == 1:
        return item_name(tpls[0], loc_ch, loc_en)
    names = [item_name(t, loc_ch, loc_en) for t in tpls]
    shown = "、".join(names[:5])
    if len(names) > 5:
        shown += " 等%d种" % len(names)
    return "(任选) " + shown


def list_profiles():
    """列出 profiles 目录下的角色档（按文件名排序）。"""
    import glob
    prof_dir = os.path.join(BASE, "user", "profiles")
    return sorted(glob.glob(os.path.join(prof_dir, "*.json")))


def newest_profile():
    """默认档＝最后修改的那个（发布版不硬编码存档 ID）。"""
    files = list_profiles()
    if not files:
        print("[错误] 未找到任何角色档: %s" % os.path.join(BASE, "user", "profiles"))
        sys.exit(1)
    return max(files, key=os.path.getmtime)


def pick_profile():
    """交互式选择角色档: 列出 profiles 目录下所有档, 输序号选择"""
    files = list_profiles()
    if not files:
        print("[错误] 未找到任何角色档: %s" % os.path.join(BASE, "user", "profiles"))
        sys.exit(1)
    # 最近修改的档排最前, 回车默认选中它（避免误用旧档生成报告）
    default_path = max(files, key=os.path.getmtime)
    files.sort(key=lambda p: 0 if p == default_path else 1)
    print("可用角色档:")
    candidates = []
    for i, p in enumerate(files):
        label = os.path.basename(p)
        try:
            with open(p, encoding="utf-8") as f:
                d = json.load(f)
            pmc = d.get("characters", {}).get("pmc", {})
            info = pmc.get("Info", {})
            label = "%s (Lv.%s %s)  [%s]" % (
                info.get("Nickname"), info.get("Level"), info.get("Side"), os.path.basename(p))
        except Exception:
            pass
        mark = "  ← 最近使用" if p == default_path else ""
        candidates.append(p)
        print("  [%d] %s%s" % (i + 1, label, mark))
    while True:
        try:
            choice = input("请选择序号 (直接回车 = 1): ").strip()
        except EOFError:
            choice = ""
        if choice == "":
            return candidates[0]
        if choice.isdigit() and 1 <= int(choice) <= len(candidates):
            return candidates[int(choice) - 1]
        print("无效输入, 请输入 1-%d 之间的数字" % len(candidates))


def build_needs_snapshot(rows, held, pick_sums, loc_ch, loc_en, info, profile_id):
    """v3: 构建"局内拾取提示"用的需求快照。

    面向客户端插件：以物品 tpl 为键，列出该物品被哪些任务/藏身处需求，
    以及各自的 need / 组内持有量，供插件在局内拾取瞬间算"还差几个"。

    约定:
      - 卢布/欧元（藏身处资金需求）不导出——捡到钱不需要提示
      - 任选组（一个 FindItem 条件列多个 tpl）会给组内每个 tpl 都生成来源，
        并带上 alts（组内其他物品），持有量用组内合计（与核对工具口径一致）
    """
    items = {}

    def esc_name(tpl):
        return item_name(tpl, loc_ch, loc_en)

    for r in rows:
        if any(t in (RUBLE_TPL, EURO_TPL) for t in r["tpls"]):
            continue
        h_group = group_held(r["tpls"], held)
        p_group = group_picked(r["tpls"], pick_sums)
        for t in r["tpls"]:
            if not t:
                continue
            ent = items.get(t)
            if ent is None:
                ent = {
                    "name": esc_name(t),
                    "full": (sget(loc_ch, t + " Name") or sget(loc_en, t + " Name")
                             or esc_name(t)),
                    "held": int(held.get(t, 0)),
                    "sources": [],
                }
                items[t] = ent
            ent["sources"].append({
                "kind": "quest" if r["kind"] == "任务" else "hideout",
                "kindText": r["kind"],
                "label": r["source"],
                "stage": r["status_tag"],
                "cond": r["cond"],
                "need": int(r["need"]),
                "heldGroup": int(h_group),
                "picked": int(p_group),
                "fir": bool(r["found_in_raid"]),
                "alts": [{"tpl": a, "name": esc_name(a)} for a in r["tpls"] if a != t],
            })

    # 每个物品内来源排序：藏身处优先，其次按缺口大的、需求数大的在前
    for ent in items.values():
        ent["sources"].sort(key=lambda s: (
            0 if s["kind"] == "hideout" else 1,
            -(max(0, s["need"] - s["heldGroup"])),
            -s["need"],
            s["label"],
        ))

    snap = {
        "schema": 1,
        "generatedAt": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
        "generator": "quest_hideout_checker v3",
        "profile": {
            "id": profile_id,
            "nickname": info.get("Nickname"),
            "level": info.get("Level"),
            "side": info.get("Side"),
        },
        "items": items,
        "stats": {
            "itemCount": len(items),
            "sourceCount": sum(len(e["sources"]) for e in items.values()),
        },
    }
    return snap


def export_snapshot(snap, path):
    """写出需求快照（UTF-8 无 BOM，避免消费方（含 Python/其它解析器）首字符踩坑）"""
    d = os.path.dirname(os.path.abspath(path))
    if d:
        os.makedirs(d, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(snap, f, ensure_ascii=False, indent=1)
        f.write("\n")


def main():
    args = sys.argv[1:]
    profile_path = None
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "output")
    pickups_path = PICKUPS_DEFAULT
    if "--pick" in args:
        profile_path = pick_profile()
    elif "--profile" in args:
        profile_path = args[args.index("--profile") + 1]
    if profile_path is None:
        profile_path = newest_profile()          # 默认＝最后修改的那个档
    if "--out" in args:
        out_dir = args[args.index("--out") + 1]
    if "--pickups" in args:
        pickups_path = args[args.index("--pickups") + 1]
    # v3: 默认随报告一起导出"局内拾取提示"用的需求快照（--no-export 可关闭）
    export_path = EXPORT_DEFAULT
    if "--export-needs" in args:
        export_path = args[args.index("--export-needs") + 1]
    if "--no-export" in args:
        export_path = None
    if not os.path.isfile(profile_path):
        print("[错误] 找不到存档:", profile_path)
        sys.exit(1)
    os.makedirs(out_dir, exist_ok=True)

    print("加载数据 ...")
    prof = load_json(profile_path)
    pmc = prof["characters"]["pmc"]
    # profileId 在 pmc._id（存档顶层没有 _id），与 PickupTrackerServer 写入的 sessionId 同源；
    # 兜底用文件名，保证 pickups 按档过滤不会静默失效。
    profile_id = (pmc.get("_id") or prof.get("_id")
                  or os.path.splitext(os.path.basename(profile_path))[0])
    qdb = load_json(QDB_PATH)
    area_db = {a["type"]: a for a in load_json(AREAS_PATH) + load_json(CAREAS_PATH)}
    loc_ch, loc_en = load_json(CH_LOC), load_json(EN_LOC)
    # 设施名以游戏 locale 为准（hideout_area_<N>_name），不再依赖易错的内置表
    AREA_NAMES.update(build_area_names(loc_ch, loc_en))
    pick_sums, pick_records, pick_exists = load_pickups(pickups_path, profile_id)

    rows = merge_rows(collect_quest_rows(pmc, qdb, loc_ch, loc_en)
                      + collect_hideout_rows(pmc, area_db, loc_ch, loc_en))
    held = count_held(pmc)
    info = pmc.get("Info", {})
    now = datetime.datetime.now()

    def status(r):
        """库存已满足 -> 1；否则 0（局内捡起仅做进度提示，不算库存满足）"""
        return 1 if group_held(r["tpls"], held) >= r["need"] else 0

    # 排序: 藏身处按区域 type; 任务按缺口在前（缺口考虑库存与局内捡起）
    def deficit(r):
        return max(0, r["need"] - max(group_held(r["tpls"], held), group_picked(r["tpls"], pick_sums)))

    quest_rows = sorted((r for r in rows if r["kind"] == "任务"),
                        key=lambda r: (status(r), deficit(r), r["source"]))
    hide_rows = sorted((r for r in rows if r["kind"] == "藏身处"),
                       key=lambda r: (r["source_key"], status(r)))

    L = []
    ap = L.append
    ap("# SPT 任务 & 藏身处物品需求核对报告")
    ap("")
    ap("- 角色: %s (Lv.%s, %s)" % (info.get("Nickname"), info.get("Level"), info.get("Side")))
    ap("- 生成时间: %s" % now.strftime("%Y-%m-%d %H:%M:%S"))
    if pick_exists:
        ap("- 局内拾取数据源: %s（记录 %d 条，按当前主档会话过滤）" % (pickups_path, pick_records))
    else:
        ap("- 局内拾取数据源: 未找到 %s（先按 mod 说明进一局游戏测试捡物品）" % pickups_path)
    ap("- ✅=库存已满足 | ✅* =库存未满足但局内已捡够 | ❌=缺口(库存差 / 还需捡) | ★=需战局内拾取 | (任选)=清单内物品凑够数量即可")
    ap("")

    for title, group in (("任务", quest_rows), ("藏身处", hide_rows)):
        if not group:
            continue
        ok = sum(1 for r in group if status(r) == 1)
        ok_pick = sum(1 for r in group if status(r) == 0 and group_picked(r["tpls"], pick_sums) >= r["need"])
        note = "（另有 %d 项局内已捡够，尚未入库）" % ok_pick if ok_pick else ""
        ap("## %s物品需求 (%d 项, 已满足 %d) %s" % (title, len(group), ok, note))
        ap("")
        ap("| 来源 | 状态 | 类型 | 物品 | 需要 | 持有 | 局内已捡 | 结果 |")
        ap("|---|---|---|---|---:|---:|---:|---|")
        for r in group:
            h = group_held(r["tpls"], held)
            p = group_picked(r["tpls"], pick_sums)
            need = r["need"]
            star = " ★" if r["found_in_raid"] else ""
            if h >= need:
                res = "✅ 已满足"
            elif p >= need:
                res = "✅* 局内已捡够（存入仓库即可）"
            else:
                parts = []
                if h < need:
                    parts.append("库存差 %d" % (need - h))
                if p < need:
                    parts.append("还需捡 %d" % (need - p))
                res = "❌ " + " / ".join(parts)
            ap("| %s | %s | %s | %s%s | %d | %d | %d | %s |" % (
                r["source"], r["status_tag"], r["cond"], fmt_items(r["tpls"], loc_ch, loc_en),
                star, need, h, p, res))
        ap("")

    sat = sum(1 for r in rows if status(r) == 1)
    sat_pick = sum(1 for r in rows if status(r) == 0 and group_picked(r["tpls"], pick_sums) >= r["need"])
    ap("## 统计汇总")
    ap("")
    ap("- 总需求项: %d（任务 %d / 藏身处 %d）" % (
        len(rows), sum(1 for r in rows if r["kind"] == "任务"),
        sum(1 for r in rows if r["kind"] == "藏身处")))
    ap("- 库存已满足: %d / 待补齐: %d" % (sat, len(rows) - sat))
    if pick_exists:
        ap("- 局内已捡够(未入库): %d / 仍需在局内捡: %d" % (
            sat_pick, sum(1 for r in rows if status(r) == 0 and group_picked(r["tpls"], pick_sums) < r["need"])))
    ap("")
    ap("---")
    ap("*本报告由 quest_hideout_checker.py v2 生成; 接新任务、藏身处升级或打完一局后请重新运行。*")

    fname = "需求核对_%s.md" % now.strftime("%Y%m%d_%H%M%S")
    out_path = os.path.join(out_dir, fname)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write("\n".join(L))
    print("完成: 任务 %d / 藏身处 %d, 已满足 %d" % (
        sum(1 for r in rows if r["kind"] == "任务"),
        sum(1 for r in rows if r["kind"] == "藏身处"), sat))
    if pick_exists:
        print("局内拾取: 记录 %d 条; 另 %d 项局内已捡够" % (pick_records, sat_pick))
    else:
        print("局内拾取: 无数据文件 (%s) - 先打一局测试" % pickups_path)
    print("报告已生成:", out_path)

    # v3: 导出局内拾取提示用的需求快照
    if export_path:
        try:
            snap = build_needs_snapshot(rows, held, pick_sums, loc_ch, loc_en, info, profile_id)
            export_snapshot(snap, export_path)
            print("局内提示快照: %s" % export_path)
            print("          含物品 %d 种 / 需求来源 %d 条" % (
                snap["stats"]["itemCount"], snap["stats"]["sourceCount"]))
        except Exception as e:
            print("[警告] 局内提示快照导出失败: %s" % e)


if __name__ == "__main__":
    main()