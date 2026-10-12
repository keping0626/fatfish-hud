using System;
using EFT.Communications;
using UnityEngine;

namespace RaidPickupHint;

/// <summary>
/// 原生通知兜底：走游戏自带的 <see cref="NotificationManager"/>，
/// 和"任务完成 / 收到邮件"是同一套 UI 系统，必定能被玩家看到。
///
/// 用途（2026-10-12 实机首测后新增）：自绘 IMGUI 在实机上完全没有输出，
/// 而拾取与文案链路已验证正常，故加这条不依赖 IMGUI 的通路；
/// 同时也是"蓝色文字"的原生支持点——DisplayMessageNotification 接受 textColor。
/// </summary>
internal static class NativeNotice
{
    private static int _failures;

    /// <summary>弹一条游戏原生通知。失败只记日志（最多记 3 次），绝不影响游戏。</summary>
    internal static bool Show(string text, Color color)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            NotificationManager.DisplayMessageNotification(
                text,
                ENotificationDurationType.Long,   // 停留久一点，便于观察
                ENotificationIconType.Note,
                color);
            return true;
        }
        catch (Exception ex)
        {
            if (_failures++ < 3 && Plugin.Log != null)
                Plugin.Log.LogWarning("原生通知发送失败（第 " + _failures + " 次）: " + ex.Message);
            return false;
        }
    }
}
