using System;
using System.Collections.Generic;
using UnityEngine;

namespace RaidPickupHint;

/// <summary>
/// 目标清单管理面板（默认 F9 开关，可配置）。
///
/// 三个来源（全部来自需求快照）：
///   ① 任务：进行中的任务阶段及其所需物品；
///   ② 藏身处设施：下一级（快照里标为「级X→Y」）所需的物品；
///   ③ 物品：快照里有需求来源的全部物品。
/// 「加入」写进目标清单并立即落盘——与手工编辑 deepseek.keping.raidpickuphint.targets.json 是同一份数据。
///
/// 注意：Unity 默认 GUI 皮肤不含中文字形，故所有样式都显式套用系统字体（UiFont）。
/// </summary>
internal static class TargetPickerUi
{
    internal static bool Visible;

    private static int _tab;
    private static string _search = string.Empty;
    private static Vector2 _scroll;
    private static List<TargetCandidate> _candidates;
    private static NeedsSnapshot _candidateSnap;

    private static GUIStyle _label, _button, _title, _small, _field;
    private static int _styleSize = -1;
    private static Texture2D _bg;
    private static int _errors;

    internal static void Toggle()
    {
        Visible = !Visible;
        if (Visible) _search = string.Empty;
        if (Plugin.Log != null)
            Plugin.Log.LogInfo("目标管理面板" + (Visible ? "已打开" : "已关闭")
                               + "（当前目标 " + TargetList.Entries.Count + " 条）");
    }

    private static Texture2D Bg()
    {
        if (_bg == null)
        {
            _bg = new Texture2D(1, 1);
            _bg.SetPixel(0, 0, Color.white);
            _bg.Apply();
        }
        return _bg;
    }

    private static void EnsureStyles(int size)
    {
        if (_label == null)
        {
            _label = new GUIStyle(GUI.skin.label);
            _label.wordWrap = false;
            _button = new GUIStyle(GUI.skin.button);
            _button.wordWrap = false;
            _title = new GUIStyle(GUI.skin.label);
            _small = new GUIStyle(GUI.skin.label);
            _small.wordWrap = true;
            _field = new GUIStyle(GUI.skin.textField);
        }

        if (_styleSize == size) return;
        _styleSize = size;

        var f = UiFont.Get(size);
        if (f != null)
        {
            _label.font = f; _button.font = f; _title.font = f; _small.font = f; _field.font = f;
        }
        _label.fontSize = size;
        _button.fontSize = size;
        _title.fontSize = size + 2;
        _small.fontSize = Mathf.Max(10, size - 2);
        _field.fontSize = size;
    }

    internal static void Draw(HudSettings s)
    {
        if (!Visible) return;
        try
        {
            int size = s != null ? Mathf.Clamp(s.PickerFontSize, 10, 28) : 14;
            EnsureStyles(size);

            float w = Mathf.Min(800f, Screen.width * 0.68f);
            float h = Mathf.Min(640f, Screen.height * 0.80f);
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.88f);
            GUI.DrawTexture(rect, Bg());
            GUI.color = prev;

            GUILayout.BeginArea(new Rect(rect.x + 14, rect.y + 10, rect.width - 28, rect.height - 20));
            DrawBody(s);
            GUILayout.EndArea();
        }
        catch (Exception ex)
        {
            if (_errors++ < 3 && Plugin.Log != null)
                Plugin.Log.LogWarning("目标管理面板渲染异常（第 " + _errors + " 次，已忽略）: " + ex);
        }
    }

    private static void DrawBody(HudSettings s)
    {
        GUILayout.Label("目标清单管理（按 F9 关闭）　当前 " + TargetList.Entries.Count + " 条", _title);
        GUILayout.Space(4);

        // 标签页用 Button 而非 Toolbar：Toolbar 走 GUI.skin，字体不含中文会显示成方块
        string[] tabs = { "任务", "藏身处设施", "物品", "当前目标" };
        GUILayout.BeginHorizontal();
        for (int i = 0; i < tabs.Length; i++)
        {
            if (GUILayout.Button((_tab == i ? "● " : "") + tabs[i], _button, GUILayout.Width(130), GUILayout.Height(30)))
            {
                _tab = i;
                _scroll = Vector2.zero;
            }
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(4);

        if (_tab != 3)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("搜索", _label, GUILayout.Width(40));
            _search = GUILayout.TextField(_search == null ? string.Empty : _search, _field, GUILayout.Height(24));
            if (GUILayout.Button("清空", _button, GUILayout.Width(64), GUILayout.Height(24)))
                _search = string.Empty;
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(4);
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        if (_tab == 3) DrawCurrentTargets(s);
        else DrawCandidates(s);
        GUILayout.EndScrollView();

        GUILayout.Space(4);
        int rounds = s != null ? s.TargetRounds : 3;
        GUILayout.Label("「加入」的目标默认存活 " + (rounds > 0 ? rounds + " 局" : "永久")
                        + "（F12 → 目标清单 → 目标存活局数）；本局凑齐的目标，出局回主菜单时自动移出。",
                        _small);
    }

    private static void DrawCandidates(HudSettings s)
    {
        var snap = Plugin.Snapshot;
        if (snap == null)
        {
            GUILayout.Label("需求快照未加载。先跑一次 D:\\tkf\\xuqiu\\启动_需求核对.bat，30 秒内自动生效。", _label);
            return;
        }

        if (_candidates == null || !ReferenceEquals(_candidateSnap, snap))
        {
            _candidates = TargetList.BuildCandidates(snap);
            _candidateSnap = snap;
        }

        string kind = _tab == 0 ? "quest" : (_tab == 1 ? "hideout" : "item");
        string q = (_search ?? string.Empty).Trim();
        int rounds = s != null ? s.TargetRounds : 3;

        int shown = 0;
        for (int i = 0; i < _candidates.Count; i++)
        {
            var c = _candidates[i];
            if (c.Kind != kind) continue;
            if (q.Length > 0 && c.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;

            bool has = TargetList.ContainsKey(c.Key);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(has ? "移除" : "加入", _button, GUILayout.Width(60), GUILayout.Height(24)))
            {
                if (has) TargetList.RemoveKey(c.Key);
                else TargetList.Add(TargetList.EntryFrom(c), rounds);
            }

            var text = (has ? "✔ " : "　") + c.Display;
            if (c.Kind != "item" && c.ItemCount > 0) text += "（" + c.ItemCount + " 种物品）";
            GUILayout.Label(text, _label);
            GUILayout.EndHorizontal();

            if (++shown >= 500) break;   // 安全上限，避免异常数据把帧时间拖长
        }

        if (shown == 0)
            GUILayout.Label(q.Length > 0 ? "没有匹配的候选" : "（这一类暂时没有候选）", _label);
    }

    private static void DrawCurrentTargets(HudSettings s)
    {
        if (TargetList.Entries.Count == 0)
        {
            GUILayout.Label("（还没有目标）切到「任务 / 藏身处设施 / 物品」标签页，点「加入」。", _label);
            GUILayout.Label("也可以直接编辑 " + (TargetList.FilePath ?? "deepseek.keping.raidpickuphint.targets.json"), _small);
            return;
        }

        var snap = Plugin.Snapshot;
        for (int i = 0; i < TargetList.Entries.Count; i++)
        {
            var e = TargetList.Entries[i];
            if (e == null) continue;
            var g = TargetList.Resolve(snap, e);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("移除", _button, GUILayout.Width(60), GUILayout.Height(24)))
            {
                TargetList.RemoveKey(e.Key);
                return;   // 列表已变，本帧到此为止（下一帧重画）
            }

            var text = "[" + e.KindText + "] " + g.Title;
            if (g.Lines.Count == 0) text += "　快照中没有该来源";
            else if (g.AllDone) text += "　✔ 已凑齐（出局后自动移出）";
            else text += "　" + g.DoneCount + "/" + g.Lines.Count + " 已够";
            if (e.roundsLeft > 0) text += "　剩 " + e.roundsLeft + " 局";
            else text += "　永久";
            GUILayout.Label(text, _label);
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8);
        if (GUILayout.Button("全部清空", _button, GUILayout.Width(120), GUILayout.Height(26)))
            TargetList.Clear();
    }
}
