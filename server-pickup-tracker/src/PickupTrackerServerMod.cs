using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace PickupTrackerServer;

/// <summary>
/// PickupTrackerServer - 服务端 mod：记录每局结束时"局内新到手"的物品，追加写入 data\pickups.json（JSONL）。
///
/// 原理：SPT 局内拾取不走服务端，客户端在结束对局时通过 /client/match/local/end 回传完整 profile；
/// 本 mod 在 MatchController.EndLocalRaidAsync 的 Prefix 里把该 profile 与磁盘上的 raid 前存档做只读 diff。
///
/// 契约：只读，绝不修改 profile / 存档 / 请求数据。
/// </summary>
[Injectable(InjectionType.Singleton, 1)]
public class PickupTrackerServerMod : IOnLoad, IModMetadata
{
    private static Harmony _harmony;
    public string ModGuid { get; init; } = "PickupTrackerServer";
    public string Name { get; init; } = "大肥鱼的拾取记录";
    public string Author { get; init; } = "deepseek + keping";
    public List<string> Contributors { get; init; } = new List<string>();
    public SemanticVersioning.Version Version { get; init; } = new SemanticVersioning.Version("0.2.3", false);
    public SemanticVersioning.Range SptVersion { get; init; } = new SemanticVersioning.Range(">=4.0.0", false);
    public bool HasPrepatcher { get; init; } = false;
    public List<string> Incompatibilities { get; init; } = new List<string>();
    public Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = new Dictionary<string, SemanticVersioning.Range>();
    public string Url { get; init; } = "";
    public string License { get; init; } = "MIT";

    public Task OnLoadAsync(CancellationToken ct)
    {
        PickupRecorder.Initialize();

        try
        {
            var target = AccessTools.Method(typeof(MatchController), "EndLocalRaidAsync");
            if (target == null)
            {
                PickupRecorder.Diag("onload", "target-method-NULL:MatchController.EndLocalRaidAsync");
                return Task.CompletedTask;
            }

            _harmony = new Harmony("PickupTrackerServer");
            var prefixMethod = typeof(EndRaidPatch).GetMethod(
                nameof(EndRaidPatch.Prefix), BindingFlags.Public | BindingFlags.Static);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefixMethod));

            var info = Harmony.GetPatchInfo(target);
            PickupRecorder.Diag("onload",
                "hooked=" + target.DeclaringType?.Name + "." + target.Name
                + ",harmonyId=PickupTrackerServer,prefixes=" + (info?.Prefixes?.Count ?? 0)
                + ",prefixMethod=" + (prefixMethod?.ToString() ?? "NULL"));
        }
        catch (Exception e)
        {
            PickupRecorder.Diag("onload-error", e.GetType().Name + ": " + e.Message);
        }

        return Task.CompletedTask;
    }
}
