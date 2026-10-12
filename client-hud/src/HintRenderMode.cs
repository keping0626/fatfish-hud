namespace RaidPickupHint;

/// <summary>
/// 提示的显示载体（纯枚举，无任何依赖，离线自检也可链接）。
///
/// 为什么要两个载体：2026-10-12 实机首测发现——拾取链路与文案生成全部正常
/// （日志有"拾取命中"，Player.log 无本插件任何异常），但屏幕上什么都看不到。
/// 说明问题只可能在"自绘 IMGUI 是否真的被 Unity 回调/绘制"这一层。
/// 故增加游戏原生通知（NotificationManager，与"任务完成"同一套 UI）作为兜底，
/// 并为自绘路径补上诊断日志（Awake / 首帧 OnGUI / 渲染异常）。
/// </summary>
public enum HintRenderMode
{
    /// <summary>自绘 HUD + 游戏原生通知（默认，双保险）。</summary>
    Both = 0,

    /// <summary>只自绘 HUD（IMGUI）。</summary>
    ImguiOnly = 1,

    /// <summary>只用游戏原生通知。</summary>
    NativeOnly = 2,
}
