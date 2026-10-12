using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RaidPickupHint;

/// <summary>
/// 底图填充方式（v0.1.9）：背景图与目标区域的宽高比通常不一致，交给用户自己选。
/// </summary>
internal enum BackdropFill
{
    Stretch,    // 拉伸填满：一定铺满，但会变形
    Crop,       // 等比裁切填满：不变形，超出部分裁掉（默认，适合人物插画）
    Fit,        // 等比完整显示：整张图都在，比例不匹配时留边
}

/// <summary>HUD 外观与行为的运行时设置（由插件从 BepInEx 配置同步过来）。</summary>
internal class HudSettings
{
    public bool Enabled = true;
    public bool ShowPanel = true;

    public string Prefix = "大肥鱼提醒您：";
    public bool WrapInBrackets = true;      // true: 【前缀+正文】；false: 前缀+正文
    public HintRenderMode RenderMode = HintRenderMode.Both;   // 显示载体（自绘 / 原生通知 / 两者）
    public Color TextColor = new Color(0.35f, 0.72f, 1f, 1f);   // 蓝色
    public int FontSize = 16;
    public float Duration = 7f;             // 通知条停留秒数
    public int MaxEntries = 4;              // 同屏最多显示几条通知
    public float OffsetX = 24f;             // 距屏幕边缘
    public float OffsetY = 0.32f;           // 起始高度占屏幕比例
    public bool AnchorRight = true;

    public int PanelMax = 8;                // 常驻清单最多显示几条
    public float PanelOffsetY = 0.55f;

    // ── 目标清单（跨局"购物清单"，与上面的本局清单各自独立开关）──
    public bool ShowTargets = true;         // 是否显示目标清单
    public float TargetOffsetY = 0.74f;     // 目标清单起始高度比例
    public int TargetMaxGroups = 4;         // 最多显示几项目标
    public int TargetLinesPerGroup = 3;     // 每项目标最多列几件未凑齐的物品
    public int TargetRounds = 3;            // 新加入的目标默认存活几局（0=永久）
    public bool NotifyTargetDone = true;    // 目标凑齐时弹提醒
    public int PickerFontSize = 14;         // F9 管理面板字号
    public bool TargetAnchorLeft = true;    // 目标清单靠左（与本局清单的 AnchorRight 互相独立）

    // ── 自定义图片（v0.1.7）──
    public bool ImagesEnabled = true;
    public string ImagesDir = "";           // 图片目录；F12 留空 = BepInEx\config\deepseek.keping.raidpickuphint.images
    public string NoticeIcon = "";          // 每条浮动提示最前面的小图（文件名或完整路径）
    public int IconHeight = 28;             // 小图标显示高度（像素），宽度按原图比例
    public string DoneIcon = "";            // 「目标完成」提示专用的图（留空 = 跟随 NoticeIcon）

    // ── 三块区域各自的底图（v0.1.9：从"一张通用背景板"拆成逐块指定）──
    public string NoticeBackdrop = "";          // 浮动提示条的底图
    public float NoticeBackdropAlpha = 0.62f;   // 0~1
    public string PanelBackdrop = "";           // 「本局拾取」清单的底图
    public string TargetBackdrop = "";          // 「目标清单」的底图
    public float PanelBackdropAlpha = 0.45f;    // 两块清单共用（清单文字密，默认更透一些）
    public BackdropFill Fill = BackdropFill.Crop;   // 底图填充方式
}

internal class HintEntry
{
    public string Text;
    public string IconSpec;                 // 这条提示用哪张图（逐条决定，可为空=不画图）
    public float BornAt;
    public float ExpireAt;
}

/// <summary>
/// HUD 渲染层：画"拾取提醒"通知条与可选的常驻清单。
/// 只读渲染，不碰游戏数据；字体用系统中文字体动态创建（IMGUI 默认字体无中文）。
///
/// 注意：本类必须是 **public**——Unity 对 MonoBehaviour 的消息回调（Awake/OnGUI）
/// 依赖脚本类型可被引擎识别，internal 类型存在静默不回调的风险。
/// </summary>
public class HintHud : MonoBehaviour
{
    internal static HintHud Instance;

    private readonly List<HintEntry> _entries = new List<HintEntry>();
    private Font _font;
    private int _fontSize = -1;
    private GUIStyle _style;
    private Texture2D _bg;
    private int _guiFrames;
    private int _guiErrors;
    private bool _fontWarmed;

    private void Awake()
    {
        Instance = this;
        if (Plugin.Log != null)
            Plugin.Log.LogInfo("HUD 组件已就绪（Awake 已回调），对象名=" + name
                               + "，宿主=" + (transform.parent != null ? transform.parent.name : "（root）"));
    }

    /// <summary>
    /// 诊断用：若日志里出现这行，说明组件宿主对象被销毁了
    /// （2026-10-12 首版正是因此导致自绘 HUD 全程不可见）。
    /// </summary>
    private void OnDestroy()
    {
        if (Plugin.Log != null)
            Plugin.Log.LogWarning("HUD 组件被销毁，对象名=" + name);
    }

    internal void ClearAll()
    {
        _entries.Clear();
    }

    internal void Push(string text, float duration, string iconSpec)
    {
        if (string.IsNullOrEmpty(text)) return;

        _entries.Add(new HintEntry
        {
            Text = text,
            IconSpec = iconSpec,
            BornAt = Time.unscaledTime,
            ExpireAt = Time.unscaledTime + Mathf.Max(1f, duration),
        });

        // 超量时丢掉最旧的，避免刷屏
        var max = Plugin.Settings != null ? Plugin.Settings.MaxEntries : 4;
        while (_entries.Count > Mathf.Max(1, max))
            _entries.RemoveAt(0);
    }

    private void EnsureFont(int size)
    {
        if (_font != null && _fontSize == size) return;
        _font = UiFont.Get(size);   // IMGUI 默认字体无中文字形，必须用系统中文字体
        _fontSize = size;
    }

    private void EnsureStyle(int size, Color color)
    {
        EnsureFont(size);
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label);
            _style.wordWrap = true;
            _style.richText = false;
            _style.alignment = TextAnchor.UpperLeft;
            _style.padding = new RectOffset(10, 10, 7, 7);
        }
        if (_font != null) _style.font = _font;
        _style.fontSize = size;
        _style.normal.textColor = color;
    }

    /// <summary>
    /// 预热字体（v0.1.11）：动态中文字体的字形是**首次渲染时才栅格化进图集**的，
    /// 「建字体对象 + 首次测量一段样本」能把这两笔开销挪到还没有提示要画的帧上。
    /// 只能在 OnGUI 里调用 —— GUIStyle 要基于 GUI.skin 构造，OnGUI 之外拿不到 skin。
    /// </summary>
    private void WarmUpFont(HudSettings s)
    {
        EnsureStyle(s.FontSize, s.TextColor);
        if (_style == null) return;
        // 样本覆盖 HUD 里高频出现的字与符号：CalcSize 会向字体请求字形 → 触发栅格化
        _style.CalcSize(new GUIContent("本局拾取有需求目标清单还差已够藏身处任务进行完成度 0123456789 ×✔｜·（）"));
    }

    private Texture2D Bg()
    {
        if (_bg == null)
        {
            _bg = new Texture2D(1, 1);
            _bg.SetPixel(0, 0, Color.white);
            _bg.Apply();
        }
        return _bg;
    }

    private void OnGUI()
    {
        try
        {
            // 诊断：确认 Unity 真的回调了 OnGUI（实机首测时屏幕无输出，需要区分
            // "没被回调" 与 "画了但看不见"）。只记前 3 帧，不刷屏。
            if (_guiFrames < 3)
            {
                _guiFrames++;
                if (Plugin.Log != null)
                    Plugin.Log.LogInfo("HUD 收到 OnGUI 回调（第 " + _guiFrames + " 帧，队列 "
                                       + _entries.Count + " 条，屏幕 " + Screen.width + "x" + Screen.height + "）");
            }

            var s = Plugin.Settings;
            if (s == null || !s.Enabled) return;

            EnsureStyle(s.FontSize, s.TextColor);

            // 字体预热（只做一次）：第 1 帧 OnGUI 通常发生在主菜单，此刻没有提示要画，
            // 正好把「中文字形栅格化」这笔开销提前花掉，避免第一次弹提示时卡一下。
            if (!_fontWarmed)
            {
                _fontWarmed = true;
                try { WarmUpFont(s); }
                catch (Exception ex)
                {
                    if (Plugin.Log != null) Plugin.Log.LogWarning("字体预热失败：" + ex.Message);
                }
            }

            DrawNotifications(s);
            if (s.ShowPanel) DrawPanel(s);
            if (s.ShowTargets) DrawTargetPanel(s);
            TargetPickerUi.Draw(s);   // F9 管理面板（未打开时内部直接返回）
        }
        catch (Exception ex)
        {
            // 渲染异常绝不允许影响游戏；只记前 3 次
            if (_guiErrors++ < 3 && Plugin.Log != null)
                Plugin.Log.LogWarning("HUD 渲染异常（第 " + _guiErrors + " 次，已忽略）: " + ex);
        }
    }

    private void DrawNotifications(HudSettings s)
    {
        var now = Time.unscaledTime;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (now >= _entries[i].ExpireAt) _entries.RemoveAt(i);
        }
        if (_entries.Count == 0) return;

        float w = Mathf.Min(560f, Screen.width * 0.42f);
        float x = s.AnchorRight ? Screen.width - w - s.OffsetX : s.OffsetX;
        float y = Screen.height * s.OffsetY;

        // 每条提示最前面的小图（v0.1.7 起，v0.1.8 改为逐条选图）：
        // 拾取提示用「提示条小图标」、目标完成用「完成提示小图标」；找不到文件则该条退回纯文本布局
        float iconH = Mathf.Clamp(s.IconHeight, 8, 160);
        const float padX = 10f;     // 与 _style.padding.left 对齐，图标与文字左缘同一列
        const float gap = 8f;       // 图标与文字之间的间距

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            var icon = ImageStore.Get(e.IconSpec);
            float iconW = 0f;
            if (icon != null)
                iconW = iconH * ((float)icon.width / Mathf.Max(1, icon.height));
            float textW = icon != null ? Mathf.Max(80f, w - padX - iconW - gap) : w;

            var content = new GUIContent(e.Text);
            float textH = _style.CalcHeight(content, textW);
            float rowH = icon != null ? Mathf.Max(textH, iconH + 8f) : textH;
            var rect = new Rect(x, y, w, rowH);

            // 淡入 0.15s / 淡出 0.6s
            float age = now - e.BornAt;
            float remain = e.ExpireAt - now;
            float alpha = Mathf.Clamp01(age / 0.15f) * Mathf.Clamp01(remain / 0.6f);

            DrawBackdrop(rect, s, 0.62f, alpha, s.NoticeBackdrop, s.NoticeBackdropAlpha);

            if (icon != null)
            {
                var iconRect = new Rect(x + padX, y + (rowH - iconH) * 0.5f, iconW, iconH);
                var pc = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);
                GUI.color = pc;
            }

            var textColor = s.TextColor;
            var textRect = icon != null ? new Rect(x + padX + iconW + gap, y, textW, rowH) : rect;
            DrawLabel(textRect, content, new Color(textColor.r, textColor.g, textColor.b, alpha),
                      !string.IsNullOrEmpty(s.NoticeBackdrop));

            y += rowH + 6f;
        }
    }

    /// <summary>
    /// 画底：配了该区域的底图就用它（不透明度 = alpha × fade），
    /// 否则退回纯黑底（黑底透明度由 blackAlpha 逐处指定）。
    /// v0.1.9：改为逐块传入图片与不透明度 —— 提示条 / 本局清单 / 目标清单各一张。
    /// v0.1.10：有底图时**先铺一层黑底再叠图** —— 用户的 Q 版小人底图里浅色人脸会把
    ///          浅蓝文字吃掉（离线模拟验证），垫黑底后文字始终有暗衬，图仍看得见。
    /// </summary>
    private void DrawBackdrop(Rect rect, HudSettings s, float blackAlpha, float fade,
                              string imageSpec, float alpha)
    {
        var prev = GUI.color;
        var tex = ImageStore.Get(imageSpec);

        // 保底暗底：无论有没有底图，文字下先有一层黑（blackAlpha 逐处指定）
        GUI.color = new Color(0f, 0f, 0f, blackAlpha * fade);
        GUI.DrawTexture(rect, Bg());

        if (tex != null)
        {
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha) * fade);
            GUI.DrawTexture(rect, tex, ToScaleMode(s.Fill));
        }
        GUI.color = prev;
    }

    /// <summary>
    /// 画文本：先按四个方向各偏移 1px 描一圈黑边，再画正文。
    /// v0.1.10 起因：清单配了人物底图后，浅色人脸正好落在文字下，浅蓝字几乎读不出来；
    /// 描边后任何底图上都清晰（只在配了底图时才描，纯黑底不需要、省 4 次绘制）。
    /// </summary>
    private void DrawLabel(Rect rect, GUIContent content, Color color, bool outline)
    {
        var oc = _style.normal.textColor;
        if (outline)
        {
            _style.normal.textColor = new Color(0f, 0f, 0f, Mathf.Clamp01(color.a) * 0.9f);
            GUI.Label(new Rect(rect.x - 1f, rect.y, rect.width, rect.height), content, _style);
            GUI.Label(new Rect(rect.x + 1f, rect.y, rect.width, rect.height), content, _style);
            GUI.Label(new Rect(rect.x, rect.y - 1f, rect.width, rect.height), content, _style);
            GUI.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), content, _style);
        }
        _style.normal.textColor = color;
        GUI.Label(rect, content, _style);
        _style.normal.textColor = oc;
    }

    private static ScaleMode ToScaleMode(BackdropFill fill)
    {
        switch (fill)
        {
            case BackdropFill.Stretch: return ScaleMode.StretchToFill;
            case BackdropFill.Fit: return ScaleMode.ScaleToFit;
            default: return ScaleMode.ScaleAndCrop;
        }
    }

    private int _panelVersion = -1;
    private int _panelWidthKey = -1;
    private int _panelMaxKey = -1;
    private GUIContent _panelContent;

    private void DrawPanel(HudSettings s)
    {
        var rows = RaidHintState.PanelRows;
        if (rows == null || rows.Count == 0) return;

        float w = Mathf.Min(460f, Screen.width * 0.34f);
        float x = s.AnchorRight ? Screen.width - w - s.OffsetX : s.OffsetX;
        float y = Screen.height * s.PanelOffsetY;

        // 文本只在清单内容/宽度/条数变化时重建，避免每帧拼字符串产生 GC 垃圾
        int widthKey = Mathf.RoundToInt(w);
        int maxKey = Mathf.Max(1, s.PanelMax);
        if (_panelContent == null || _panelVersion != RaidHintState.PanelVersion
            || _panelWidthKey != widthKey || _panelMaxKey != maxKey)
        {
            int shown = Mathf.Min(rows.Count, maxKey);
            var sb = new StringBuilder();
            sb.Append("本局拾取（有需求）");
            for (int i = rows.Count - shown; i < rows.Count; i++)
            {
                sb.Append('\n').Append(rows[i].Text);
            }
            _panelContent = new GUIContent(sb.ToString());
            _panelVersion = RaidHintState.PanelVersion;
            _panelWidthKey = widthKey;
            _panelMaxKey = maxKey;
        }

        float h = _style.CalcHeight(_panelContent, w);
        var rect = new Rect(x, y, w, h);

        DrawBackdrop(rect, s, 0.5f, 1f, s.PanelBackdrop, s.PanelBackdropAlpha);

        var tc = s.TextColor;
        DrawLabel(rect, _panelContent, new Color(tc.r, tc.g, tc.b, 0.92f),
                  !string.IsNullOrEmpty(s.PanelBackdrop));
    }

    // ── 目标清单（跨局）────────────────────────────────────────────────────

    private int _targetVersion = -1;
    private int _targetPanelVersion = -1;
    private int _targetWidthKey = -1;
    private int _targetKey = -1;
    private GUIContent _targetContent;

    private void DrawTargetPanel(HudSettings s)
    {
        var groups = Plugin.TargetGroups;
        if (groups == null || groups.Count == 0) return;

        float w = Mathf.Min(460f, Screen.width * 0.34f);
        // 目标清单独立选边（默认靠左）：本局清单在右边时，两块不再挤在同一条竖线上
        float x = s.TargetAnchorLeft ? s.OffsetX : Screen.width - w - s.OffsetX;
        float y = Screen.height * s.TargetOffsetY;

        int widthKey = Mathf.RoundToInt(w);
        int key = Mathf.Max(1, s.TargetMaxGroups) * 1000 + Mathf.Max(1, s.TargetLinesPerGroup);
        if (_targetContent == null
            || _targetVersion != TargetList.Version
            || _targetPanelVersion != RaidHintState.PanelVersion   // 本局捡到东西会改变"还差多少"
            || _targetWidthKey != widthKey || _targetKey != key)
        {
            _targetContent = new GUIContent(BuildTargetText(groups, s));
            _targetVersion = TargetList.Version;
            _targetPanelVersion = RaidHintState.PanelVersion;
            _targetWidthKey = widthKey;
            _targetKey = key;
        }

        float h = _style.CalcHeight(_targetContent, w);
        var rect = new Rect(x, y, w, h);

        DrawBackdrop(rect, s, 0.5f, 1f, s.TargetBackdrop, s.PanelBackdropAlpha);

        var tc = s.TextColor;
        DrawLabel(rect, _targetContent, new Color(tc.r, tc.g, tc.b, 0.92f),
                  !string.IsNullOrEmpty(s.TargetBackdrop));
    }

    /// <summary>目标清单文本：未完成的排前面；每项只列未凑齐的物品行。</summary>
    private static string BuildTargetText(List<TargetGroup> groups, HudSettings s)
    {
        var ordered = new List<TargetGroup>(groups);
        ordered.Sort(delegate (TargetGroup a, TargetGroup b)
        {
            if (a.AllDone != b.AllDone) return a.AllDone ? 1 : -1;   // 已完成的沉底
            return 0;
        });

        var sb = new StringBuilder();
        sb.Append("目标清单（").Append(groups.Count).Append(" 项）");

        int maxGroups = Mathf.Max(1, s.TargetMaxGroups);
        int maxLines = Mathf.Max(1, s.TargetLinesPerGroup);
        int shownGroups = 0;

        for (int i = 0; i < ordered.Count && shownGroups < maxGroups; i++)
        {
            var g = ordered[i];
            sb.Append('\n');
            sb.Append(g.AllDone ? "✔ " : "▸ ").Append(g.Title);
            if (g.Entry != null && g.Entry.roundsLeft > 0)
                sb.Append("（剩").Append(g.Entry.roundsLeft).Append("局）");
            shownGroups++;

            if (g.AllDone) continue;

            int shown = 0;
            for (int j = 0; j < g.Lines.Count && shown < maxLines; j++)
            {
                if (g.Lines[j].Done) continue;
                sb.Append('\n').Append(HintText.FormatTargetLine(g.Lines[j]));
                shown++;
            }
            if (shown == 0 && g.Lines.Count > 0)
                sb.Append("\n  ✔ 全部已够");
            else if (g.DoneCount > 0)
                sb.Append("\n  （已够 ").Append(g.DoneCount).Append('/').Append(g.Lines.Count).Append("）");
        }

        if (ordered.Count > shownGroups)
            sb.Append("\n…另有 ").Append(ordered.Count - shownGroups).Append(" 项目标");
        return sb.ToString();
    }
}
