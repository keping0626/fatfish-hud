using System.Collections.Generic;
using System.Text;

namespace RaidPickupHint;

/// <summary>
/// 提示文案生成（纯逻辑，便于离线自检）。
///
/// 文案只有两种合法形态（由 HudSettings.WrapInBrackets 选择）：
///   ① 整条【】包裹（默认）：
///      【大肥鱼提醒您：螺母 ×2
///      藏身处·健身房 级0→1｜需3｜仓5+局1｜还差1】
///   ② 完全不带括号：
///      大肥鱼提醒您：螺母 ×2
///      藏身处·健身房 级0→1｜需3｜仓5+局1｜还差1
/// 前缀里若混进了【】，会被 NormalizePrefix 剥掉，避免半包裹/双括号。
/// </summary>
internal static class HintText
{
    /// <summary>某来源当前还差多少个（仓库持有 + 本局已捡，任选组按组合计）。</summary>
    internal static int Remain(SnapshotSource src, string tpl)
    {
        int eff = src.heldGroup + RaidHintState.PickedInGroup(tpl, src.alts);
        int r = src.need - eff;
        return r > 0 ? r : 0;
    }

    /// <summary>某来源当前的有效持有量（仓库 + 本局已捡，组合计）。</summary>
    internal static int EffectiveHeld(SnapshotSource src, string tpl)
    {
        return src.heldGroup + RaidHintState.PickedInGroup(tpl, src.alts);
    }

    /// <summary>某来源本局已捡的数量（任选组按组合计）。</summary>
    internal static int PickedInGroup(string tpl, SnapshotSource src)
    {
        return RaidHintState.PickedInGroup(tpl, src.alts);
    }

    private static List<SnapshotSource> SortedSources(SnapshotItem item, string tpl)
    {
        var list = new List<SnapshotSource>(item.sources);
        list.Sort(delegate (SnapshotSource a, SnapshotSource b)
        {
            int ka = a.IsHideout ? 0 : 1;         // 藏身处设施优先
            int kb = b.IsHideout ? 0 : 1;
            if (ka != kb) return ka - kb;
            int ra = Remain(a, tpl);
            int rb = Remain(b, tpl);
            if (ra != rb) return rb - ra;          // 缺口大的在前
            return b.need - a.need;
        });
        return list;
    }

    /// <summary>拾取提醒的完整文案。</summary>
    internal static string FormatPickup(string tpl, SnapshotItem item, HudSettings s)
    {
        return FormatPickup(tpl, item, s, null);
    }

    /// <summary>拾取提醒的完整文案；tag 非空时作为正文首段（用于自测标记，保证它落在括号内）。</summary>
    internal static string FormatPickup(string tpl, SnapshotItem item, HudSettings s, string tag)
    {
        int pickedTotal = RaidHintState.Picked(tpl);

        var body = new StringBuilder();
        if (!string.IsNullOrEmpty(tag)) body.Append(tag);
        body.Append(item.DisplayName);
        if (pickedTotal > 1) body.Append(" ×").Append(pickedTotal);

        var sources = SortedSources(item, tpl);
        int shown = 0;
        for (int i = 0; i < sources.Count && shown < 4; i++)
        {
            var src = sources[i];
            int pickedHere = PickedInGroup(tpl, src);
            int remain = Remain(src, tpl);

            body.Append('\n');
            body.Append(src.kindText).Append('·').Append(src.DisplayName);
            body.Append("｜需").Append(src.need);
            // 仓库持有与本局捡到的分开写，避免"已有 6"让人以为仓库真有 6 个
            if (pickedHere > 0)
                body.Append("｜仓").Append(src.heldGroup).Append("+局").Append(pickedHere);
            else
                body.Append("｜仓").Append(src.heldGroup);
            body.Append(remain > 0 ? ("｜还差" + remain) : "｜已够");
            if (src.fir) body.Append("｜战局内");
            shown++;
        }

        if (sources.Count > shown)
            body.Append("\n…另有 ").Append(sources.Count - shown).Append(" 处需求");

        return Decorate(body.ToString(), s);
    }

    /// <summary>常驻清单里的一行（紧凑版，最多两个来源）。</summary>
    internal static string FormatPanelLine(string tpl, SnapshotItem item)
    {
        var picked = RaidHintState.Picked(tpl);
        var sb = new StringBuilder();
        sb.Append(item.name);
        if (picked > 1) sb.Append(" ×").Append(picked);

        var sources = SortedSources(item, tpl);
        for (int i = 0; i < sources.Count && i < 2; i++)
        {
            var src = sources[i];
            sb.Append(" → ").Append(src.kindText).Append('·').Append(src.DisplayName);
            int remain = Remain(src, tpl);
            sb.Append(remain > 0 ? (" 还差" + remain) : " 已够");
        }
        return sb.ToString();
    }

    /// <summary>目标清单里的一行（未完成显示缺口，已完成打勾）。</summary>
    internal static string FormatTargetLine(TargetLine line)
    {
        if (line == null) return string.Empty;
        if (line.Done) return "  ✔ " + line.Name;
        return "  " + line.Name + " 还差" + line.Remain;
    }

    /// <summary>目标达成时的提醒正文（走与拾取提示完全相同的渲染路径）。</summary>
    internal static string FormatTargetDone(TargetGroup g, HudSettings s)
    {
        var sb = new StringBuilder();
        sb.Append("目标完成 ✔ ").Append(g.Title);
        int shown = 0;
        for (int i = 0; i < g.Lines.Count && shown < 3; i++)
        {
            var l = g.Lines[i];
            sb.Append('\n').Append(l.Name).Append("｜需").Append(l.Need).Append("｜已够");
            shown++;
        }
        if (g.Lines.Count > shown)
            sb.Append("\n…共 ").Append(g.Lines.Count).Append(" 项已凑齐");
        sb.Append("\n（回主菜单后自动移出目标清单）");
        return Decorate(sb.ToString(), s);
    }

    /// <summary>
    /// 前缀归一化：用户若把【】也写进了"消息前缀"配置里，这里剥掉。
    ///
    /// 约束（用户明确要求）：整条消息只有两种合法形态——
    ///   ① 完全不带括号：前缀：正文…
    ///   ② 【】包裹整条：【前缀：正文…】
    /// 绝不允许"只有前缀带括号"的中间形态，也不允许出现【【…】】双重括号。
    /// </summary>
    internal static string NormalizePrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var p = raw.Trim();
        while (p.Length > 0 && (p[0] == '【' || p[0] == '['))
            p = p.Substring(1).TrimStart();
        while (p.Length > 0 && (p[p.Length - 1] == '】' || p[p.Length - 1] == ']'))
            p = p.Substring(0, p.Length - 1).TrimEnd();
        return p;
    }

    internal static string Decorate(string body, HudSettings s)
    {
        var prefix = NormalizePrefix(s.Prefix);
        if (s.WrapInBrackets)
            return "【" + prefix + body + "】";
        return prefix + body;
    }
}
