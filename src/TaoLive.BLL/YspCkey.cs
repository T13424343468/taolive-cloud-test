using System;
using System.Text;

namespace TaoLive.BLL;

/// <summary>
/// 央视频 cKey 票据（TEA 八字节反馈封装），自参考实现调研/ysp/ckey.js 逐字段移植。
/// 对拍向量：Python 参考实现（ckey.py，固定随机源）产出，见 tests 断言。
/// 2026-11-07 沙箱实测：本算法生成票据对 CCTV-1 真接口 iretcode=0 取得 playurl。
/// </summary>
public static class YspCkey
{
    public const uint Platform = 4330403;
    public const string AppVersion = "V8.22.1035.3031";
    internal static readonly byte[] CkeyTeaKey = Hex("59b2f7cf725ef43c34fdd7c123411ed3");
    internal static readonly byte[] GuardTeaKey = Hex("110DBEC10C23E7D2E56A1CAD6914EF1B");
    internal static readonly byte[] CkeyXor =
        { 0x84, 0x2e, 0xed, 0x08, 0xf0, 0x66, 0xe6, 0xea, 0x48, 0xb4, 0xca, 0xa9, 0x91, 0xed, 0x6f, 0xf3 };
    internal static readonly byte[] GuardXor = { 0xb3, 0xc9, 0x53, 0xa0, 0x69, 0x13, 0xad, 0x4d };

    internal static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    internal static void TeaEncryptBlock(ReadOnlySpan<byte> block, byte[] key, Span<byte> outBlock)
    {
        uint y = (uint)(block[0] << 24 | block[1] << 16 | block[2] << 8 | block[3]);
        uint z = (uint)(block[4] << 24 | block[5] << 16 | block[6] << 8 | block[7]);
        uint k0 = (uint)(key[0] << 24 | key[1] << 16 | key[2] << 8 | key[3]);
        uint k1 = (uint)(key[4] << 24 | key[5] << 16 | key[6] << 8 | key[7]);
        uint k2 = (uint)(key[8] << 24 | key[9] << 16 | key[10] << 8 | key[11]);
        uint k3 = (uint)(key[12] << 24 | key[13] << 16 | key[14] << 8 | key[15]);
        uint sum = 0;
        for (int i = 0; i < 16; i++)
        {
            sum += 0x9e3779b9;
            y += ((z << 4) + k0) ^ (z + sum) ^ ((z >> 5) + k1);
            z += ((y << 4) + k2) ^ (y + sum) ^ ((y >> 5) + k3);
        }
        outBlock[0] = (byte)(y >> 24); outBlock[1] = (byte)(y >> 16);
        outBlock[2] = (byte)(y >> 8); outBlock[3] = (byte)y;
        outBlock[4] = (byte)(z >> 24); outBlock[5] = (byte)(z >> 16);
        outBlock[6] = (byte)(z >> 8); outBlock[7] = (byte)z;
    }

    internal static uint Checksum(ReadOnlySpan<byte> b)
    {
        uint v = 0;
        foreach (var t in b) v = (0x83 * v + t) & 0x7fffffff;
        return v;
    }

    /// <summary>腾讯/QQ TEA 八字节反馈模式封包。</summary>
    internal static byte[] EncryptTeaPacket(byte[] input, byte[] key, Func<int, byte[]> randomBytes)
    {
        int padLen = (8 - ((input.Length + 10) % 8)) % 8;
        var plain = new byte[1 + padLen + 2 + input.Length + 7];
        plain[0] = (byte)((randomBytes(1)[0] & 0xf8) | padLen);
        randomBytes(padLen + 2).CopyTo(plain, 1);
        input.CopyTo(plain, 1 + padLen + 2);
        var outBuf = new byte[plain.Length];
        var prevPlain = new byte[8];
        var prevCipher = new byte[8];
        for (int off = 0; off < plain.Length; off += 8)
        {
            var mixed = new byte[8];
            for (int i = 0; i < 8; i++) mixed[i] = (byte)(plain[off + i] ^ prevCipher[i]);
            var enc = new byte[8];
            TeaEncryptBlock(mixed, key, enc);
            for (int i = 0; i < 8; i++) outBuf[off + i] = (byte)(enc[i] ^ prevPlain[i]);
            mixed.CopyTo(prevPlain, 0);
            Array.Copy(outBuf, off, prevCipher, 0, 8);
        }
        return outBuf;
    }

    private static byte[] Lp(string s)
    {
        var d = Encoding.UTF8.GetBytes(s);
        var b = new byte[2 + d.Length];
        b[0] = (byte)(d.Length >> 8); b[1] = (byte)d.Length;
        d.CopyTo(b, 2);
        return b;
    }

    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] LpBytes(byte[] d)
    {
        var b = new byte[2 + d.Length];
        b[0] = (byte)(d.Length >> 8); b[1] = (byte)d.Length;
        d.CopyTo(b, 2);
        return b;
    }

    private static string GuardTail(string v) => v.Length >= 5 ? v[^5..] : "";

    private static byte[] CreateGuardBody(long ts, string guid)
    {
        using var ms = new System.IO.MemoryStream();
        ms.Write(U32((uint)ts));
        ms.Write(Lp(GuardTail(guid)));
        ms.Write(Lp(GuardTail("null")));
        ms.Write(Lp(GuardTail("null")));
        ms.Write(Lp("-1"));
        // 注意：包体是原始字节，不能经字符串转码（高位字节会被 UTF-8 重编码），必须按字节封长。
        return LpBytes(ms.ToArray());
    }

    internal static string CreateGuard(long ts, string guid, Func<int, byte[]> randomBytes)
    {
        var body = CreateGuardBody(ts, guid);
        var cipher = EncryptTeaPacket(body, GuardTeaKey, randomBytes);
        var enc = new byte[cipher.Length + 4];
        cipher.CopyTo(enc, 0);
        U32(Checksum(body)).CopyTo(enc, cipher.Length);
        for (int i = 0; i < enc.Length; i++) enc[i] ^= GuardXor[i & 7];
        return Convert.ToHexString(enc);
    }

    internal static byte[] BuildPacket(string channelId, long ts, string guid, string guard, string uid)
    {
        using var ms = new System.IO.MemoryStream();
        ms.Write(Hex("0000004200000004000004d2"));
        ms.Write(U32(Platform));
        ms.Write(U32(0));
        ms.Write(U32((uint)ts));
        ms.Write(Lp("dcgh"));
        ms.Write(Lp("_zj1A5Gh6QYcxWjIUGos2w=="));
        ms.Write(Lp(AppVersion));
        ms.Write(Lp(channelId));
        ms.Write(Lp(guid));
        ms.Write(U32(1));
        ms.Write(U32(1));
        ms.Write(Lp(uid));
        ms.Write(Lp("nil"));
        ms.Write(Lp("57eab0c4-2c58-44c6-8ae9-dd2757525dc5"));
        ms.Write(Lp("nil"));
        ms.Write(Lp("v0.1.000"));
        ms.Write(Lp("com.cctv.yangshipin.app.iphone"));
        ms.Write(Lp(Platform.ToString()));
        ms.Write(Lp("ex_json_bus"));
        ms.Write(Lp("ex_json_vs"));
        ms.Write(Lp(guard));
        var body = ms.ToArray();
        var packet = new byte[body.Length + 2];
        packet[0] = (byte)(body.Length >> 8); packet[1] = (byte)body.Length;
        body.CopyTo(packet, 2);
        var sum = BitConverter.GetBytes(Checksum(packet));
        packet[18] = sum[3]; packet[19] = sum[2]; packet[20] = sum[1]; packet[21] = sum[0];
        return packet;
    }

    internal static string CustomBase64(byte[] b) =>
        Convert.ToBase64String(b).Replace('+', '_').Replace('/', '-').TrimEnd('=');

    /// <summary>生成 cKey；guid 由调用方固定（同一设备保持同 guid，服务端按设备画像下发清晰度）。</summary>
    public static (string CKey, string Guid, string Uid) Create(string channelId, long ts, string guid, Func<int, byte[]>? randomBytes = null)
    {
        randomBytes ??= b => { var a = new byte[b]; Random.Shared.NextBytes(a); return a; };
        var guard = CreateGuard(ts, guid, randomBytes);
        var uid = Convert.ToHexString(randomBytes(4));
        var packet = BuildPacket(channelId, ts, guid, guard, uid);
        var cipher = EncryptTeaPacket(packet, CkeyTeaKey, randomBytes);
        var full = new byte[cipher.Length + 4];
        cipher.CopyTo(full, 0);
        U32(Checksum(packet)).CopyTo(full, cipher.Length);
        for (int i = 0; i < full.Length; i++) full[i] ^= CkeyXor[i & 15];
        return (("--01" + CustomBase64(full)), guid, uid);
    }
}

/// <summary>
/// 取票服务：每频道缓存真实 playurl，正常 5 分钟换票；
/// 1.0.12 修法 B：403 冷却从固定 30 秒改指数退避账（2/4/8 分钟 ×2 步进，封顶 2 小时）——
/// 30 秒一轮反复重试坏流是被自身重试频率喂出来的风控（真机 10:40 日志实锤）；取票成功全额恢复；
/// 版权停播（iretcode 文案含「版权」）冷却到节目结束。取票请求带固定 UA（App 全局统一）。
/// </summary>
public class YspTicketService
{
    private sealed class Ticket { public string PlayUrl = ""; public long FetchedAt; }
    // v25 修法七 3a：热备校验流与稳态循环/播放并发出入账 → 并发安全（锁保护，读写都在锁内）
    private readonly object _lock = new();
    private readonly Dictionary<string, Ticket> _cache = new();
    private readonly Dictionary<string, (DateTime Until, int Streak)> _cooldownUntil = new();
    public static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(5);
    /// <summary>1.0.12 修法 B：退避基值 2 分钟（连败 ×2 步进：2/4/8/16…）。</summary>
    public static readonly TimeSpan CooldownBase = TimeSpan.FromMinutes(2);
    /// <summary>1.0.12 修法 B：退避封顶 2 小时（与 SourceBackoffLedger 同一口径）。</summary>
    public static readonly TimeSpan CooldownMax = TimeSpan.FromHours(2);

    private readonly string _guid;
    public const string ClientUserAgent = "Mozilla/5.0 (Linux; Android 12; TCL 50V8G) AppleWebKit/537.36 TaoLive/1.0";

    public YspTicketService() => _guid = DeviceIdentity.GetGuid(); // A类第7项：身份持久化，重启不翻新

    public string BuildTicketUrl(string cnlid, string livepid, string defn, long tsUnix)
    {
        var (cKey, guid, _) = YspCkey.Create(cnlid, tsUnix, _guid);
        return
            $"https://bkliveinfo.ysp.cctv.cn/?cnlid={cnlid}&livepid={livepid}&cKey={Uri.EscapeDataString(cKey)}" +
            $"&guid={guid}&platform={YspCkey.Platform}&appVer={YspCkey.AppVersion}&defn={defn}&defnsrc=1" +
            $"&stream=2&encryptVer=4.2&cmd=2&otype=json&refer_pageId=page_launch&pageId=page_launch" +
            $"&playbacktime=0&logintype=0&vip_status=0&newnettype=1";
    }

    /// <summary>是否允许本次取票（缓存窗口判断；纯 epoch 比较，避免 DateTime Kind 歧义）。</summary>
    public bool ShouldFetch(string channelId, long nowEpoch)
    {
        lock (_lock)
        {
            return !_cache.TryGetValue(channelId, out var t)
                || nowEpoch - t.FetchedAt >= (long)RenewInterval.TotalSeconds;
        }
    }

    /// <summary>缓存窗口内直接复用真实 playurl（预连接/快速切台零等待）。</summary>
    public bool TryGet(string channelId, long nowEpoch, out string? playUrl)
    {
        lock (_lock)
        {
            playUrl = null;
            return _cache.TryGetValue(channelId, out var t)
                && nowEpoch - t.FetchedAt < (long)RenewInterval.TotalSeconds
                && !string.IsNullOrEmpty(t.PlayUrl)
                && (playUrl = t.PlayUrl) != null;
        }
    }

    public void MarkFetched(string channelId, long nowEpoch, string playUrl)
    {
        lock (_lock)
        {
            _cache[channelId] = new Ticket { PlayUrl = playUrl, FetchedAt = nowEpoch };
            _cooldownUntil.Remove(channelId); // 1.0.12 修法 B：取票成功=退避全额恢复（口子开自动切回）
        }
    }

    /// <summary>
    /// 1.0.12 修法 B·记录一次取票失败（403/风控/清单失效）：该频道退避一个周期，
    /// 连败 ×2 步进（2/4/8 分钟起步拉长），封顶 CooldownMax。now=当前时刻（调用方注入）。
    /// </summary>
    public void MarkCooldown(string channelId, DateTime now)
    {
        lock (_lock)
        {
            int streak = _cooldownUntil.TryGetValue(channelId, out var cur) ? cur.Streak + 1 : 1;
            long ms = (long)CooldownBase.TotalMilliseconds;
            for (int i = 1; i < streak; i++)
            {
                ms *= 2;
                if (ms >= (long)CooldownMax.TotalMilliseconds) { ms = (long)CooldownMax.TotalMilliseconds; break; }
            }
            _cooldownUntil[channelId] = (now.AddMilliseconds(ms), streak);
        }
    }

    public bool InCooldown(string channelId, DateTime now)
    {
        lock (_lock)
        {
            return _cooldownUntil.TryGetValue(channelId, out var u) && now < u.Until;
        }
    }

    /// <summary>退避剩余毫秒（未冻结为 0）：限流提示按剩余时长动态给预期。</summary>
    public long RemainingCooldownMs(string channelId, DateTime now)
    {
        lock (_lock)
        {
            return _cooldownUntil.TryGetValue(channelId, out var u) && now < u.Until
                ? (long)(u.Until - now).TotalMilliseconds : 0;
        }
    }

    /// <summary>自愈6/8：清空全部票据缓存（可疑状态重置，静默，随后自然重取）。</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _cache.Clear();
            _cooldownUntil.Clear();
        }
    }

    /// <summary>
    /// v25 修法二 2c/2d：只清退避账、保留票据缓存（网络变更换 IP / 时钟跳变后，
    /// 旧账是旧 IP/旧墙钟记的，新环境不可信；票据缓存可继续验证，失败自然重新入账）。
    /// </summary>
    public void ClearCooldowns()
    {
        lock (_lock)
        {
            _cooldownUntil.Clear();
        }
    }
}
