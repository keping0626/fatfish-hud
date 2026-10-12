using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace RaidPickupHint;

// ─────────────────────────────────────────────────────────────────────────────
// 需求快照的数据模型：与 quest_hideout_checker.py 的 --export-needs 输出一一对应
// （JSON 字段名保持 snake/short 风格，故这里用公共字段而非属性，便于 Newtonsoft 直接映射）
// ─────────────────────────────────────────────────────────────────────────────

public class SnapshotProfile
{
    public string id;
    public string nickname;
    public int level;
    public string side;
}

public class SnapshotStats
{
    public int itemCount;
    public int sourceCount;
}

public class SnapshotAlt
{
    public string tpl;
    public string name;
}

/// <summary>一条需求来源：某个任务阶段或某个藏身处设施的某一级。</summary>
public class SnapshotSource
{
    public string kind;      // "quest" | "hideout"
    public string kindText;  // "任务" | "藏身处"（已本地化）
    public string label;     // 任务名 / 设施名
    public string stage;     // "进行中" / "级0→1"
    public string cond;      // "持有" / "递交" / "投入" / "持有+递交"
    public int need;         // 该来源需要多少个
    public int heldGroup;    // 仓库持有量（任选组为组内合计，与核对工具口径一致）
    public int picked;       // 快照生成时记录的局内已捡数（仅作参考）
    public bool fir;         // 是否要求"战局内找到"
    public List<SnapshotAlt> alts;   // 任选组内的其他可选物品

    public bool IsHideout { get { return kind == "hideout"; } }

    /// <summary>来源的展示名，如 "健身房 级0→1" 或 "挖矿 - 4 进行中"。</summary>
    public string DisplayName
    {
        get
        {
            if (IsHideout)
                return string.IsNullOrEmpty(stage) ? label : label + " " + stage;
            return string.IsNullOrEmpty(stage) ? label : label + " " + stage;
        }
    }
}

/// <summary>一件有需求的物品及其全部需求来源。</summary>
public class SnapshotItem
{
    public string name;   // 游戏内使用的短名（ShortName）
    public string full;   // 全名（Name）
    public int held;      // 仓库持有量（该物品自身）
    public List<SnapshotSource> sources;

    /// <summary>提示标题用：短名足够区分就用短名，否则补全名。</summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(full) || full == name)
                return name;
            if (string.IsNullOrEmpty(name))
                return full;
            return name + "（" + full + "）";
        }
    }
}

public class NeedsSnapshot
{
    public int schema;
    public string generatedAt;
    public string generator;
    public SnapshotProfile profile;
    public Dictionary<string, SnapshotItem> items;
    public SnapshotStats stats;

    [JsonIgnore] public string sourcePath;
    [JsonIgnore] public DateTime loadedAtUtc;
    [JsonIgnore] public DateTime fileWriteUtc;

    /// <summary>尝试加载快照；失败返回 null 并给出原因（不影响游戏）。</summary>
    public static NeedsSnapshot TryLoad(string path, out string error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "快照文件不存在: " + path;
                return null;
            }
            var text = File.ReadAllText(path, new System.Text.UTF8Encoding(false));
            // 容忍可能存在的 UTF-8 BOM（历史上踩过这个坑）
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);

            var snap = JsonConvert.DeserializeObject<NeedsSnapshot>(text);
            if (snap == null || snap.items == null)
            {
                error = "快照内容为空或缺少 items 字段";
                return null;
            }
            snap.sourcePath = path;
            snap.loadedAtUtc = DateTime.UtcNow;
            try { snap.fileWriteUtc = File.GetLastWriteTimeUtc(path); }
            catch { snap.fileWriteUtc = DateTime.MinValue; }
            return snap;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }
}
