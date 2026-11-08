using System.Collections.Generic;

namespace TaoLive.Model;

/// <summary>水墨主题三象（与 Tao 系一致）：道映默认水墨。</summary>
public static class TaoPalette
{
    public static readonly (string Name, string Bg, string Ink, string Accent)[] Themes =
    {
        ("水墨", "#F7F5EE", "#52708A", "#8A5C3D"),
        ("天灰", "#A8B2BC", "#0F4C9E", "#52708A"),
        ("墨夜", "#131211", "#6FA3BC", "#8A5C3D"),
    };

    public static (string Bg, string Ink, string Accent) Current { get; private set; } = Themes[0].ToTuple2();

    public static void Select(int index) => Current = Themes[index].ToTuple2();

    private static (string, string, string) ToTuple2(this (string Name, string Bg, string Ink, string Accent) t)
        => (t.Bg, t.Ink, t.Accent);
}

/// <summary>应用设置。</summary>
public class AppSettings
{
    public bool AutoStartOnBoot { get; set; } = false;   // 开机自启：默认关，真机实测定
    public int ShortcutKeySlotA { get; set; } = -1;      // A 档快捷键绑定的频道号（-1 未绑）
    public int ShortcutKeySlotB { get; set; } = -1;
    public int ShortcutKeySlotC { get; set; } = -1;
    public int ShortcutKeySlotD { get; set; } = -1;
    public bool StartOnFavoriteFirst { get; set; } = false; // B 档：启动直达收藏首位
    public string AiEnhanceTier { get; set; } = "auto"; // AI 画质档位：v1.0.10 统一 auto(自动,默认)/low/high/off（旧版 smart 兼容按 auto）
    public string LastChannel { get; set; } = "";        // 上次频道（开机即看）
    public bool UserSourcesEnabled { get; set; } = true;
    public string DecodeMode { get; set; } = "auto";     // 解码方式（v1.0.8 第18项）：auto/force_hw/force_sw
    public bool HwKnownBad { get; set; } = false;        // 硬解坏档标记（自动回退过即记档，auto 直接软解）
    public bool PrecheckEnabled { get; set; } = true;    // 循环预检开关
}
