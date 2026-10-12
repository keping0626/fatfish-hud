using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace RaidPickupHint;

/// <summary>
/// 局内拾取监听。
///
/// 挂钩点（由 cecdump 反查 Assembly-CSharp 元数据确定，2026-10-12）：
///   EFT.InventoryLogic.ItemController.RaiseAddEvent(AddItemEventArgs)
///   —— 声明为 virtual **final**（子类不 override），patch 基类即覆盖全部路径。
///   全程序集反查 RaiseAddEvent 的调用者仅 7 处，局内拾取必走
///   ItemAddress.RaiseAddEvent → IItemOwner.RaiseAddEvent → ItemController.RaiseAddEvent。
///
/// 三层过滤（缺一不可）：
///   ① 落点所有者必须是本机玩家的 InventoryController（排除交易/挂单/AI/其他玩家）
///   ② 排除武器与弹药内部槽（上膛 patron_in_weapon、弹匣 patron_in_mag/cartridges、改枪 mod_*）
///      —— 服务端曾因 SA58 上膛多记一发 M993，这里是它的客户端等价噪音
///   ③ 排除本局开始时就已经在自己身上的物品 id（背包内整理不该算拾取）
/// </summary>
internal static class PickupWatcher
{
    /// <summary>本局"已经在自己身上"的物品实例 id（进局时快照，用于区分整理与拾取）。</summary>
    private static readonly HashSet<string> KnownItemIds =
        new HashSet<string>(StringComparer.Ordinal);

    internal static bool Patched { get; private set; }

    /// <summary>本次进藏身处是否已记过日志（避免每帧刷屏）。</summary>
    private static bool _hideoutLogged;

    /// <summary>挂载 Harmony Postfix（失败只记日志，不影响游戏）。</summary>
    internal static void Install()
    {
        try
        {
            var target = AccessTools.Method(
                typeof(ItemController),
                "RaiseAddEvent",
                new[] { typeof(AddItemEventArgs) });

            if (target == null)
            {
                Plugin.Log.LogError("未找到 ItemController.RaiseAddEvent(AddItemEventArgs)，拾取提示不可用");
                return;
            }

            var harmony = new Harmony(Plugin.Guid);
            harmony.Patch(target, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(PickupWatcher), "OnAddItem")));

            Patched = true;
            Plugin.Log.LogInfo("拾取钩子已挂载: " + target.FullDescription());
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError("挂载拾取钩子失败: " + ex);
        }
    }

    /// <summary>每帧调用：用 GameWorld 的存在与否判断进局/出局（不依赖额外 patch）。</summary>
    internal static void Tick()
    {
        try
        {
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            var player = gw != null ? gw.MainPlayer : null;
            bool hasPlayer = player != null && player.InventoryController != null;

            // 藏身处也有 GameWorld + MainPlayer（类型是 EFT.HideoutPlayer，继承 EFT.LocalPlayer），
            // 且那一刻 player.Inventory 是整份 profile（含仓库，实机实测 1821 件）——
            // 绝不能当作战局：否则在藏身处交易/收邮件/整理仓库都会被当成"局内拾取"而弹提示。
            if (hasPlayer && IsHideout(gw, player))
            {
                if (!_hideoutLogged)
                {
                    _hideoutLogged = true;
                    Plugin.Log.LogInfo("藏身处场景：不计入战局（LocationId=" + (gw.LocationId ?? "(null)")
                        + "，玩家类型=" + player.GetType().Name + "），本场景不产生拾取提示");
                }
                if (RaidHintState.InRaid) LeaveRaid("藏身处", false);   // 藏身处不算一局，不结算目标局数
                return;
            }
            _hideoutLogged = false;

            bool inRaid = hasPlayer;

            if (inRaid && !RaidHintState.InRaid)
            {
                RaidHintState.InRaid = true;
                RaidHintState.ResetForRaid();
                SnapshotKnownItems(player);
                CheckProfileMatch(player);
                if (HintHud.Instance != null) HintHud.Instance.ClearAll();
                Plugin.Log.LogInfo("进入战局：已记录身上物品 " + KnownItemIds.Count + " 件");
            }
            else if (!inRaid && RaidHintState.InRaid)
            {
                LeaveRaid("已出局", true);   // 真出局：顺便结算目标清单（局数 -1 / 已完成移除）
            }
        }
        catch
        {
            // Singleton 未就绪等异常一律忽略
        }
    }

    /// <summary>
    /// 统一的出局/切场清理。**必须连常驻清单的数据源一起清**：
    /// HintHud.ClearAll() 只清浮动提示队列（_entries），而常驻清单是 DrawPanel 每帧
    /// 从 RaidHintState.PanelRows 现读现画——只清队列的话，撤离回主菜单后清单会继续留在
    /// 屏幕上，直到下一局进图触发 ResetForRaid 才消失。
    /// 实机复现（2026-10-12 03:2x，v0.1.4）：第一次工厂撤离后清单不消失，第二局进图才消失。
    /// </summary>
    /// <param name="settleTargets">
    /// 是否结算目标清单（只有"真正出局"才结算；进藏身处不算一局，否则整理一次仓库就吃掉一局）。
    /// 结算必须在 ResetForRaid 之前做——它要读本局已捡计数（"仓库 + 本局已捡 ≥ 需求"才算凑齐）。
    /// </param>
    private static void LeaveRaid(string reason, bool settleTargets)
    {
        if (settleTargets)
        {
            try { TargetList.OnRaidEnd(Plugin.Snapshot); }
            catch (Exception ex) { Plugin.Log.LogWarning("目标清单结算异常：" + ex.Message); }
        }

        RaidHintState.InRaid = false;
        KnownItemIds.Clear();
        RaidHintState.ResetForRaid();   // 清 PickedThisRaid + PanelRows + PanelVersion++
        if (HintHud.Instance != null) HintHud.Instance.ClearAll();
        Plugin.RefreshTargetGroups(true);
        Plugin.Log.LogInfo("离开战局：本局提示已清理（" + reason + "）");
    }

    /// <summary>
    /// 是否处于藏身处。主判据＝主玩家类型：藏身处里 EFT 创建的是 EFT.HideoutPlayer
    /// （继承 EFT.LocalPlayer，经静态 HideoutPlayer.Create 构造）；战局里是 LocalPlayer 本身。
    /// 再用 GameWorld.LocationId 含 "hideout" 兜底（该字段的实际取值留给实机日志确认）。
    /// </summary>
    private static bool IsHideout(GameWorld gw, Player player)
    {
        if (player is HideoutPlayer) return true;

        var loc = gw != null ? gw.LocationId : null;
        return !string.IsNullOrEmpty(loc)
               && loc.IndexOf("hideout", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void SnapshotKnownItems(Player player)
    {
        KnownItemIds.Clear();
        var inv = player.Inventory;
        if (inv == null) return;

        var all = inv.AllRealPlayerItems;
        if (all == null) return;

        foreach (var it in all)
        {
            if (it != null && !string.IsNullOrEmpty(it.Id))
                KnownItemIds.Add(it.Id);
        }
    }

    /// <summary>
    /// 快照是按哪个角色档生成的？不匹配时只记日志（提示照常工作，但数据可能来自另一个档）。
    /// 用户有两份存档（KEP1NG / keping），这个校验能避免"静默用了旧档的需求"。
    /// </summary>
    private static void CheckProfileMatch(Player player)
    {
        try
        {
            var snap = Plugin.Snapshot;
            if (snap == null || snap.profile == null || string.IsNullOrEmpty(snap.profile.id)) return;

            var profile = player.Profile;
            if (profile == null) return;
            var currentId = profile.Id.ToString();
            if (string.IsNullOrEmpty(currentId)) return;

            if (!string.Equals(currentId, snap.profile.id, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogWarning("需求快照不是当前角色档生成的（快照=" + snap.profile.id
                    + "，当前=" + currentId + "），提示内容可能不准；请重跑一次 启动_需求核对.bat");
            }
        }
        catch
        {
            // 校验失败不影响提示
        }
    }

    /// <summary>Harmony Postfix：物品被加入某个容器后触发。</summary>
    private static void OnAddItem(ItemController __instance, AddItemEventArgs args)
    {
        try
        {
            if (args == null || args.Item == null) return;
            if (!Plugin.Settings.Enabled) return;
            if (!RaidHintState.InRaid) return;      // 只在战局内提示

            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            var player = gw != null ? gw.MainPlayer : null;
            if (player == null || player.IsAI) return;

            var myCtrl = player.InventoryController;
            if (myCtrl == null) return;

            // ① 只有"落进本机玩家身上"才算拾取
            var owner = ContainerExtensions.GetOwner(args.To);
            if (!ReferenceEquals(owner, myCtrl) && !ReferenceEquals(__instance, myCtrl)) return;

            // ② 武器/弹药内部槽：上膛、装弹、改枪都不是拾取
            if (IsInternalSlot(args.To)) return;

            // ③ 本局一开始就在身上的物品（背包内整理）不算拾取
            var id = args.Item.Id;
            if (!string.IsNullOrEmpty(id))
            {
                if (KnownItemIds.Contains(id)) return;
                KnownItemIds.Add(id);
            }

            var tpl = args.Item.StringTemplateId;
            if (string.IsNullOrEmpty(tpl)) return;

            int count = 1;
            try { if (args.Item.StackObjectsCount > 1) count = args.Item.StackObjectsCount; }
            catch { }

            Plugin.OnItemPicked(tpl, count);
        }
        catch (Exception ex)
        {
            // 提示逻辑绝不允许影响游戏本身
            if (Plugin.Log != null)
                Plugin.Log.LogWarning("拾取钩子异常（已忽略）: " + ex.Message);
        }
    }

    /// <summary>落点是否属于武器/弹药内部槽（与 PickupTrackerServer 服务端侧同一套判据）。</summary>
    private static bool IsInternalSlot(ItemAddress to)
    {
        if (to == null) return true;
        var container = to.Container;
        if (container == null) return true;

        var parent = container.ParentItem;
        if (parent is Magazine) return true;   // 弹匣内部（cartridges）
        if (parent is Weapon) return true;     // 枪膛 / 改枪槽

        var cid = container.ID;
        if (!string.IsNullOrEmpty(cid))
        {
            if (cid.StartsWith("mod_", StringComparison.OrdinalIgnoreCase)) return true;
            if (cid.StartsWith("patron_", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(cid, "cartridges", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
