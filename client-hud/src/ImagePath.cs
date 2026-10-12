using System;
using System.IO;
using System.Text;

namespace RaidPickupHint;

/// <summary>
/// 图片路径解析（纯逻辑，无 Unity 依赖 —— 便于离线自检覆盖命名规则）。
///
/// 配置里可以写两种形式，本类负责统一解析成绝对路径：
///   ① 只写文件名（如 `小人.png` 或省略扩展名的 `小人`）→ 去「图片目录」里找
///   ② 写完整/相对路径（含 \ 或 / 或盘符）→ 直接当路径用
/// 解析不到返回 null（调用方据此跳过绘制，不报错刷屏）。
/// </summary>
internal static class ImagePath
{
    /// <summary>没写扩展名时依次尝试的格式。只列 Unity LoadImage 可靠支持的：PNG（可透明）/ JPG。</summary>
    internal static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

    /// <summary>把配置里的写法解析成可读取的绝对路径；找不到返回 null。</summary>
    internal static string Resolve(string spec, string imagesDir)
    {
        if (string.IsNullOrEmpty(spec)) return null;

        var s = spec.Trim().Trim('"');
        if (s.Length == 0) return null;

        // 判断"这是路径"还是"这是文件名"：出现目录分隔符，或形如 C: 的盘符
        bool looksLikePath = s.IndexOf('\\') >= 0 || s.IndexOf('/') >= 0
                             || (s.Length > 1 && s[1] == ':');

        if (looksLikePath)
        {
            if (File.Exists(s)) return s;
            return TryAppendExtension(s);
        }

        if (string.IsNullOrEmpty(imagesDir)) return null;

        var inDir = Path.Combine(imagesDir, s);
        if (File.Exists(inDir)) return inDir;
        return TryAppendExtension(inDir);
    }

    /// <summary>已带图片扩展名就返回 null（不再拼接）；否则依次试 Extensions。</summary>
    private static string TryAppendExtension(string basePath)
    {
        if (HasImageExtension(basePath)) return null;
        for (int i = 0; i < Extensions.Length; i++)
        {
            var p = basePath + Extensions[i];
            if (File.Exists(p)) return p;
        }
        return null;
    }

    internal static bool HasImageExtension(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        for (int i = 0; i < Extensions.Length; i++)
        {
            if (string.Equals(ext, Extensions[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>图片目录里那份给人看的说明（插件只在文件不存在时写入，不会覆盖你的改动）。</summary>
    internal static string ReadmeText(string imagesDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("大肥鱼的hud · 自定义图片目录");
        sb.AppendLine("========================================");
        sb.AppendLine();
        sb.AppendLine("这个目录是给你放自己的图片用的。把图丢进来，然后在游戏里按 F12");
        sb.AppendLine("找到「大肥鱼的hud」→「图片」分节，把文件名填进去即可，不用重启游戏");
        sb.AppendLine("（最多 2 秒自动生效）。");
        sb.AppendLine();
        sb.AppendLine("【怎么填】");
        sb.AppendLine("  提示条小图标 = 小人.png        ← 只写文件名，就在这里找");
        sb.AppendLine("  提示条小图标 = D:\\图\\小人.png   ← 写完整路径也行，两者都支持");
        sb.AppendLine("  提示条小图标 = 小人             ← 省略扩展名也能认（试 .png/.jpg/.jpeg）");
        sb.AppendLine("  留空 = 不显示这张图。");
        sb.AppendLine();
        sb.AppendLine("【提示条小图标】");
        sb.AppendLine("  每条浮动提示（右边冒出来的蓝色文字）最前面会显示这张图，");
        sb.AppendLine("  适合放一个 Q 版小人、头像、logo 之类。");
        sb.AppendLine("  建议：正方形、PNG 带透明背景、高度 64~256 像素。");
        sb.AppendLine("  实际显示高度由 F12 的「小图标高度」控制（默认 28），宽度按原图比例自动算。");
        sb.AppendLine();
        sb.AppendLine("【完成提示小图标】");
        sb.AppendLine("  「目标完成 ✔」那条提示专用的小图（与普通拾取提示分开）。");
        sb.AppendLine("  留空 = 跟随上面的「提示条小图标」。");
        sb.AppendLine();
        sb.AppendLine("【三块区域的底图（v0.1.9）】");
        sb.AppendLine("  提示条背景      = 浮动提示条后面的底图");
        sb.AppendLine("  本局清单背景    = 右边「本局拾取」清单后面的底图");
        sb.AppendLine("  目标清单背景    = 左边「目标清单」后面的底图");
        sb.AppendLine("  三块各填一张，留空的那块就用默认的纯黑半透明底。");
        sb.AppendLine("  不透明度分开调：「提示条背景不透明度」默认 0.62，「清单背景不透明度」默认 0.45");
        sb.AppendLine("  （图越花越该调低，保证文字压得住）。");
        sb.AppendLine("  填充方式：「底图填充方式」= Crop 等比裁切（默认，不变形）/ Stretch 拉伸填满 / Fit 完整显示。");
        sb.AppendLine("  注：底图不需要抠成透明，普通 PNG/JPG 即可 —— 透明度由上面的不透明度选项控制。");
        sb.AppendLine();
        sb.AppendLine("【格式】");
        sb.AppendLine("  PNG（推荐，支持透明）或 JPG。GIF/BMP 等其他格式 Unity 解不出来，会记一条告警。");
        sb.AppendLine();
        sb.AppendLine("【换图】");
        sb.AppendLine("  直接覆盖同名文件即可，游戏内 2 秒内自动换成新图，不用重启。");
        sb.AppendLine();
        sb.AppendLine("目录位置：" + (string.IsNullOrEmpty(imagesDir) ? "(未设置)" : imagesDir));
        return sb.ToString();
    }
}
