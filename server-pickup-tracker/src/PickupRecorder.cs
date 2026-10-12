using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SptInventory = SPTarkov.Server.Core.Models.Eft.Common.Tables.BotBaseInventory;
using SptItem = SPTarkov.Server.Core.Models.Eft.Common.Tables.Item;
using SptUpd = SPTarkov.Server.Core.Models.Eft.Common.Tables.Upd;

namespace PickupTrackerServer;

/// <summary>
/// 拾取记录器（v0.2.0）。
///
/// 实测结论（2026-10-10，spt20261010.log 逐条核对）：SPT 局内拾取**不经过服务端**。
/// 整局 raid 期间服务端收不到任何 /client/game/profile/items/moving 请求，
/// 客户端在 raid 中本地记账，只有结束对局时通过 /client/match/local/end 把**完整 profile**
/// 回传（EndLocalRaidRequestData.Results.Profile）。因此：
///
///   hook 点 = MatchController.EndLocalRaidAsync 的 Prefix（服务端应用客户端数据之前）
///   新到手物品 = 客户端最终 profile 的物品 − 服务端 raid 前 profile（磁盘 user\profiles\{id}.json）的物品
///   只记录其中归属玩家装备树（Inventory.Equipment 可达）的物品
///
/// 契约：全程只读。不修改 profile、不修改请求数据、不写存档（只追加自己的 data\pickups.json）。
/// 注意：ALT+F4 强退不会触发 local/end，那一局的拾取无法记录（必须正常撤离/结算）。
/// </summary>
public static class PickupRecorder
{
    private static readonly object Gate = new object();
    // 注意：Encoding.UTF8 是"带 BOM"的编码——首次创建文件时会写入 BOM，
    // 导致 Python 侧 json.loads 解析首行失败（核对工具曾因此漏读第一条记录）。必须用无 BOM 版本。
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
    private static string _path;
    private static string _diagPath;
    private static string _profilesDir;
    private static int _diagCount;
    private const int DiagLimit = 20000;
    private static readonly HashSet<string> SeenKeys = new HashSet<string>();

    public static void Initialize()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(typeof(PickupRecorder).Assembly.Location);
            var dataDir = Path.Combine(asmDir ?? ".", "data");
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "pickups.json");
            _diagPath = Path.Combine(dataDir, "_raid_diag.jsonl");
            _profilesDir = ResolveProfilesDir(asmDir);

            if (File.Exists(_path))
            {
                foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("{")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(t);
                        if (doc.RootElement.TryGetProperty("sessionId", out var s)
                            && doc.RootElement.TryGetProperty("itemId", out var i))
                        {
                            var sk = s.GetString();
                            var ik = i.GetString();
                            if (!string.IsNullOrEmpty(sk) && !string.IsNullOrEmpty(ik)) SeenKeys.Add(sk + ":" + ik);
                        }
                    }
                    catch
                    {
                        // 跳过损坏行
                    }
                }
            }

            Diag("initialize", "path=" + _path + " seen=" + SeenKeys.Count
                 + " profilesDir=" + (_profilesDir ?? "NULL")
                 + " profilesDirExists=" + (_profilesDir != null && Directory.Exists(_profilesDir)));
        }
        catch
        {
            _path = null;
            _diagPath = null;
        }
    }

    /// <summary>
    /// 定位 SPT 根目录下的 user\profiles：优先进程基目录，其次由 mod 目录上溯（mods\X → user\mods → user → 根）。
    /// </summary>
    private static string ResolveProfilesDir(string asmDir)
    {
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir)) candidates.Add(Path.Combine(baseDir, "user", "profiles"));

        if (!string.IsNullOrEmpty(asmDir))
        {
            var d = new DirectoryInfo(asmDir);
            for (int up = 0; up < 4 && d != null; up++)
            {
                candidates.Add(Path.Combine(d.FullName, "user", "profiles"));
                d = d.Parent;
            }
        }

        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>诊断日志：data\_raid_diag.jsonl（排查用，可随时删除；失败静默）。</summary>
    public static void Diag(string evt, string detail)
    {
        try
        {
            if (_diagPath == null) return;
            if (Interlocked.Increment(ref _diagCount) > DiagLimit) return;
            var line = JsonSerializer.Serialize(new
            {
                t = DateTime.UtcNow.ToString("o"),
                evt = evt,
                detail = detail
            });
            lock (Gate)
            {
                File.AppendAllText(_diagPath, line + Environment.NewLine, Utf8NoBom);
            }
        }
        catch
        {
            // 诊断失败忽略
        }
    }

    /// <summary>
    /// 由 MatchController.EndLocalRaidAsync 的 Prefix 调用（此时服务端 profile 尚未被客户端数据覆盖）。
    /// </summary>
    public static void OnRaidEnded(MongoId sessionId, EndLocalRaidRequestData request)
    {
        try
        {
            var client = request?.Results?.Profile;
            if (client?.Inventory?.Items == null)
            {
                Diag("raid-end-skip", "no-client-profile sess=" + sessionId
                     + " results=" + (request?.Results != null));
                return;
            }

            var clientItems = client.Inventory.Items;
            var clientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in clientItems) clientIds.Add(it.Id.ToString());

            var profileId = client.Id?.ToString();
            if (string.IsNullOrEmpty(profileId)) profileId = sessionId.ToString();

            var baseline = LoadBaseline(profileId, clientIds, out var baseInfo);
            if (baseline == null)
            {
                Diag("raid-end-skip", "no-baseline profileId=" + profileId
                     + ",clientItems=" + clientItems.Count + "," + baseInfo);
                return;
            }

            var equip = BuildEquipmentContainers(client);
            var samples = new List<string>();
            int newCount = 0, accepted = 0, notEquip = 0, dup = 0, writeFail = 0, internalSlot = 0;

            foreach (var it in clientItems)
            {
                var id = it.Id.ToString();
                if (baseline.Contains(id)) continue;

                newCount++;
                var tpl = it.Template.ToString();
                var slot = it.SlotId;
                var inEquip = equip.Contains(id);
                var isInternal = IsInternalSlot(slot);
                if (samples.Count < 15) samples.Add(tpl + (inEquip ? ":E" : ":x") + "/" + (slot ?? "-"));

                // 武器内部槽（枪膛 / 弹匣 / 改枪配件）的新 itemId 是局内上膛、装弹、装配时客户端新生成的，
                // 不代表"捡到"——实例：SA58 枪膛里那发 M993 曾因此被误记。
                if (isInternal) { internalSlot++; continue; }
                if (!inEquip) { notEquip++; continue; }

                var count = it.Upd?.StackObjectsCount ?? 1d;
                lock (Gate)
                {
                    if (!SeenKeys.Add(profileId + ":" + id))
                    {
                        dup++;
                        continue;
                    }
                    if (_path == null) continue;
                    try
                    {
                        var record = new
                        {
                            sessionId = profileId,
                            tpl = tpl,
                            itemId = id,
                            count = (int)count,
                            timeUtc = DateTime.UtcNow.ToString("o"),
                            map = (string)null,
                            source = "raidEnd"
                        };
                        File.AppendAllText(_path, JsonSerializer.Serialize(record) + Environment.NewLine, Utf8NoBom);
                        accepted++;
                    }
                    catch
                    {
                        writeFail++;
                    }
                }
            }

            Diag("raid-end", "profileId=" + profileId
                 + ",clientItems=" + clientItems.Count
                 + ",baseItems=" + baseline.Count
                 + ",new=" + newCount
                 + ",accepted=" + accepted
                 + ",notEquip=" + notEquip
                 + ",internalSlot=" + internalSlot
                 + ",dup=" + dup
                 + ",writeFail=" + writeFail
                 + ",base={" + baseInfo + "}"
                 + ",samples=[" + string.Join(",", samples) + "]");
        }
        catch (Exception e)
        {
            Diag("raid-end-error", e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>
    /// raid 前基准 = 磁盘存档（服务端在 raid 结束时才写回，故 Prefix 阶段仍是 raid 前状态）。
    /// pmc / scav 两个角色里取与客户端 profile 交集更大的那个作为基准。
    /// </summary>
    private static HashSet<string> LoadBaseline(string profileId, HashSet<string> clientIds, out string info)
    {
        info = "";
        if (string.IsNullOrEmpty(_profilesDir) || !Directory.Exists(_profilesDir))
        {
            info = "profiles-dir-missing=" + (_profilesDir ?? "NULL");
            return null;
        }

        var file = Path.Combine(_profilesDir, profileId + ".json");
        if (!File.Exists(file))
        {
            info = "file-missing=" + file;
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        if (!doc.RootElement.TryGetProperty("characters", out var chars))
        {
            info = "no-characters-node";
            return null;
        }

        HashSet<string> best = null;
        string bestName = null;
        int bestScore = -1, bestSize = 0;

        foreach (var name in new[] { "pmc", "scav" })
        {
            if (!chars.TryGetProperty(name, out var node)) continue;
            if (!node.TryGetProperty("Inventory", out var inv)) continue;
            if (!inv.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array) continue;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in itemsEl.EnumerateArray())
            {
                if (it.TryGetProperty("_id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    set.Add(idEl.GetString());
                }
            }

            var score = 0;
            foreach (var id in set)
            {
                if (clientIds.Contains(id)) score++;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = set;
                bestName = name;
                bestSize = set.Count;
            }
        }

        if (best == null)
        {
            info = "no-candidate-set";
            return null;
        }

        info = "from=" + bestName + ",size=" + bestSize + ",overlap=" + bestScore
               + ",file=" + Path.GetFileName(file);
        return best;
    }

    /// <summary>
    /// 该 slotId 是否属于"武器 / 弹药内部槽"。
    /// 局内上膛（patron_in_weapon）、装弹（patron_in_mag / cartridges）、改枪（mod_*）都会让客户端
    /// 生成新的物品实例 id，这些不属于"捡到"，必须排除（否则枪膛里那发子弹会被误记）。
    /// </summary>
    private static bool IsInternalSlot(string slotId)
    {
        if (string.IsNullOrEmpty(slotId)) return false;
        if (slotId.StartsWith("mod_", StringComparison.OrdinalIgnoreCase)) return true;
        if (slotId.StartsWith("patron_", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(slotId, "cartridges", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>BFS 收集玩家装备树上的所有 item id（根为 Inventory.Equipment）。</summary>
    private static HashSet<string> BuildEquipmentContainers(PmcData pmcData)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = pmcData?.Inventory?.Equipment;
        if (root == null) return set;

        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in pmcData.Inventory.Items)
        {
            var pid = it.ParentId;
            if (string.IsNullOrEmpty(pid)) continue;
            if (!children.TryGetValue(pid, out var list))
            {
                list = new List<string>();
                children[pid] = list;
            }
            list.Add(it.Id.ToString());
        }

        var queue = new Queue<string>();
        queue.Enqueue(root.Value.ToString());
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!set.Add(cur)) continue;
            if (children.TryGetValue(cur, out var kids))
            {
                foreach (var k in kids) queue.Enqueue(k);
            }
        }

        return set;
    }

    /// <summary>
    /// 离线自检（只由 _tools\verify_mod 调用，游戏内不会执行）。
    ///
    /// 不反序列化整份存档（SPT 的 MongoId 需要其自定义转换器），而是：
    ///   1) 从真实存档读出装备根 / 仓库 id 与 3 件"已存在"的物品 id；
    ///   2) 手工构造客户端回传 profile：3 件已存在 + 1 件装备树内新增 + 1 件仓库内新增；
    ///   3) 跑完整 raid 结束流程，预期 new=2 / accepted=1 / notEquip=1（已存在的被 diff 排除）。
    /// 结束后把 pickups.json 恢复原样。
    /// </summary>
    public static void RunSelfTest(string profileId)
    {
        byte[] backup = null;
        var hadFile = false;
        try
        {
            if (string.IsNullOrEmpty(_profilesDir) || !Directory.Exists(_profilesDir))
            {
                Diag("selftest", "profiles-dir-missing=" + (_profilesDir ?? "NULL"));
                return;
            }

            string file;
            if (!string.IsNullOrEmpty(profileId))
            {
                file = Path.Combine(_profilesDir, profileId + ".json");
            }
            else
            {
                file = new DirectoryInfo(_profilesDir).GetFiles("*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                Diag("selftest", "file-missing=" + file);
                return;
            }
            profileId = Path.GetFileNameWithoutExtension(file);

            hadFile = _path != null && File.Exists(_path);
            if (hadFile) backup = File.ReadAllBytes(_path);

            string equipId = null, stashId = null;
            var existingItems = new List<KeyValuePair<string, string>>();
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(file)))
            {
                var inv = doc.RootElement.GetProperty("characters").GetProperty("pmc").GetProperty("Inventory");
                if (inv.TryGetProperty("equipment", out var eqEl) && eqEl.ValueKind == JsonValueKind.String)
                    equipId = eqEl.GetString();
                if (inv.TryGetProperty("stash", out var stEl) && stEl.ValueKind == JsonValueKind.String)
                    stashId = stEl.GetString();
                if (inv.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in itemsEl.EnumerateArray())
                    {
                        if (existingItems.Count >= 3) break;
                        if (!it.TryGetProperty("_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                        // 保留真实父子关系，避免自检自己把仓库误接进装备树
                        if (!it.TryGetProperty("parentId", out var pEl) || pEl.ValueKind != JsonValueKind.String) continue;
                        existingItems.Add(new KeyValuePair<string, string>(idEl.GetString(), pEl.GetString()));
                    }
                }
            }

            if (string.IsNullOrEmpty(equipId))
            {
                Diag("selftest", "no-equipment-root in " + file);
                return;
            }

            var pmc = new PmcData
            {
                Id = new MongoId(profileId),
                Inventory = new SptInventory
                {
                    Equipment = new MongoId(equipId),
                    Stash = string.IsNullOrEmpty(stashId) ? (MongoId?)null : new MongoId(stashId),
                    Items = new List<SptItem>()
                }
            };

            // 1) 基准里已存在的物品（保留真实 parentId）→ 必须被 diff 排除
            foreach (var pair in existingItems)
            {
                pmc.Inventory.Items.Add(new SptItem
                {
                    Id = new MongoId(pair.Key),
                    Template = new MongoId("5448bd6b4dad0c2b0d8b4569"),
                    ParentId = pair.Value,
                    SlotId = "hideout",
                    Upd = new SptUpd { StackObjectsCount = 1 }
                });
            }

            // 2) 假新增：装备树内 → 应记录
            var fakeEquip = new SptItem
            {
                Id = NewMongoId(),
                Template = new MongoId("590c392f86f77444754deb29"),
                ParentId = equipId,
                SlotId = "FirstPrimaryWeapon",
                Upd = new SptUpd { StackObjectsCount = 1 }
            };
            pmc.Inventory.Items.Add(fakeEquip);

            // 3) 假新增：仓库内 → 应排除
            var fakeStash = new SptItem
            {
                Id = NewMongoId(),
                Template = new MongoId("5448bd6b4dad0c2b0d8b4569"),
                ParentId = string.IsNullOrEmpty(stashId) ? equipId : stashId,
                SlotId = "hideout",
                Upd = new SptUpd { StackObjectsCount = 1 }
            };
            pmc.Inventory.Items.Add(fakeStash);

            // 4) 假新增：在装备树上，但属于武器内部槽（枪膛）→ 应被内部槽规则排除
            var fakeChamber = new SptItem
            {
                Id = NewMongoId(),
                Template = new MongoId("5efb0c1bd79ff02a1f5e68d9"),
                ParentId = equipId,
                SlotId = "patron_in_weapon",
                Upd = new SptUpd { StackObjectsCount = 1 }
            };
            pmc.Inventory.Items.Add(fakeChamber);

            var request = new EndLocalRaidRequestData
            {
                Results = new EndRaidResult { Profile = pmc }
            };

            Diag("selftest-start", "profileId=" + profileId + ",file=" + Path.GetFileName(file)
                 + ",existing=" + existingItems.Count + ",equipRoot=" + equipId + ",stash=" + (stashId ?? "NULL")
                 + ",fakeEquip=" + fakeEquip.Id + ",fakeStash=" + fakeStash.Id
                 + ",fakeChamber=" + fakeChamber.Id);

            OnRaidEnded(new MongoId(profileId), request);

            var wrote = _path != null && File.Exists(_path) ? File.ReadAllText(_path, Encoding.UTF8) : "";
            Diag("selftest-done", "expect new=3,accepted=1,notEquip=1,internalSlot=1 | fakeEquipInFile="
                 + wrote.Contains(fakeEquip.Id.ToString())
                 + ",fakeStashInFile=" + wrote.Contains(fakeStash.Id.ToString())
                 + ",fakeChamberInFile=" + wrote.Contains(fakeChamber.Id.ToString()));
        }
        catch (Exception e)
        {
            Diag("selftest-error", e.GetType().Name + ": " + e.Message
                 + " | inner=" + (e.InnerException?.Message ?? "none"));
        }
        finally
        {
            try
            {
                if (_path != null)
                {
                    if (backup != null) File.WriteAllBytes(_path, backup);
                    else if (!hadFile && File.Exists(_path)) File.Delete(_path);
                }
            }
            catch
            {
                // 恢复失败忽略
            }
        }
    }

    private static MongoId NewMongoId()
    {
        return new MongoId(Guid.NewGuid().ToString("N").Substring(0, 24));
    }
}
