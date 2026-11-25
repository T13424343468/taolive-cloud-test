using System.Text.Json;

namespace TaoLive.Session;

/// <summary>重放步骤（v60 重放格式）：时间戳 + 命令 ID + 可选参数，JSONL 逐行。</summary>
public sealed record ReplayStep(long T, string Cmd, string? Arg);

/// <summary>
/// 命令流重放（方案书 v60/v97，R1 红条收口）：遥控器按键与注入事件统一成命令流进会话，
/// 好测好重放；按键 ID 表固化随包；重放=回归测试固定资产。
/// </summary>
public static class CommandReplay
{
    /// <summary>按键 ID 表（v60，固化随包）：物理键名 → 命令 ID。新键入表须同步包内核对表。</summary>
    public static readonly IReadOnlyDictionary<string, string> KeyIds = new Dictionary<string, string>
    {
        ["KEY_OK"] = "ok",
        ["KEY_UP"] = "up",
        ["KEY_DOWN"] = "down",
        ["KEY_LEFT"] = "left",
        ["KEY_RIGHT"] = "right",
        ["KEY_MENU"] = "menu",
        ["KEY_BACK"] = "back",
        ["KEY_DIGIT"] = "digit", // arg=数字 0-9
    };

    /// <summary>
    /// 解析重放流（JSONL 逐行）：按 t 升序排列；坏行抛 FormatException（重放流=固定资产，坏了不静默）。
    /// </summary>
    public static List<ReplayStep> Parse(string jsonl)
    {
        var steps = new List<ReplayStep>();
        var lines = (jsonl ?? string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            ReplayStep? step;
            try
            {
                step = JsonSerializer.Deserialize<ReplayStep?>(line, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch (JsonException ex)
            {
                throw new FormatException($"重放流第 {i + 1} 行不可解析：{ex.Message}");
            }
            if (step is null || step.T < 0 || string.IsNullOrEmpty(step.Cmd))
                throw new FormatException($"重放流第 {i + 1} 行字段不合法（t≥0、cmd 必填）");
            steps.Add(step);
        }
        return steps.OrderBy(s => s.T).ToList();
    }
}
