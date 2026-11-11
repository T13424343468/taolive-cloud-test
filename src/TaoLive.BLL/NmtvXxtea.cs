using System;
using System.Text;

namespace TaoLive.BLL;

/// <summary>
/// 内蒙古腾格里网 XXTEA（Ma Bingyao 版 xxtea.js 协议，与调研/长尾移植/内蒙古NmgSource.cs 同源移植）：
/// 密钥 16 字节 128bit（5b28bae827e651b3），小端 4 字组，明文末尾追加 4 字节小端 padded_len。
/// </summary>
public static class NmtvXxtea
{
    private const uint Delta = 0x9E3779B9;
    public const string Key = "5b28bae827e651b3";

    public static byte[] Decrypt(byte[] data)
    {
        if (data.Length < 8) return data;
        var words = new uint[data.Length / 4];
        Buffer.BlockCopy(data, 0, words, 0, words.Length * 4);
        var dec = Crypt(words, encrypt: false);
        // 末 dword（完整 4 字节小端）为明文长度；不合法时全量输出（服务端老版本口径）
        var p = (int)(dec[dec.Length - 4] | (uint)dec[dec.Length - 3] << 8
            | (uint)dec[dec.Length - 2] << 16 | (uint)dec[dec.Length - 1] << 24);
        var total = dec.Length;
        if (p < 0 || p > total - 4) return dec;
        var plain = new byte[p];
        Buffer.BlockCopy(dec, 0, plain, 0, p);
        return plain;
    }

    /// <summary>对端口径的加密封装（仅测试与协议验证用）：明文补齐 4 字节对齐，末 dword 为小端明文长度。</summary>
    public static byte[] Encrypt(byte[] plain)
    {
        var words = new uint[(plain.Length + 3) / 4 + 1];
        Buffer.BlockCopy(plain, 0, words, 0, plain.Length);
        words[words.Length - 1] = (uint)plain.Length;
        return Crypt(words, encrypt: true);
    }

    private static uint[] BuildKey()
    {
        var kb = new byte[16];
        var k = Encoding.UTF8.GetBytes(Key);
        Buffer.BlockCopy(k, 0, kb, 0, Math.Min(16, k.Length));
        var r = new uint[4];
        for (var i = 0; i < 4; i++)
            r[i] = (uint)(kb[i * 4] | kb[i * 4 + 1] << 8 | kb[i * 4 + 2] << 16 | kb[i * 4 + 3] << 24);
        return r;
    }

    private static byte[] Crypt(uint[] v, bool encrypt)
    {
        var k = BuildKey();
        var L = v.Length;
        if (L < 2) return ToBytes(v);
        var n = L - 1;
        if (!encrypt)
        {
            // 与 xxtea-js（Ma Bingyao, 2019）decryptUint32Array 逐句对齐：y 跨步传递
            var vv = (uint[])v.Clone();
            var y = vv[0];
            var q = 6 + 52 / L;
            var sum = (uint)((ulong)q * Delta & 0xFFFFFFFF);
            while (sum != 0)
            {
                var e = (int)((sum >> 2) & 3);
                for (var p = n; p > 0; p--)
                {
                    var z = vv[p - 1];
                    y = vv[p] = vv[p] - Mx(sum, y, z, p, e, k);
                }
                var zn = vv[n];
                y = vv[0] = vv[0] - Mx(sum, y, zn, 0, e, k);
                sum -= Delta;
            }
            return ToBytes(vv);
        }
        // 与 xxtea-js encryptUint32Array 逐句对齐
        var v2 = (uint[])v.Clone();
        var z2 = v2[n];
        var q2 = 6 + 52 / L;
        var s = 0u;
        while (q2-- > 0)
        {
            s += Delta;
            var e2 = (int)((s >> 2) & 3);
            for (var p2 = 0; p2 < n; p2++)
            {
                var y2 = v2[p2 + 1];
                z2 = v2[p2] = v2[p2] + Mx(s, y2, z2, p2, e2, k);
            }
            var y3 = v2[0];
            z2 = v2[n] = v2[n] + Mx(s, y3, z2, n, e2, k);
        }
        return ToBytes(v2);
    }

    private static uint Mx(uint sum, uint y, uint z, int p, int e, uint[] k)
        => (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (k[(p & 3) ^ e] ^ z));

    private static byte[] ToBytes(uint[] v)
    {
        var outp = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, outp, 0, outp.Length);
        return outp;
    }
}
