using System.Collections.Generic;

namespace RaidPickupHint;

/// <summary>常驻清单的一行（按物品去重，重复拾取时原地更新）。</summary>
internal class PanelRow
{
    public string Tpl;
    public string Text;
}

/// <summary>
/// 本局运行状态：本局已捡计数（用于把"还差多少个"实时收敛）与常驻清单内容。
/// 只读统计，不修改任何游戏数据。
/// </summary>
internal static class RaidHintState
{
    /// <summary>本局已捡：tpl -> 件数（堆叠按数量累加）。</summary>
    internal static readonly Dictionary<string, int> PickedThisRaid =
        new Dictionary<string, int>();

    /// <summary>常驻清单显示的行（按首次拾取先后，同物品原地更新）。</summary>
    internal static readonly List<PanelRow> PanelRows = new List<PanelRow>();

    /// <summary>清单内容的版本号：每次变更 +1，供 HUD 判断是否需要重建文本（避免每帧拼字符串）。</summary>
    internal static int PanelVersion;

    internal static bool InRaid;

    /// <summary>进局时重置（每个 raid 独立计数）。</summary>
    internal static void ResetForRaid()
    {
        PickedThisRaid.Clear();
        PanelRows.Clear();
        PanelVersion++;
    }

    internal static void AddPickup(string tpl, int count)
    {
        if (string.IsNullOrEmpty(tpl)) return;
        if (count < 1) count = 1;
        int cur;
        PickedThisRaid.TryGetValue(tpl, out cur);
        PickedThisRaid[tpl] = cur + count;
    }

    internal static int Picked(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return 0;
        int v;
        return PickedThisRaid.TryGetValue(tpl, out v) ? v : 0;
    }

    /// <summary>任选组内的本局已捡合计（与快照 heldGroup 的口径一致）。</summary>
    internal static int PickedInGroup(string tpl, List<SnapshotAlt> alts)
    {
        int sum = Picked(tpl);
        if (alts != null)
        {
            for (int i = 0; i < alts.Count; i++)
                sum += Picked(alts[i].tpl);
        }
        return sum;
    }

    /// <summary>写入/更新常驻清单的一行。</summary>
    internal static void UpsertPanel(string tpl, string text)
    {
        for (int i = 0; i < PanelRows.Count; i++)
        {
            if (PanelRows[i].Tpl == tpl)
            {
                if (PanelRows[i].Text == text) return;
                PanelRows[i].Text = text;
                PanelVersion++;
                return;
            }
        }
        PanelRows.Add(new PanelRow { Tpl = tpl, Text = text });
        PanelVersion++;
    }
}
