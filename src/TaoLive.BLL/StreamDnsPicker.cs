using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.BLL;

/// <summary>
/// v28 主线④网络面·DNS 择优（移植自 NetOpt 口径，精简为取流用）：
/// 系统 DNS 与两个 DoH（阿里 223.5.5.5 / CF 1.1.1.1）并发解析，合并去重只取 A 记录（IPv4），
/// 结果缓存 10 分钟。设计底线：DoH 任何异常静默回落系统 DNS，绝不让优化挂掉影响取流；
/// IP 择路=把可达 IP 排前（连接失败者降权），供 HttpClient 连接复用前快速锁定可用地址。
/// 纯逻辑可测：解析委托与时钟注入。
/// </summary>
public class StreamDnsPicker
{
    private const int CacheTtlMs = 10 * 60 * 1000;
    /// <summary>v46 修（B2）：最后一路独苗解析的观察上限——DoH 各自带 3s 超时，系统 DNS 无超时
    /// （OS DNS 栈自超时常态 5–15s，Broken 假活场景可挂数十秒）；只剩系统解析时给 10s 上限，超时按全挂兜底。</summary>
    private const int LastLegTimeoutMs = 10_000;

    private readonly Func<string, Task<IPAddress[]>> _systemResolve;
    private readonly Func<string, string, Task<IPAddress[]>> _dohResolve; // (server, host)
    private readonly Func<long> _nowMs;

    private readonly ConcurrentDictionary<string, (IPAddress[] Ips, long AtMs)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Fails, long UntilMs)> _penalty = new(StringComparer.Ordinal); // IP 择路降权账

    private static readonly HttpClient DohHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    public StreamDnsPicker(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _systemResolve = host => Dns.GetHostAddressesAsync(host);
        _dohResolve = DohResolveAsync;
    }

    /// <summary>测试注入：自定义系统解析与 DoH 委托。</summary>
    public StreamDnsPicker(Func<string, Task<IPAddress[]>> systemResolve, Func<string, string, Task<IPAddress[]>> dohResolve, Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _systemResolve = systemResolve;
        _dohResolve = dohResolve;
    }

    /// <summary>解析主机名：缓存命中直接返回；未命中系统 DNS 与双 DoH 并发，合并去重只取 IPv4。
    /// 全部失败抛给调用方（与系统 Dns 行为一致，由取流链路兜底）。</summary>
    public async Task<IPAddress[]> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal }; // IP 字面量直接返回
        if (_cache.TryGetValue(host, out var hit) && _nowMs() - hit.AtMs <= CacheTtlMs)
            return OrderByPenalty(hit.Ips);

        var systemTask = SafeCall(() => _systemResolve(host), "系统DNS");
        var dohA = SafeCall(() => _dohResolve("223.5.5.5", host), "DoH阿里");
        var dohB = SafeCall(() => _dohResolve("1.1.1.1", host), "DoH CF");
        // ③热修 B4：三路改 WhenAny——任一解析器出 IP 即返回。系统解析无超时，SystemDnsBroken 下
        // DoH 3 秒出结果被白等（每 host 每 10 分钟窗口首连付一次系统超时）；其余路在后台继续补账进缓存
        var all = new[] { systemTask, dohA, dohB };
        var pending = new List<Task<IPAddress[]>>(all);
        while (pending.Count > 0)
        {
            // v46 修（B2）：WhenAny 后若 DoH 均空、pending 只剩系统解析，旧写法无限挂等——
            // 独苗路加 10s 上限观察，超时按 DNS 全挂走 SocketException 兜底
            var wait = pending.Count == 1
                ? Task.WhenAny(pending).WaitAsync(TimeSpan.FromMilliseconds(LastLegTimeoutMs))
                : Task.WhenAny(pending);
            Task<IPAddress[]> done;
            try { done = await wait; }
            catch (TimeoutException)
            {
                throw new System.Net.Sockets.SocketException(); // 最后一根独苗超 10s：与全挂同型交上层兜底
            }
            await done;
            var partial = all.Where(t => t.IsCompletedSuccessfully)
                .SelectMany(t => t.Result)
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Distinct().ToArray();
            if (partial.Length > 0)
            {
                _cache[host] = (partial, _nowMs());
                MergeLateResults(host, all); // 其余路完成后合并补账（后台不等待）
                return OrderByPenalty(partial);
            }
            pending.RemoveAll(t => t.IsCompleted); // 先出的路为空（如系统解析抛错）：不再重复等它
        }

        throw new System.Net.Sockets.SocketException(); // 全挂：与系统 DNS 失败同型，交上层兜底
    }

    /// <summary>③热修 B4 配套：早返回后剩余解析器后台完成后合并结果补账进缓存（取全部成功的路）。</summary>
    private async void MergeLateResults(string host, Task<IPAddress[]>[] all)
    {
        try
        {
            await Task.WhenAll(all);
            var merged = all.Where(t => t.IsCompletedSuccessfully)
                .SelectMany(t => t.Result)
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Distinct().ToArray();
            if (merged.Length > 0) _cache[host] = (merged, _nowMs());
        }
        catch { } // 补账失败不影响已返回的主路
    }

    /// <summary>DoH 查询（RFC8484 JSON）：只取 Answer 里 type=1 的 IPv4。失败返回空（不抛）。</summary>
    private async Task<IPAddress[]> DohResolveAsync(string server, string host)
    {
        try
        {
            var json = await DohHttp.GetStringAsync($"https://{server}/resolve?name={Uri.EscapeDataString(host)}&type=A");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return Array.Empty<IPAddress>();
            return answers.EnumerateArray()
                .Where(a => a.TryGetProperty("type", out var t) && t.GetInt32() == 1)
                .Select(a => a.GetProperty("data").GetString())
                .Where(s => s != null && IPAddress.TryParse(s, out _))
                .Select(s => IPAddress.Parse(s!))
                .ToArray();
        }
        catch { return Array.Empty<IPAddress>(); } // DoH 失败回落系统 DNS（底线口径）
    }

    private static async Task<IPAddress[]> SafeCall(Func<Task<IPAddress[]>> call, string tag)
    {
        try { return await call(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DNS择优] {tag} 失败回落：{ex.Message}"); return Array.Empty<IPAddress>(); } // BLL 层无 UI 日志器，兜底静默（诊断走 NetOpt 同款 Trace 事件可后挂）
    }

    /// <summary>IP 择路：失败过（仍在降权期）的 IP 排后面，其余保持原序（稳定排序）。</summary>
    private IPAddress[] OrderByPenalty(IPAddress[] ips)
    {
        var now = _nowMs();
        return ips.OrderBy(ip => _penalty.TryGetValue(ip.ToString(), out var p) && now < p.UntilMs ? 1 : 0)
            .ToArray();
    }

    /// <summary>连接失败记账：该 IP 降权 5 分钟（择路让可用地址排前，不拉黑）。</summary>
    public void ReportFail(IPAddress ip)
        => _penalty[ip.ToString()] = (0, _nowMs() + 5 * 60 * 1000);

    /// <summary>连接成功清账（恢复原序参与）。</summary>
    public void ReportOk(IPAddress ip) => _penalty.TryRemove(ip.ToString(), out _);
}
