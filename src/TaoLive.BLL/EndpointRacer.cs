using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TaoLive.BLL;

/// <summary>
/// A 类第13项·多端点竞速（总装口径二/三）：同一频道多来源变体并行轻探（4 秒统一预算），
/// 第一个探通者胜出；退避期内的源直接跳过不发起（绝不绕闸）；结果回写健康账让预热少探。
/// 纯逻辑可测：探测委托与时间注入。
/// </summary>
public static class EndpointRacer
{
    /// <summary>竞速探测结论：通 / 被 403 限流 / 其他失败。</summary>
    public enum Outcome { Alive, Limited, Dead }

    public class Candidate
    {
        public Candidate(string sourceId, Func<Task<Outcome>> probe) { SourceId = sourceId; Probe = probe; }
        public string SourceId { get; }
        public Func<Task<Outcome>> Probe { get; }
    }

    /// <summary>
    /// 竞速：跳过退避期源后并行探测，首个 Alive 即胜出并立即返回。
    /// vc53 刀4（画面平稳优先令同批）：输家不再拖胜者——原实现胜出后仍等全部输家收尾（最长拖满 4 秒预算），
    /// 改为胜出即返回，输家废弃自灭（结果丢弃、异常吞掉，不漏未观察异常）。
    /// 返回胜者 SourceId；全部失败或全部退避返回 null。budgetMs=统一 4 秒预算。
    /// </summary>
    public static async Task<string?> RaceAsync(
        IReadOnlyList<Candidate> candidates,
        SourceBackoffLedger backoff,
        long nowMs,
        Func<string, Task>? onLimited = null,
        int budgetMs = 4000)
    {
        // 总装口径三：退避期内的源竞速跳过，直接用缓存/健康账现有结论，绝不绕闸
        var runners = candidates.Where(c => !backoff.IsFrozen(c.SourceId, nowMs)).ToList();
        if (runners.Count == 0) return null;

        var tasks = runners.Select(async c =>
        {
            try
            {
                var win = Task.Run(c.Probe);
                var done = await Task.WhenAny(win, Task.Delay(budgetMs));
                var outcome = done != win ? Outcome.Dead // 4 秒预算不归=按失败计（A④同口径）
                    : await win;
                return (c.SourceId, outcome);
            }
            catch { return (c.SourceId, Outcome.Dead); }
        }).ToList();

        string? winner = null;
        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks);
            tasks.Remove(finished);
            var (sourceId, outcome) = await finished;
            if (outcome == Outcome.Alive && winner is null)
            {
                winner = sourceId; // 先到先得；后面 Alive 不改判
                // 输家废弃自灭：不观结果不阻塞胜者，只接异常（探针内部已兜成 Dead，防御性再吞一层）
                foreach (var loser in tasks)
                    _ = loser.ContinueWith(static fin =>
                    {
                        try { _ = fin.Result; }
                        catch { /* 废弃收尾：胜者已定，输家结果丢弃 */ }
                    }, TaskScheduler.Default);
                return winner;
            }
            if (outcome == Outcome.Limited && onLimited != null)
                await onLimited(sourceId); // 竞速触发 403：写同一本退避账（调用方落账）
        }
        return winner;
    }
}
