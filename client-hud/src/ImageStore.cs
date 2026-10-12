using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace RaidPickupHint;

/// <summary>
/// 自定义图片的加载与缓存（Unity 侧）。
///
/// 设计要点：
///  ① <see cref="Get"/> 每帧会被调用（HUD 绘制路径），**只查内存缓存、不做任何磁盘 I/O**；
///  ② 换图/首次放图由 <see cref="Poll"/> 每 2 秒检查文件时间戳后热重载 —— 不用重启游戏；
///  ③ 解析失败（文件不存在/格式不对）只告警一次，不刷屏、不抛异常影响渲染。
/// 路径规则见 <see cref="ImagePath"/>（纯逻辑，已由离线自检覆盖）。
/// </summary>
internal static class ImageStore
{
    private class Entry
    {
        public string Spec;          // 配置里写的原文（缓存键）
        public string Path;          // 解析出的绝对路径；null = 没找到
        public DateTime Stamp;
        public Texture2D Tex;
        public bool WarnedMissing;
        public bool WarnedFormat;
    }

    private static readonly Dictionary<string, Entry> _cache =
        new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private static float _nextPoll;
    private const float PollInterval = 2f;   // 换图后最多 2 秒生效

    // 预热状态（v0.1.11）
    private static string[] _warmSpecs;
    private static int _warmIndex;
    private static bool _warmDone;

    /// <summary>
    /// 预热（v0.1.11）：把配置里用到的图逐张加载进缓存，**每次调用最多加载 1 张**。
    ///
    /// 起因（2026-10-12 实机）：第一次按 F10，提示要等几秒才出来。日志实证＝
    /// 「自测提示已弹出」之后才出现「图片已加载：捡起_图标_无台词.png」「…完成_图标.png」——
    /// 说明 <see cref="Get"/> 是在**绘制路径**里首次同步加载的（PNG 解码 + mipmap 生成 + 上传显存），
    /// 一次两张 1MB 级 PNG 挤在同一帧。
    /// 更隐蔽的是「本局清单背景」：那块清单只在有内容时才画，所以**进局后第一次捡到东西**
    /// 才会首次解码那张 1.3MB 的图 —— 玩家会以为没提示。
    /// 现在改为在进局前（插件 Update 的前几帧，游戏还在加载）摊开预热，每帧只做一张。
    /// 用户还没把图放好时不影响：预热只是提前登记，Poll 仍会继续找并热重载。
    /// </summary>
    internal static void WarmUp()
    {
        if (_warmDone) return;

        var s = Plugin.Settings;
        if (s == null) return;                      // 配置还没同步好，下一帧再试
        if (!s.ImagesEnabled) { _warmDone = true; return; }

        if (_warmSpecs == null)
        {
            _warmSpecs = new[] { s.NoticeIcon, s.DoneIcon, s.NoticeBackdrop, s.PanelBackdrop, s.TargetBackdrop };
            _warmIndex = 0;
        }

        while (_warmIndex < _warmSpecs.Length)
        {
            var spec = _warmSpecs[_warmIndex++];
            if (string.IsNullOrEmpty(spec)) continue;
            var key = spec.Trim();
            if (_cache.ContainsKey(key)) continue;  // 已处理过（含"没找到"，交给 Poll 继续重试）
            Get(key);
            return;                                  // 本帧只加载一张
        }

        _warmDone = true;
        _warmSpecs = null;
        if (Plugin.Log != null && _cache.Count > 0)
            Plugin.Log.LogInfo("图片预热完成：" + _cache.Count + " 项已进缓存（首次提示不再等图片解码）");
    }

    private static string ImagesDir
    {
        get
        {
            var s = Plugin.Settings;
            return s != null ? s.ImagesDir : null;
        }
    }

    /// <summary>
    /// 取一张图（每帧可安全调用）。没配置、找不到文件、格式不对都返回 null。
    /// </summary>
    internal static Texture2D Get(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        if (Plugin.Settings != null && !Plugin.Settings.ImagesEnabled) return null;

        var key = spec.Trim();
        Entry e;
        if (_cache.TryGetValue(key, out e)) return e.Tex;   // 失败过的条目 Tex 为 null，直接返回

        e = new Entry { Spec = key };
        _cache[key] = e;
        Load(e, null);
        return e.Tex;
    }

    /// <summary>
    /// 每 2 秒检查一次已登记图片的时间戳：文件被覆盖 → 重载；先前没找到的 → 再找一次。
    /// 不做目录扫描，只在已知的候选路径上做 File 检查，开销极小。
    /// </summary>
    internal static void Poll()
    {
        if (_cache.Count == 0) return;
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollInterval;

        var dir = ImagesDir;
        foreach (var kv in _cache)
        {
            var e = kv.Value;
            try
            {
                if (string.IsNullOrEmpty(e.Path))
                {
                    Load(e, dir);          // 之前没找到：用户可能刚把图放进来
                    continue;
                }
                if (!File.Exists(e.Path))
                {
                    e.Path = null;
                    continue;
                }
                var stamp = File.GetLastWriteTimeUtc(e.Path);
                if (stamp != e.Stamp) Load(e, dir);
            }
            catch
            {
                // 轮询异常绝不冒泡到 HUD 渲染
            }
        }
    }

    /// <summary>配置变更时清空缓存（下次绘制按新配置重新解析加载），并释放已加载的纹理。</summary>
    internal static void Invalidate()
    {
        foreach (var kv in _cache)
        {
            if (kv.Value != null && kv.Value.Tex != null)
                UnityEngine.Object.Destroy(kv.Value.Tex);
        }
        _cache.Clear();
        _warmDone = false;      // 配置变了 → 重新预热（下一帧起逐张加载）
        _warmSpecs = null;
        _warmIndex = 0;
    }

    /// <summary>确保图片目录存在，并写入一份给人看的说明（已存在则不覆盖）。</summary>
    internal static void EnsureDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var readme = Path.Combine(dir, "说明.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme, ImagePath.ReadmeText(dir), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            if (Plugin.Log != null) Plugin.Log.LogWarning("创建图片目录失败：" + ex.Message);
        }
    }

    /// <summary>按 spec 解析并加载；找不到/解不开只告警一次。</summary>
    private static void Load(Entry e, string dir)
    {
        if (dir == null) dir = ImagesDir;

        var path = ImagePath.Resolve(e.Spec, dir);
        if (path == null)
        {
            e.Path = null;
            e.Tex = null;
            if (!e.WarnedMissing && Plugin.Log != null)
            {
                e.WarnedMissing = true;
                Plugin.Log.LogWarning("找不到图片「" + e.Spec + "」（图片目录：" + (dir ?? "(未设置)")
                                      + "）；该项暂不显示，放好图后 2 秒内自动生效");
            }
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            // mipChain=true：1254x1254 这类大图缩到 ~28px 显示时，没有 mipmap 会有明显锯齿
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(tex);
                e.Path = path;
                e.Tex = null;
                if (!e.WarnedFormat && Plugin.Log != null)
                {
                    e.WarnedFormat = true;
                    Plugin.Log.LogWarning("图片格式无法解码：" + path + "（请用 PNG 或 JPG）");
                }
                return;
            }

            tex.wrapMode = TextureWrapMode.Clamp;      // 边缘拉伸而不是平铺
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(true, false);                    // 重建 mipmap，缩小显示才平滑

            if (e.Tex != null) UnityEngine.Object.Destroy(e.Tex);   // 热重载时释放旧纹理
            e.Tex = tex;
            e.Path = path;
            e.Stamp = File.GetLastWriteTimeUtc(path);
            e.WarnedMissing = false;
            e.WarnedFormat = false;

            if (Plugin.Log != null)
            {
                int big = Mathf.Max(tex.width, tex.height);
                Plugin.Log.LogInfo("图片已加载：" + Path.GetFileName(path)
                                   + "（" + tex.width + "x" + tex.height + "）"
                                   + (big > 2048 ? "  ⚠ 偏大：会占较多显存，非必要建议缩到 1024px 以内" : ""));
            }
        }
        catch (Exception ex)
        {
            e.Tex = null;
            if (!e.WarnedFormat && Plugin.Log != null)
            {
                e.WarnedFormat = true;
                Plugin.Log.LogWarning("读取图片失败：" + path + " — " + ex.Message);
            }
        }
    }
}
