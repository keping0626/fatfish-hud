using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RaidPickupHint;

// ─────────────────────────────────────────────────────────────────────────────
// 目标清单（"购物清单"）：跨局保留，直到凑齐或局数用尽。
//   三种目标：item（某个物品）/ quest（某个任务阶段）/ hideout（某个藏身处设施的某一级）
//   数据落在 BepInEx\config\deepseek.keping.raidpickuphint.targets.json，可手工编辑（30 秒热加载），
//   也可用游戏内管理面板（默认 F9）增删。
//   本文件是纯逻辑（只依赖 NeedsSnapshot 与 RaidHintState），可离线自检。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>目标清单里的一条目标。</summary>
public class TargetEntry
{
    public string kind;      // "item" | "quest" | "hideout"
    public string tpl;       // kind=item：物品 tpl
    public string label;     // kind=quest/hideout：任务名 / 设施名（与快照 source.label 一致）
    public string stage;     // kind=quest/hideout：阶段，如 "进行中" / "级1→2"；留空 = 该来源全部阶段
    public string name;      // 显示名（手工编辑时便于辨认；展示优先用快照里的权威名）
    public int roundsLeft;   // 剩余局数；0 = 永久保留（不按局数淘汰）
    public string addedAt;   // 加入时间（ISO）

    [JsonIgnore] public bool CompletedNotified;

    [JsonIgnore]
    public string Key
    {
        get
        {
            if (kind == "item") return "item:" + (tpl ?? "");
            return (kind ?? "?") + ":" + (label ?? "") + "|" + (stage ?? "");
        }
    }

    [JsonIgnore]
    public string KindText
    {
        get
        {
            if (kind == "item") return "物品";
            if (kind == "quest") return "任务";
            if (kind == "hideout") return "藏身处";
            return kind;
        }
    }
}

/// <summary>目标清单文件（手工可编辑）。</summary>
public class TargetFile
{
    public int schema = 1;
    public string hint =
        "目标清单：kind=item 用 tpl；kind=quest 用 label(+stage)；kind=hideout 用 label+stage。" +
        "roundsLeft=0 表示永久保留。改完保存即可，游戏内 30 秒内热加载。";
    public List<TargetEntry> targets = new List<TargetEntry>();
}

/// <summary>目标展开后的一行（一件物品在该目标范围内的需求状态）。</summary>
internal class TargetLine
{
    public string Tpl;
    public string Name;
    public int Need;
    public int HeldGroup;
    public int Remain;

    public bool Done { get { return Remain <= 0; } }
}

/// <summary>一条目标展开后的全部行。</summary>
internal class TargetGroup
{
    public TargetEntry Entry;
    public string Title;
    public List<TargetLine> Lines = new List<TargetLine>();

    public bool AllDone
    {
        get
        {
            if (Lines.Count == 0) return false;
            for (int i = 0; i < Lines.Count; i++)
                if (!Lines[i].Done) return false;
            return true;
        }
    }

    public int DoneCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Lines.Count; i++) if (Lines[i].Done) n++;
            return n;
        }
    }
}

/// <summary>管理面板里的一条候选（来自快照）。</summary>
internal class TargetCandidate
{
    public string Kind;      // item | quest | hideout
    public string Label;     // 物品名 / 任务名 / 设施名
    public string Stage;
    public string Tpl;
    public int ItemCount;    // 该任务/设施涉及多少种物品

    public string Key
    {
        get
        {
            if (Kind == "item") return "item:" + Tpl;
            return Kind + ":" + Label + "|" + Stage;
        }
    }

    public string Display
    {
        get
        {
            if (Kind == "item") return Label;
            if (string.IsNullOrEmpty(Stage)) return Label;
            return Label + " " + Stage;
        }
    }
}

internal static class TargetList
{
    internal static readonly List<TargetEntry> Entries = new List<TargetEntry>();

    /// <summary>内容版本号，HUD 据此决定是否重建文本（避免每帧拼串）。</summary>
    internal static int Version { get; private set; }

    internal static string FilePath;
    internal static string LastError;

    private static DateTime _fileWriteUtc = DateTime.MinValue;
    private static bool _missingLogged;

    internal static void SetPath(string path) { FilePath = path; }
    internal static void Bump() { Version++; }

    // ── 持久化 ───────────────────────────────────────────────────────────────

    internal static void Load(bool force)
    {
        try
        {
            if (string.IsNullOrEmpty(FilePath)) return;

            if (!File.Exists(FilePath))
            {
                if (force && !_missingLogged && Plugin.Log != null)
                {
                    _missingLogged = true;
                    Plugin.Log.LogInfo("目标清单文件尚不存在（首次使用，加入目标后自动创建）：" + FilePath);
                }
                return;
            }

            var write = File.GetLastWriteTimeUtc(FilePath);
            if (!force && write <= _fileWriteUtc) return;   // 外部没改过，不必重载

            var text = File.ReadAllText(FilePath, new UTF8Encoding(false));
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);   // 容忍 BOM

            var f = JsonConvert.DeserializeObject<TargetFile>(text);
            Entries.Clear();
            if (f != null && f.targets != null)
            {
                for (int i = 0; i < f.targets.Count; i++)
                {
                    var t = f.targets[i];
                    if (t == null || string.IsNullOrEmpty(t.kind)) continue;
                    t.CompletedNotified = false;
                    Entries.Add(t);
                }
            }
            _fileWriteUtc = write;
            LastError = null;
            Version++;
            if (Plugin.Log != null)
                Plugin.Log.LogInfo("目标清单已加载：" + Entries.Count + " 条（" + FilePath + "）");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (Plugin.Log != null) Plugin.Log.LogWarning("目标清单加载失败：" + ex.Message);
        }
    }

    internal static void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(FilePath)) return;

            var f = new TargetFile();
            for (int i = 0; i < Entries.Count; i++) f.targets.Add(Entries[i]);

            // 必须用无 BOM 的 UTF8（历史上 BOM 曾让 Python 侧解析首行失败）
            var json = JsonConvert.SerializeObject(f, Formatting.Indented);
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            try { _fileWriteUtc = File.GetLastWriteTimeUtc(FilePath); } catch { }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (Plugin.Log != null) Plugin.Log.LogWarning("目标清单保存失败：" + ex.Message);
        }
    }

    // ── 增删 ─────────────────────────────────────────────────────────────────

    internal static bool ContainsKey(string key)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i] != null && Entries[i].Key == key) return true;
        return false;
    }

    internal static int IndexOfKey(string key)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i] != null && Entries[i].Key == key) return i;
        return -1;
    }

    /// <summary>加入一条目标（去重）；rounds 由配置给出，0 = 永久。</summary>
    internal static bool Add(TargetEntry e, int rounds)
    {
        if (e == null) return false;
        if (ContainsKey(e.Key)) return false;

        e.roundsLeft = rounds;
        e.CompletedNotified = false;
        e.addedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        Entries.Add(e);
        Version++;
        Save();
        return true;
    }

    internal static bool RemoveKey(string key)
    {
        int i = IndexOfKey(key);
        if (i < 0) return false;
        Entries.RemoveAt(i);
        Version++;
        Save();
        return true;
    }

    internal static void Clear()
    {
        if (Entries.Count == 0) return;
        Entries.Clear();
        Version++;
        Save();
    }

    // ── 展开 ─────────────────────────────────────────────────────────────────

    private static bool MatchSource(TargetEntry e, SnapshotSource src)
    {
        if (src == null) return false;
        if (e.kind != src.kind) return false;
        if (!string.Equals(src.label, e.label, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(e.stage) && !string.Equals(src.stage, e.stage, StringComparison.Ordinal))
            return false;
        return true;
    }

    private static TargetLine MakeLine(string tpl, SnapshotItem item, List<SnapshotSource> matched)
    {
        var line = new TargetLine { Tpl = tpl, Name = item.DisplayName };
        var srcs = matched != null ? matched : item.sources;
        if (srcs == null || srcs.Count == 0) return line;

        int maxRemain = -1;
        for (int i = 0; i < srcs.Count; i++)
        {
            int r = HintText.Remain(srcs[i], tpl);
            if (r > maxRemain)
            {
                maxRemain = r;
                line.Need = srcs[i].need;
                line.HeldGroup = srcs[i].heldGroup;
            }
        }
        if (maxRemain < 0) maxRemain = 0;
        line.Remain = maxRemain;
        return line;
    }

    internal static string TitleOf(NeedsSnapshot snap, TargetEntry e)
    {
        if (e == null) return "（空目标）";

        if (e.kind == "item")
        {
            SnapshotItem it;
            if (snap != null && snap.items != null && !string.IsNullOrEmpty(e.tpl)
                && snap.items.TryGetValue(e.tpl, out it) && it != null)
                return it.DisplayName;
            if (!string.IsNullOrEmpty(e.name)) return e.name;
            return string.IsNullOrEmpty(e.tpl) ? "（未知物品）" : e.tpl;
        }

        var label = string.IsNullOrEmpty(e.label) ? "（未命名）" : e.label;
        return string.IsNullOrEmpty(e.stage) ? label : label + " " + e.stage;
    }

    internal static TargetGroup Resolve(NeedsSnapshot snap, TargetEntry e)
    {
        var g = new TargetGroup { Entry = e, Title = TitleOf(snap, e) };
        if (snap == null || snap.items == null || e == null) return g;

        foreach (var kv in snap.items)
        {
            var item = kv.Value;
            if (item == null || item.sources == null || item.sources.Count == 0) continue;

            if (e.kind == "item")
            {
                if (kv.Key != e.tpl) continue;
                g.Lines.Add(MakeLine(kv.Key, item, null));
                continue;
            }

            List<SnapshotSource> matched = null;
            for (int i = 0; i < item.sources.Count; i++)
            {
                if (!MatchSource(e, item.sources[i])) continue;
                if (matched == null) matched = new List<SnapshotSource>();
                matched.Add(item.sources[i]);
            }
            if (matched != null) g.Lines.Add(MakeLine(kv.Key, item, matched));
        }

        // 未完成的排前面（缺口大的更靠前），已完成的沉底
        g.Lines.Sort(delegate (TargetLine a, TargetLine b)
        {
            if (a.Done != b.Done) return a.Done ? 1 : -1;
            return b.Remain - a.Remain;
        });
        return g;
    }

    internal static List<TargetGroup> ResolveAll(NeedsSnapshot snap)
    {
        var list = new List<TargetGroup>(Entries.Count);
        for (int i = 0; i < Entries.Count; i++)
            list.Add(Resolve(snap, Entries[i]));
        return list;
    }

    // ── 完成提醒 / 出局结算 ──────────────────────────────────────────────────

    /// <summary>取出"本局刚刚凑齐"的目标（每个目标只提醒一次）。</summary>
    internal static List<TargetEntry> TakeNewlyCompleted(NeedsSnapshot snap)
    {
        List<TargetEntry> fresh = null;
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            if (e == null || e.CompletedNotified) continue;

            var g = Resolve(snap, e);
            if (g.Lines.Count == 0 || !g.AllDone) continue;

            e.CompletedNotified = true;
            if (fresh == null) fresh = new List<TargetEntry>();
            fresh.Add(e);
        }
        return fresh;
    }

    /// <summary>
    /// 出局结算（撤离回主菜单时调用，**必须在清空本局已捡计数之前**）：
    ///   ① 本局已凑齐的目标 → 自动移除；
    ///   ② 其余目标 roundsLeft-1，归零则移除（0 = 永久，不动）。
    /// </summary>
    internal static void OnRaidEnd(NeedsSnapshot snap)
    {
        bool changed = false;

        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            var g = Resolve(snap, e);

            if (g.Lines.Count > 0 && g.AllDone)
            {
                Entries.RemoveAt(i);
                changed = true;
                if (Plugin.Log != null) Plugin.Log.LogInfo("目标已完成，自动移除：" + g.Title);
                continue;
            }

            e.CompletedNotified = false;      // 跨局重置提醒标记

            if (e.roundsLeft > 0)
            {
                e.roundsLeft--;
                changed = true;
                if (e.roundsLeft <= 0)
                {
                    Entries.RemoveAt(i);
                    if (Plugin.Log != null) Plugin.Log.LogInfo("目标局数用尽，已移除：" + g.Title);
                }
            }
        }

        if (changed)
        {
            Version++;
            Save();
            if (Plugin.Log != null)
                Plugin.Log.LogInfo("目标清单出局结算完成，剩余 " + Entries.Count + " 条");
        }
    }

    // ── 候选（供管理面板）────────────────────────────────────────────────────

    /// <summary>从快照汇总可加入的目标：进行中任务、未满级藏身处设施、全部有需求物品。</summary>
    internal static List<TargetCandidate> BuildCandidates(NeedsSnapshot snap)
    {
        var map = new Dictionary<string, TargetCandidate>(StringComparer.Ordinal);
        var items = new List<TargetCandidate>();

        if (snap != null && snap.items != null)
        {
            foreach (var kv in snap.items)
            {
                var item = kv.Value;
                if (item == null) continue;

                items.Add(new TargetCandidate
                {
                    Kind = "item",
                    Label = item.DisplayName,
                    Tpl = kv.Key,
                    ItemCount = 1,
                });

                if (item.sources == null) continue;
                for (int i = 0; i < item.sources.Count; i++)
                {
                    var s = item.sources[i];
                    if (s == null) continue;
                    if (s.kind != "quest" && s.kind != "hideout") continue;

                    var key = s.kind + ":" + s.label + "|" + s.stage;
                    TargetCandidate c;
                    if (!map.TryGetValue(key, out c))
                    {
                        c = new TargetCandidate
                        {
                            Kind = s.kind,
                            Label = s.label,
                            Stage = s.stage,
                            ItemCount = 0,
                        };
                        map[key] = c;
                    }
                    c.ItemCount++;
                }
            }
        }

        var list = new List<TargetCandidate>(map.Count + items.Count);
        foreach (var kv in map)
        {
            // 任务排前面，其次藏身处
            kv.Value.ItemCount = kv.Value.ItemCount;
            list.Add(kv.Value);
        }
        list.Sort(delegate (TargetCandidate a, TargetCandidate b)
        {
            int ka = a.Kind == "quest" ? 0 : 1;
            int kb = b.Kind == "quest" ? 0 : 1;
            if (ka != kb) return ka - kb;
            int c = string.Compare(a.Label, b.Label, StringComparison.Ordinal);
            if (c != 0) return c;
            return string.Compare(a.Stage, b.Stage, StringComparison.Ordinal);
        });

        items.Sort(delegate (TargetCandidate a, TargetCandidate b)
        {
            return string.Compare(a.Label, b.Label, StringComparison.Ordinal);
        });
        list.AddRange(items);
        return list;
    }

    /// <summary>由候选生成目标条目。</summary>
    internal static TargetEntry EntryFrom(TargetCandidate c)
    {
        if (c == null) return null;
        return new TargetEntry
        {
            kind = c.Kind,
            tpl = c.Kind == "item" ? c.Tpl : null,
            label = c.Kind == "item" ? null : c.Label,
            stage = c.Kind == "item" ? null : c.Stage,
            name = c.Label,
        };
    }
}
