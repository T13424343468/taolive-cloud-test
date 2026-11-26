using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.Players;

/// <summary>
/// 慢分片竞速数据源（六项优化⑤，vc85 蓝本 v2.5 全量落码）：媒体分片首字节慢出滑动均值 →
/// 立刻对同源发第二请求赛跑，先到先用；监控发现的每次慢片用竞速消化，不动整路（与整路降档一细一粗）。
/// 竞速只发生在本路未消费任何数据字节前（换缝零偏移，字节对齐）；赛跑是让路角色，
/// 任何失败原样退回慢路继续播，绝不打断在播（四原则）。
/// </summary>
public sealed class RacingDataSource : Java.Lang.Object, AndroidX.Media3.DataSource.IDataSource
{
    /// <summary>慢判倍数：首字节超滑动均值 2 倍即坐实慢片（单倍是抖动，双倍是病灶）。</summary>
    public const int RaceTriggerFactor = 2;

    /// <summary>赛跑触发下限（毫秒）：快链路首字节本就快，不白赛（发请求也有成本）。</summary>
    public const int RaceFloorMs = 1_000;

    /// <summary>等第二请求先到的窗口（毫秒）：有界阻塞，超窗判慢路胜，loader 线程不悬死。</summary>
    public const int RacerWaitMs = 1_500;

    private static readonly object MeanGate = new();
    private static long _meanFirstByteMs; // 首字节滑动均值（EWMA 1/4，进程共享：同进程实例共用一个读数）

    private readonly Func<AndroidX.Media3.DataSource.IDataSource> _innerFactory;
    private readonly List<AndroidX.Media3.DataSource.ITransferListener> _listeners = new();
    private AndroidX.Media3.DataSource.IDataSource? _inner;
    private AndroidX.Media3.DataSource.DataSpec? _spec;
    private long _openAtMs;
    private bool _raced;        // 本分片已判过竞速（一片最多赛一次）
    private byte? _pendingByte; // 赛跑者已取到的首字节（换缝：先吐这字节再走正常读）

    public RacingDataSource(Func<AndroidX.Media3.DataSource.IDataSource> innerFactory)
    {
        _innerFactory = innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));
    }

    /// <summary>媒体分片判据（慢分片竞速只对分片赛跑）：URL 非 .m3u8 清单即按分片对待。</summary>
    public static bool IsSegment(string? url)
        => url is not null
            && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && !url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    public long Open(AndroidX.Media3.DataSource.DataSpec? dataSpec)
    {
        _spec = dataSpec;
        _openAtMs = Environment.TickCount64;
        _raced = false;
        _pendingByte = null;
        _inner = _innerFactory();
        foreach (var l in _listeners) _inner.AddTransferListener(l);
        return _inner.Open(dataSpec);
    }

    public int Read(byte[]? buffer, int offset, int count)
    {
        if (!_raced && _spec is { } spec && IsSegment(spec.Uri?.ToString()))
        {
            _raced = true;
            long elapsed = Environment.TickCount64 - _openAtMs;
            long threshold = RaceFloorMs;
            lock (MeanGate)
            {
                if (_meanFirstByteMs > 0) threshold = _meanFirstByteMs * RaceTriggerFactor;
            }
            if (elapsed > threshold)
            {
                RaceInner(spec); // 慢片坐实：对同源发第二请求赛跑
            }
        }
        if (_pendingByte is { } pending)
        {
            if (count <= 0) return 0;
            buffer![offset] = pending;
            _pendingByte = null;
            return 1;
        }
        var active = _inner!;
        int read = active.Read(buffer, offset, count);
        if (read > 0) RecordFirstByte(Environment.TickCount64 - _openAtMs);
        return read;
    }

    /// <summary>赛跑：第二请求同源同偏移开拉，先拿到首字节者胜；败者关闭，胜者接管本片余下字节。</summary>
    private void RaceInner(AndroidX.Media3.DataSource.DataSpec spec)
    {
        try
        {
            var racer = _innerFactory();
            foreach (var l in _listeners) racer.AddTransferListener(l);
            var probe = new byte[1];
            var raceTask = Task.Run(() =>
            {
                racer.Open(spec);
                return racer.Read(probe, 0, 1);
            });
            var winner = Task.WhenAny(raceTask, Task.Delay(RacerWaitMs)).GetAwaiter().GetResult();
            if (ReferenceEquals(winner, raceTask) && raceTask.Status == TaskStatus.RanToCompletion && raceTask.Result > 0)
            {
                // 第二请求先到：弃慢路用快路（未消费任何字节，偏移零对齐）
                try { _inner?.Close(); } catch { /* 慢路关闭失败无害，资源随进程回收 */ }
                _inner = racer;
                _pendingByte = probe[0];
            }
            else
            {
                try { racer.Close(); } catch { /* 败者回收失败无害 */ }
            }
        }
        catch
        {
            // 竞速是让路角色：任何异常原样退回慢路照播（绝不打断在播）
        }
    }

    private static void RecordFirstByte(long firstByteMs)
    {
        lock (MeanGate)
        {
            _meanFirstByteMs = _meanFirstByteMs == 0
                ? firstByteMs
                : (_meanFirstByteMs * 3 + firstByteMs) / 4;
        }
    }

    public void Close()
    {
        try { _inner?.Close(); } catch { /* 关闭期异常无害 */ }
        _inner = null;
        _spec = null;
        _pendingByte = null;
    }

    public Android.Net.Uri? Uri => _inner?.Uri;

    public IDictionary<string, IList<string>>? ResponseHeaders => _inner?.ResponseHeaders;

    public void AddTransferListener(AndroidX.Media3.DataSource.ITransferListener? transferListener)
        => _listeners.Add(transferListener!);
}

/// <summary>竞速数据源工厂：内层即 ExoPlayer 默认 HTTP 工厂口径（默认超时/头纪律原样），创建时包一层竞速壳。
/// vc87 票据热轮换（内核级）：再包一层换票壳——分片/清单 4xx 即刻换新票同偏移重开，不等会话层自救链
/// （真机台账：央视频坏票 6 秒即被源端拒→循环主因；内核换票比「报错→换源→重取票」整链快一个数量级）。</summary>
public sealed class RacingDataSourceFactory : Java.Lang.Object, AndroidX.Media3.DataSource.IDataSourceFactory
{
    /// <summary>换票委托（vc87）：参数=失败 URL，返回新票 URL（null=不换，原样上抛交会话自救）。
    /// 进程级单份，壳层 once-block 接 TicketResolver.ReissueAsync；未接线=不启用（既有契约不变）。</summary>
    public static Func<string, Task<string?>>? TicketReissuer;

    /// <summary>一片换票封顶（vc87）：同一分片最多换 2 次新票，仍拒即原样上抛——防死票无限重开烧线。
    /// vc93 核实结论：频控熔断后票据缓存项已移除，后续 ReissueAsync 在反查阶段即返回 null、零网络调用；
    /// 内核单分片重开受本封顶 2 次约束；会话退避自救兜底——无紧密空转，不改行为。</summary>
    public const int TicketReissueMaxPerOpen = 2;

    public AndroidX.Media3.DataSource.IDataSource CreateDataSource() =>
        new TicketSwapDataSource(() => new RacingDataSource(CreateInner));

    private static AndroidX.Media3.DataSource.IDataSource CreateInner()
    {
        // 老板令 2026-11-17 慢源宽限 A：内核 HTTP 超时放宽——连接 8→12s（与会话起播窗对齐）、
        // 读 8→15s（连接后慢灌容忍）；真死线（拒绝连接/DNS/404）不受影响照旧快切。
        // 链式 setter 在 xamarin 绑定空性标注下触发 CS8602，拆步落局部变量消除
        var factory = new AndroidX.Media3.DataSource.DefaultHttpDataSource.Factory();
        factory.SetConnectTimeoutMs(12_000);
        factory.SetReadTimeoutMs(15_000);
        return factory.CreateDataSource()!;
    }
}

/// <summary>
/// 换票数据源壳（vc87 票据热轮换，内核级）：包竞速壳工厂，Open 捕获 4xx 级
/// <see cref="AndroidX.Media3.DataSource.HttpDataSourceInvalidResponseCodeException"/> 即调换票委托取新票，
/// 同偏移（Position/Length 不变）重开继续拉——坏票不死循环，封顶 <see cref="RacingDataSourceFactory.TicketReissueMaxPerOpen"/>
/// 次后原样上抛交会话层既有自救（坏票重试/换源）兜底。loader 线程同 RaceInner 口径同步等票。
/// </summary>
public sealed class TicketSwapDataSource : Java.Lang.Object, AndroidX.Media3.DataSource.IDataSource
{
    private readonly Func<AndroidX.Media3.DataSource.IDataSource> _innerFactory;
    private readonly List<AndroidX.Media3.DataSource.ITransferListener> _listeners = new();
    private AndroidX.Media3.DataSource.IDataSource? _inner;

    public TicketSwapDataSource(Func<AndroidX.Media3.DataSource.IDataSource> innerFactory)
    {
        _innerFactory = innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));
    }

    public long Open(AndroidX.Media3.DataSource.DataSpec? dataSpec)
    {
        var inner = _innerFactory();
        _inner = inner;
        foreach (var l in _listeners) inner.AddTransferListener(l);
        try
        {
            return inner.Open(dataSpec);
        }
        catch (AndroidX.Media3.DataSource.HttpDataSourceInvalidResponseCodeException ex)
            when (ex.ResponseCode is >= 400 and < 500)
        {
            var failingUrl = dataSpec?.Uri?.ToString() ?? inner.Uri?.ToString();
            if (string.IsNullOrEmpty(failingUrl))
            {
                throw; // 无失败 URL 可查：无从换票，原样上抛
            }
            for (var attempt = 0; attempt < RacingDataSourceFactory.TicketReissueMaxPerOpen; attempt++)
            {
                var reissuer = RacingDataSourceFactory.TicketReissuer;
                if (reissuer is null) throw;
                // loader 线程同步等票（竞速壳 RaceInner 同口径）：换票是短耗时 HTTP（壳层超时 5s 管着）
                var fresh = reissuer(failingUrl!).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(fresh)) throw; // 未命中/换票失败：原样上抛交会话自救
                if (dataSpec is not { } spec) throw;
                // 同偏移重开：Uri 换新票，Position/Length 原样（换缝字节对齐，与竞速壳换缝同纪律）；
                // 绑定空性标注下链式 setter 触发 CS8602，拆步落局部变量（CreateInner 同款）
                var builder = new AndroidX.Media3.DataSource.DataSpec.Builder();
                builder.SetUri(Android.Net.Uri.Parse(fresh));
                builder.SetPosition(spec.Position);
                builder.SetLength(spec.Length);
                var rebased = builder.Build();
                try
                {
                    return inner.Open(rebased);
                }
                catch (AndroidX.Media3.DataSource.HttpDataSourceInvalidResponseCodeException ex2)
                    when (ex2.ResponseCode is >= 400 and < 500)
                {
                    // 新票也被拒：封顶内再试一次（票据源侧可能刚发的新票同样短命），耗尽即上抛
                    failingUrl = fresh; // 封顶内第二次按新票反查（旧票缓存已作废，按旧 URL 反查会落空）
                }
            }
            throw; // 换票封顶仍拒：原样上抛交会话层自救
        }
    }

    public int Read(byte[]? buffer, int offset, int count) => _inner!.Read(buffer, offset, count);

    public void Close()
    {
        try { _inner?.Close(); } catch { /* 关闭期异常无害 */ }
        _inner = null;
    }

    public Android.Net.Uri? Uri => _inner?.Uri;

    public IDictionary<string, IList<string>>? ResponseHeaders => _inner?.ResponseHeaders;

    public void AddTransferListener(AndroidX.Media3.DataSource.ITransferListener? transferListener)
        => _listeners.Add(transferListener!);
}
