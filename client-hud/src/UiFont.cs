using UnityEngine;

namespace RaidPickupHint;

/// <summary>
/// IMGUI 中文字体（Unity 默认字体不含中文字形，直接画会是一排方块）。
/// HUD 与目标管理面板共用同一份，避免重复创建字体对象。
/// </summary>
internal static class UiFont
{
    private static Font _font;
    private static int _size = -1;

    internal static Font Get(int size)
    {
        if (_font != null && _size == size) return _font;

        // 依次尝试系统里的中文字体
        string[] candidates = {
            "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑",
            "SimHei", "黑体", "SimSun", "宋体", "Noto Sans CJK SC", "Arial Unicode MS",
        };
        _font = Font.CreateDynamicFontFromOSFont(candidates, size);
        _size = size;

        if (_font == null && Plugin.Log != null)
            Plugin.Log.LogWarning("未能创建系统中文字体，HUD 中文可能显示为方块");
        return _font;
    }
}
