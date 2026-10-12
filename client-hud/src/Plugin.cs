using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace RaidPickupHint;

/// <summary>
/// 大肥鱼的hud（局内拾取提示）· 客户端插件。作者：deepseek + keping。
/// 显示名、dll 文件名、插件目录名统一为「大肥鱼的hud」，便于在 BepInEx 配置管理器（F12）里一眼找到。
/// GUID `deepseek.keping.raidpickuphint` 同时决定配置文件名 `deepseek.keping.raidpickuphint.cfg`；
/// 三个数据文件（.cfg / .targets.json / .images）都跟 GUID 同名，**改 GUID 必须同步迁移它们**，
/// 否则 F12 里的既有设置与目标清单会看起来"被重置"。
///
/// 目标：raid 中捡起物品时，HUD 蓝色提示“这个物品被哪个任务 / 哪个藏身处设施需要、还差多少个”。
/// 数据源：quest_hideout_checker.py --export-needs 生成的需求快照（needs_snapshot.json）。
/// 契约：只读。不修改存档、不修改游戏数据，只读取快照并渲染提示。
/// </summary>
[BepInPlugin(Guid, "大肥鱼的hud", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "deepseek.keping.raidpickuphint";
    public const string Version = "0.1.13";

    /// <summary>作者署名（BepInPlugin 特性本身没有作者字段，故单独留一个常量供工具/文档引用）。</summary>
    public const string Author = "deepseek + keping";

    internal static ManualLogSource Log;
    internal static Plugin Instance;
    internal static HudSettings Settings = new HudSettings();
    internal static NeedsSnapshot Snapshot;

    /// <summary>目标清单展开后的当前内容（HUD 直接画这个，避免每帧重算）。</summary>
    internal static List<TargetGroup> TargetGroups = new List<TargetGroup>();

    internal static string SnapshotPath;
    internal static string TargetPath;

    private ConfigEntry<bool> _enabled;
    private ConfigEntry<string> _prefix;
    private ConfigEntry<bool> _wrap;
    private ConfigEntry<string> _color;
    private ConfigEntry<int> _fontSize;
    private ConfigEntry<float> _duration;
    private ConfigEntry<int> _maxEntries;
    private ConfigEntry<bool> _panel;
    private ConfigEntry<int> _panelMax;
    private ConfigEntry<bool> _anchorRight;
    private ConfigEntry<float> _offsetY;
    private ConfigEntry<float> _offsetX;
    private ConfigEntry<float> _panelOffsetY;
    private ConfigEntry<bool> _logPickups;
    private ConfigEntry<KeyCode> _testKey;
    private ConfigEntry<HintRenderMode> _renderMode;

    // 目标清单
    private ConfigEntry<bool> _showTargets;
    private ConfigEntry<int> _targetRounds;
    private ConfigEntry<bool> _notifyTargetDone;
    private ConfigEntry<KeyCode> _targetKey;
    private ConfigEntry<float> _targetOffsetY;
    private ConfigEntry<int> _targetMaxGroups;
    private ConfigEntry<int> _targetLinesPerGroup;
    private ConfigEntry<int> _pickerFontSize;
    private ConfigEntry<bool> _targetAnchorLeft;

    // 图片（自定义外观，v0.1.7；v0.1.9 起三块区域各一张底图）
    private ConfigEntry<bool> _imagesEnabled;
    private ConfigEntry<string> _imagesDir;
    private ConfigEntry<string> _noticeIcon;
    private ConfigEntry<int> _iconHeight;
    private ConfigEntry<string> _doneIcon;
    private ConfigEntry<string> _noticeBackdrop;
    private ConfigEntry<float> _noticeBackdropAlpha;
    private ConfigEntry<string> _panelBackdrop;
    private ConfigEntry<string> _targetBackdrop;
    private ConfigEntry<float> _panelBackdropAlpha;
    private ConfigEntry<BackdropFill> _backdropFill;

    /// <summary>上一次已生效的图片配置签名（v0.1.12）：只有它变了才清图片缓存。</summary>
    private string _imgSig;

    private float _nextSnapshotCheck;
    private bool _hotkeyDisabled;
    private float _nextHudTry;
    private int _hudMounts;

    private static int _targetCacheVersion = -1;
    private static int _targetCachePanelVersion = -1;
    private static NeedsSnapshot _targetCacheSnap;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        BindConfig();
        SyncSettings();       // 初始化一次
        HookConfigEvents();   // 之后只在配置变更时同步（避免每帧字符串解析）

        SnapshotPath = Path.Combine(Path.GetDirectoryName(Info.Location), "needs_snapshot.json");
        ReloadSnapshot(true);

        // 目标清单（跨局"购物清单"）：与 .cfg 放同一目录，方便手工编辑。
        // 注意必须写全限定名 BepInEx.Paths —— Assembly-CSharp.dll 里有一个全局命名空间的
        // `Paths : MonoBehaviour` 类，只写 Paths 会被它抢先解析（CS0117: 未包含 ConfigPath）。
        TargetPath = Path.Combine(BepInEx.Paths.ConfigPath, "deepseek.keping.raidpickuphint.targets.json");
        TargetList.SetPath(TargetPath);
        TargetList.Load(true);
        RefreshTargetGroups(true);

        // 自定义图片目录（默认 BepInEx\config\deepseek.keping.raidpickuphint.images）：自动创建，并写一份给人看的说明
        ImageStore.EnsureDir(Settings.ImagesDir);
        Log.LogInfo("自定义图片目录：" + Settings.ImagesDir
                    + "；提示条小图标=" + Img(Settings.NoticeIcon)
                    + "；完成提示小图标=" + (string.IsNullOrEmpty(Settings.DoneIcon) ? "跟随提示条小图标" : Settings.DoneIcon)
                    + "；提示条背景=" + Img(Settings.NoticeBackdrop) + "(" + Settings.NoticeBackdropAlpha.ToString("0.00") + ")"
                    + "；本局清单背景=" + Img(Settings.PanelBackdrop)
                    + "；目标清单背景=" + Img(Settings.TargetBackdrop)
                    + "（清单不透明度 " + Settings.PanelBackdropAlpha.ToString("0.00") + "，填充=" + Settings.Fill + "）");

        // ⚠ 不要用 new GameObject + DontDestroyOnLoad 挂 HUD。
        // 实机证据（2026-10-12 第二局）：插件 Awake 发生在游戏首个场景加载之前，
        // 这样建出来的独立对象会在游戏加载主场景时被连带销毁 —— 加载时日志是
        // 「HUD 组件挂载成功；Instance=已就绪」，而进主菜单按 F10 却报
        // 「HUD 实例为空，自绘提示被跳过」（Unity 对已销毁对象的 != null 为 false），
        // 于是自绘 IMGUI 一帧都没被回调过。
        // 改为挂到插件自身的 GameObject（BepInEx 的 manager，已验证随游戏全程存活：
        // Plugin.Update 一直在跑，热键才被检测到），并在 Update 里自愈重建。
        EnsureHud();

        PickupWatcher.Install();

        Log.LogInfo("大肥鱼的hud v" + Version + " 已加载；快照: " + SnapshotPath);
    }

    /// <summary>
    /// 懒加载 HUD 组件，挂在插件自身的 GameObject 上。
    /// 该对象属于 BepInEx 的 manager，随游戏全程存活（Plugin.Update 持续在跑即证据）；
    /// 而独立 new GameObject 会在游戏加载主场景时被销毁（2026-10-12 实机已验证）。
    /// </summary>
    private void EnsureHud()
    {
        try
        {
            if (HintHud.Instance != null) return;

            var hud = gameObject.AddComponent<HintHud>();
            _hudMounts++;
            Log.LogInfo("HUD 组件挂载" + (hud != null ? "成功" : "失败")
                        + "；Instance=" + (HintHud.Instance != null ? "已就绪" : "为空")
                        + "；宿主=" + gameObject.name
                        + "；第 " + _hudMounts + " 次");
        }
        catch (Exception ex)
        {
            Log.LogWarning("HUD 组件挂载异常：" + ex.Message);
        }
    }

    private void BindConfig()
    {
        _enabled = Config.Bind("开关", "启用", true, "是否显示局内拾取提示");
        _panel = Config.Bind("开关", "常驻清单", true, "是否在屏幕角落常驻显示本局已捡到的有用物品");
        _logPickups = Config.Bind("开关", "记录拾取日志", true, "把每次拾取与判定写入 BepInEx 日志（排查用）");

        _prefix = Config.Bind("文案", "消息前缀", "大肥鱼提醒您：",
            "提示文本开头的固定前缀（不要在这里写【】，括号由下面一项统一决定）");
        _wrap = Config.Bind("文案", "整条用【】包裹", true,
            "true = 【前缀：正文…】（【】包住整条）；false = 前缀：正文…（完全不带括号）");

        _color = Config.Bind("外观", "文本颜色", "#59B8FF", "提示文本颜色（十六进制，默认蓝色）");
        _fontSize = Config.Bind("外观", "字号", 16, "提示文本字号");
        _duration = Config.Bind("外观", "停留秒数", 7f, "单条提示显示多久后消失");
        _maxEntries = Config.Bind("外观", "同屏最多条数", 4, "同时显示的最大提示条数（超出丢弃最旧的）");
        _anchorRight = Config.Bind("外观", "靠右显示", true, "true = 屏幕右侧；false = 左侧");
        _offsetX = Config.Bind("外观", "水平边距", 24f, "距屏幕左/右边缘的像素距离");
        _offsetY = Config.Bind("外观", "起始高度比例", 0.32f, "提示区起始位置（0=顶部, 1=底部）");
        _panelOffsetY = Config.Bind("外观", "清单高度比例", 0.55f, "常驻清单起始位置（0=顶部, 1=底部）");
        _panelMax = Config.Bind("外观", "清单最多条数", 8, "常驻清单最多显示几行");
        _testKey = Config.Bind("外观", "自测热键", KeyCode.F10,
            "按一下弹出一条自测提示（用于确认 HUD/字体/配色是否正常）；None = 关闭");
        _renderMode = Config.Bind("外观", "显示方式", HintRenderMode.Both,
            "Both = 自绘 HUD + 游戏原生通知（双保险）；ImguiOnly = 只自绘；NativeOnly = 只用游戏原生通知");

        // ── 目标清单（跨局购物清单；与本局拾取清单各自独立开关）──
        _showTargets = Config.Bind("目标清单", "启用目标清单", true,
            "是否常驻显示「目标清单」：你自己设定要盯的任务 / 藏身处设施 / 物品，跨局保留");
        _targetRounds = Config.Bind("目标清单", "目标存活局数", 3,
            "新加入的目标保留几局（每出局一次减 1，归零自动移除）；0 = 永久保留直到手动删");
        _notifyTargetDone = Config.Bind("目标清单", "凑齐时提醒", true,
            "某个目标在本局凑齐时，弹一条「目标完成」提示");
        _targetKey = Config.Bind("目标清单", "管理面板热键", KeyCode.F9,
            "按一下打开/关闭目标清单管理面板（任务 / 藏身处设施 / 物品 三组点选）；None = 关闭");
        _targetOffsetY = Config.Bind("目标清单", "清单高度比例", 0.74f,
            "目标清单的起始位置（0=顶部, 1=底部）；本局清单的位置见 外观→清单高度比例");
        _targetMaxGroups = Config.Bind("目标清单", "最多显示项目", 4, "目标清单最多显示几项目标");
        _targetLinesPerGroup = Config.Bind("目标清单", "每项最多列物品", 3,
            "每项目标最多列几件尚未凑齐的物品（其余折叠为「另 N 项」）");
        _pickerFontSize = Config.Bind("目标清单", "管理面板字号", 14, "F9 管理面板的字号");
        _targetAnchorLeft = Config.Bind("目标清单", "靠左显示", true,
            "true = 目标清单显示在屏幕左侧（推荐：本局清单通常在右边，两块分开就不挤了）；false = 跟随本局清单同侧");

        // ── 自定义图片（v0.1.7）──
        _imagesEnabled = Config.Bind("图片", "启用图片", true,
            "总开关：关掉则一律不显示自定义图片（文字提示不受影响）");
        _imagesDir = Config.Bind("图片", "图片目录", "",
            "留空 = BepInEx\\config\\deepseek.keping.raidpickuphint.images（插件会自动创建，并在里面写一份说明）");
        _noticeIcon = Config.Bind("图片", "提示条小图标", "",
            "每条浮动提示最前面显示的小图（Q版小人 / 头像 / logo）。只写文件名→去图片目录里找；写完整路径也支持；留空 = 不显示");
        _iconHeight = Config.Bind("图片", "小图标高度", 28,
            "小图标的显示高度（像素，8~160）；宽度按原图比例自动算");
        // v0.1.9：底图从"一张通用背景板"拆成三块区域各自指定（旧名「背景板图片」已废，值本来就没配过）
        _noticeBackdrop = Config.Bind("图片", "提示条背景", "",
            "浮动提示条后面的底图（Q版小人 / 花纹等）。只写文件名→去图片目录里找；留空 = 用默认的纯黑半透明底");
        _noticeBackdropAlpha = Config.Bind("图片", "提示条背景不透明度", 0.62f,
            "0~1，仅在配了「提示条背景」时生效；越小越透，文字越清楚");
        _panelBackdrop = Config.Bind("图片", "本局清单背景", "",
            "「本局拾取」清单后面的底图。留空 = 用默认的纯黑半透明底");
        _targetBackdrop = Config.Bind("图片", "目标清单背景", "",
            "「目标清单」后面的底图。留空 = 用默认的纯黑半透明底");
        _panelBackdropAlpha = Config.Bind("图片", "清单背景不透明度", 0.45f,
            "0~1，两块清单共用；清单文字密，默认比提示条更透一些");
        _backdropFill = Config.Bind("图片", "底图填充方式", BackdropFill.Crop,
            "Crop=等比裁切填满（不变形，默认）；Stretch=拉伸填满（会变形）；Fit=等比完整显示（比例不匹配时留边）");
        _doneIcon = Config.Bind("图片", "完成提示小图标", "",
            "「目标完成 ✔」提示专用的小图（与普通拾取提示分开）。留空 = 跟随上面那个「提示条小图标」");
    }

    private void Update()
    {
        // HUD 自愈：对象若被销毁（场景切换等），发现即重建；失败时每秒最多重试一次，不刷屏
        if (HintHud.Instance == null && Time.unscaledTime >= _nextHudTry)
        {
            _nextHudTry = Time.unscaledTime + 1f;
            EnsureHud();
        }

        PickupWatcher.Tick();
        CheckHotkeys();
        ImageStore.Poll();   // 自定义图片热重载（内部限频 2 秒；没配图片时立即返回）
        ImageStore.WarmUp(); // 预热：进局前把用到的图逐张加载好（每帧最多 1 张），首次提示不用等解码

        // 热更新：快照与目标清单文件被外部改写后，30 秒内自动生效
        if (Time.unscaledTime >= _nextSnapshotCheck)
        {
            _nextSnapshotCheck = Time.unscaledTime + 30f;
            ReloadSnapshot(false);
            TargetList.Load(false);
        }

        RefreshTargetGroups(false);
    }

    /// <summary>目标清单展开结果只在"目标内容 / 本局已捡 / 快照"变化时重算（避免每帧遍历快照）。</summary>
    internal static void RefreshTargetGroups(bool force)
    {
        try
        {
            if (!force
                && _targetCacheVersion == TargetList.Version
                && _targetCachePanelVersion == RaidHintState.PanelVersion
                && ReferenceEquals(_targetCacheSnap, Snapshot))
                return;

            _targetCacheVersion = TargetList.Version;
            _targetCachePanelVersion = RaidHintState.PanelVersion;
            _targetCacheSnap = Snapshot;
            TargetGroups = TargetList.ResolveAll(Snapshot);
        }
        catch (Exception ex)
        {
            if (Log != null) Log.LogWarning("目标清单展开失败：" + ex.Message);
        }
    }

    /// <summary>配置只在变更时同步——每帧解析颜色字符串会产生 GC 垃圾（塔科夫对每帧分配敏感）。</summary>
    private void HookConfigEvents()
    {
        Hook(_enabled); Hook(_panel); Hook(_logPickups);
        Hook(_prefix); Hook(_wrap); Hook(_color);
        Hook(_fontSize); Hook(_duration); Hook(_maxEntries);
        Hook(_anchorRight); Hook(_offsetX); Hook(_offsetY);
        Hook(_panelOffsetY); Hook(_panelMax); Hook(_testKey); Hook(_renderMode);
        Hook(_showTargets); Hook(_targetRounds); Hook(_notifyTargetDone); Hook(_targetKey);
        Hook(_targetOffsetY); Hook(_targetMaxGroups); Hook(_targetLinesPerGroup); Hook(_pickerFontSize);
        Hook(_targetAnchorLeft);
        Hook(_imagesEnabled); Hook(_imagesDir); Hook(_noticeIcon);
        Hook(_iconHeight); Hook(_doneIcon);
        Hook(_noticeBackdrop); Hook(_noticeBackdropAlpha);
        Hook(_panelBackdrop); Hook(_targetBackdrop);
        Hook(_panelBackdropAlpha); Hook(_backdropFill);
    }

    /// <summary>SettingChanged 事件只存在于 ConfigEntry&lt;T&gt;（ConfigEntryBase 上没有）。</summary>
    private void Hook<T>(ConfigEntry<T> entry)
    {
        entry.SettingChanged += OnSettingChanged;
    }

    private void OnSettingChanged(object sender, EventArgs e)
    {
        try { SyncSettings(); }
        catch (Exception ex) { Log.LogWarning("同步配置失败：" + ex.Message); }
    }

    /// <summary>热键：自测提示（默认 F10）与目标清单管理面板（默认 F9）。</summary>
    private void CheckHotkeys()
    {
        if (_hotkeyDisabled) return;

        bool selfTest = false, picker = false;
        try
        {
            if (_testKey != null && _testKey.Value != KeyCode.None)
                selfTest = Input.GetKeyDown(_testKey.Value);
            if (_targetKey != null && _targetKey.Value != KeyCode.None)
                picker = Input.GetKeyDown(_targetKey.Value);
        }
        catch
        {
            // 若游戏使用新版输入系统，UnityEngine.Input 会抛异常——停用热键，只提示一次
            _hotkeyDisabled = true;
            Log.LogWarning("热键不可用（游戏可能已切换到新输入系统），已停用");
            return;
        }

        if (picker) TargetPickerUi.Toggle();
        if (selfTest) ShowTestHint();
    }

    /// <summary>弹一条自测提示（走与真实拾取完全相同的渲染路径，但不改动本局计数）。</summary>
    internal static void ShowTestHint()
    {
        try
        {
            var s = Settings;
            string text = null;
            var snap = Snapshot;

            if (snap != null && snap.items != null)
            {
                foreach (var kv in snap.items)
                {
                    var it = kv.Value;
                    if (it == null || it.sources == null || it.sources.Count == 0) continue;
                    text = HintText.FormatPickup(kv.Key, it, s, "（自测）");
                    break;
                }
            }
            if (text == null)
                text = HintText.Decorate("（自测）需求快照为空或未加载", s);

            Dispatch(text, s);
            Log.LogInfo("自测提示已弹出：" + OneLine(text));

            // 若单独配了「完成提示小图标」，再弹一条「目标完成」样式——
            // 这样不进局、不捡东西也能一眼验证两张图是否都配对
            if (!string.IsNullOrEmpty(s.DoneIcon))
            {
                var doneText = HintText.Decorate("目标完成 ✔（自测）", s);
                Dispatch(doneText, s, s.DoneIcon);
                Log.LogInfo("自测（完成样式）已弹出：" + OneLine(doneText));
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("自测提示失败：" + ex.Message);
        }
    }

    private void SyncSettings()
    {
        var s = Settings;
        s.Enabled = _enabled.Value;
        s.ShowPanel = _panel.Value;
        s.Prefix = HintText.NormalizePrefix(_prefix.Value);   // 前缀里混进的【】在此剥掉
        s.WrapInBrackets = _wrap.Value;
        s.FontSize = Mathf.Clamp(_fontSize.Value, 8, 48);
        s.Duration = Mathf.Max(1f, _duration.Value);
        s.MaxEntries = Mathf.Max(1, _maxEntries.Value);
        s.AnchorRight = _anchorRight.Value;
        s.OffsetX = Mathf.Max(0f, _offsetX.Value);
        s.OffsetY = Mathf.Clamp01(_offsetY.Value);
        s.PanelOffsetY = Mathf.Clamp01(_panelOffsetY.Value);
        s.PanelMax = Mathf.Max(1, _panelMax.Value);
        s.RenderMode = _renderMode.Value;

        s.ShowTargets = _showTargets.Value;
        s.TargetRounds = Mathf.Max(0, _targetRounds.Value);
        s.NotifyTargetDone = _notifyTargetDone.Value;
        s.TargetOffsetY = Mathf.Clamp01(_targetOffsetY.Value);
        s.TargetMaxGroups = Mathf.Max(1, _targetMaxGroups.Value);
        s.TargetLinesPerGroup = Mathf.Max(1, _targetLinesPerGroup.Value);
        s.PickerFontSize = Mathf.Clamp(_pickerFontSize.Value, 10, 28);
        s.TargetAnchorLeft = _targetAnchorLeft.Value;

        s.ImagesEnabled = _imagesEnabled.Value;
        s.ImagesDir = ResolveImagesDir(_imagesDir.Value);
        s.NoticeIcon = Trimmed(_noticeIcon.Value);
        s.IconHeight = Mathf.Clamp(_iconHeight.Value, 8, 160);
        s.DoneIcon = Trimmed(_doneIcon.Value);
        s.NoticeBackdrop = Trimmed(_noticeBackdrop.Value);
        s.NoticeBackdropAlpha = Mathf.Clamp01(_noticeBackdropAlpha.Value);
        s.PanelBackdrop = Trimmed(_panelBackdrop.Value);
        s.TargetBackdrop = Trimmed(_targetBackdrop.Value);
        s.PanelBackdropAlpha = Mathf.Clamp01(_panelBackdropAlpha.Value);
        s.Fill = _backdropFill.Value;
        // 图片相关配置**真的**变了才清缓存（v0.1.12）：F12 里拖滑块（不透明度等）会连续触发
        // SettingChanged，而无条件 Invalidate 会把 4 张大图反复销毁 + 重新解码，设置界面直接卡住。
        var imgSig = ImageSignature(s);
        if (imgSig != _imgSig)
        {
            _imgSig = imgSig;
            ImageStore.Invalidate();   // 路径或开关变了：下次绘制按新配置重新解析并加载
        }

        Color parsed;
        if (TryParseColor(_color.Value, out parsed)) s.TextColor = parsed;

        RefreshTargetGroups(true);   // 显示参数变了，让 HUD 重建文本
    }

    /// <summary>
    /// 图片配置签名（v0.1.12）：只含「决定加载哪张图」的字段，
    /// **不含**字号/高度/不透明度/填充方式这类纯绘制参数——它们改了不需要重新解码。
    /// </summary>
    private static string ImageSignature(HudSettings s)
    {
        return (s.ImagesEnabled ? "1" : "0")
             + "\u0001" + (s.ImagesDir ?? "")
             + "\u0001" + (s.NoticeIcon ?? "")
             + "\u0001" + (s.DoneIcon ?? "")
             + "\u0001" + (s.NoticeBackdrop ?? "")
             + "\u0001" + (s.PanelBackdrop ?? "")
             + "\u0001" + (s.TargetBackdrop ?? "");
    }

    private static string Img(string v)
    {
        return string.IsNullOrEmpty(v) ? "未设置" : v;
    }

    private static string Trimmed(string v)
    {
        return v == null ? "" : v.Trim();
    }

    /// <summary>把多行提示压成单行再写日志：否则日志里只剩第一行，核对时看不到正文。</summary>
    private static string OneLine(string text)
    {
        return string.IsNullOrEmpty(text) ? "" : text.Replace("\r", "").Replace("\n", " ｜ ");
    }

    /// <summary>图片目录：F12 留空时用 config 下的默认目录（与 targets.json 放同一处，好找）。</summary>
    private static string ResolveImagesDir(string configured)
    {
        var d = (configured ?? "").Trim().Trim('"');
        if (d.Length == 0)
            d = Path.Combine(BepInEx.Paths.ConfigPath, "deepseek.keping.raidpickuphint.images");  // 必须全限定名，原因见 Awake 注释
        try { return Path.GetFullPath(d); }
        catch { return d; }
    }

    internal static bool TryParseColor(string hex, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrEmpty(hex)) return false;
        var t = hex.Trim();
        if (!t.StartsWith("#")) t = "#" + t;
        return ColorUtility.TryParseHtmlString(t, out color);
    }

    /// <summary>加载/热重载需求快照（force=true 时忽略时间戳检查）。</summary>
    internal static void ReloadSnapshot(bool force)
    {
        try
        {
            if (!force && Snapshot != null && File.Exists(SnapshotPath))
            {
                if (File.GetLastWriteTimeUtc(SnapshotPath) <= Snapshot.fileWriteUtc) return;
            }

            string err;
            var snap = NeedsSnapshot.TryLoad(SnapshotPath, out err);
            if (snap == null)
            {
                if (force) Log.LogWarning("需求快照加载失败：" + err);
                return;
            }

            Snapshot = snap;
            Log.LogInfo(string.Format("需求快照已加载：物品 {0} 种 / 需求来源 {1} 条（生成于 {2}）",
                snap.stats != null ? snap.stats.itemCount : snap.items.Count,
                snap.stats != null ? snap.stats.sourceCount : 0,
                snap.generatedAt));
        }
        catch (Exception ex)
        {
            Log.LogWarning("需求快照加载异常：" + ex.Message);
        }
    }

    private static int _hudMissingWarned;

    /// <summary>
    /// 把一条提示投递到启用的载体上：
    ///   自绘 HUD（IMGUI，可多行、带常驻清单）与游戏原生通知（NotificationManager）。
    /// 2026-10-12 实机首测时自绘路径在屏幕上毫无输出，故默认两条都发（双保险）。
    /// </summary>
    internal static void Dispatch(string text, HudSettings s, string iconSpec = null)
    {
        if (string.IsNullOrEmpty(text) || s == null) return;

        if (s.RenderMode != HintRenderMode.NativeOnly)
        {
            var hud = HintHud.Instance;
            if (hud != null)
            {
                // 没单独指定就跟随「提示条小图标」；两者都留空 = 这条不画图
                var icon = string.IsNullOrEmpty(iconSpec) ? s.NoticeIcon : iconSpec;
                hud.Push(text, s.Duration, icon);
            }
            else if (_hudMissingWarned++ < 3 && Log != null)
                Log.LogWarning("HUD 实例为空，自绘提示被跳过（原生通知不受影响）");
        }

        if (s.RenderMode != HintRenderMode.ImguiOnly)
            NativeNotice.Show(text, s.TextColor);
    }

    /// <summary>拾取回调：命中需求则弹提示并更新常驻清单。</summary>
    internal static void OnItemPicked(string tpl, int count)
    {
        try
        {
            if (string.IsNullOrEmpty(tpl)) return;

            var snap = Snapshot;
            if (snap == null || snap.items == null) return;

            SnapshotItem item;
            if (!snap.items.TryGetValue(tpl, out item) || item == null) return;
            if (item.sources == null || item.sources.Count == 0) return;

            RaidHintState.AddPickup(tpl, count);

            if (Log != null && Instance != null && Instance._logPickups != null && Instance._logPickups.Value)
                Log.LogInfo("拾取命中：" + tpl + " x" + count + " (" + item.name + ")");

            var text = HintText.FormatPickup(tpl, item, Settings);
            Dispatch(text, Settings);

            RaidHintState.UpsertPanel(tpl, HintText.FormatPanelLine(tpl, item));

            CheckTargetCompletion();   // 目标清单：本局刚凑齐的目标弹一次提醒
        }
        catch (Exception ex)
        {
            if (Log != null) Log.LogWarning("拾取提示生成异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 检查目标清单里是否有目标"本局刚凑齐"（每个目标从加入到达成只提醒一次）。
    /// 注意：判定用的是"仓库持有 + 本局已捡"的口径（与提示一致）。
    /// </summary>
    private static void CheckTargetCompletion()
    {
        try
        {
            if (Instance != null && Instance._notifyTargetDone != null && !Instance._notifyTargetDone.Value) return;

            var snap = Snapshot;
            if (snap == null) return;

            var freshly = TargetList.TakeNewlyCompleted(snap);
            if (freshly == null || freshly.Count == 0) return;

            for (int i = 0; i < freshly.Count; i++)
            {
                var g = TargetList.Resolve(snap, freshly[i]);
                var text = HintText.FormatTargetDone(g, Settings);
                Dispatch(text, Settings, Settings.DoneIcon);   // 完成提示用专用小图（留空则跟随通用图）
                if (Log != null) Log.LogInfo("目标完成提醒：" + OneLine(text));
            }

            RefreshTargetGroups(true);
        }
        catch (Exception ex)
        {
            if (Log != null) Log.LogWarning("目标完成检查异常：" + ex.Message);
        }
    }
}
