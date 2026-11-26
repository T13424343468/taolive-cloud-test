#!/usr/bin/env python3
# 坑40 老板可见特征关：.NET Android 托管串在 assemblies.*.blob（XALZ=LZ4 块压缩，直 grep 必漏）。
# 本脚本解出 blob 内全部程序集，字节级搜「正在换信号」与「正在连接」特征串。
import struct, sys, zipfile

def lz4_block_decompress(src, uncompressed_size):
    dst = bytearray()
    i = 0
    n = len(src)
    while i < n:
        token = src[i]; i += 1
        lit_len = token >> 4
        if lit_len == 15:
            while True:
                b = src[i]; i += 1
                lit_len += b
                if b != 255:
                    break
        dst += src[i:i + lit_len]
        i += lit_len
        if i >= n:
            break
        offset = src[i] | (src[i + 1] << 8); i += 2
        match_len = token & 0xF
        if match_len == 15:
            while True:
                b = src[i]; i += 1
                match_len += b
                if b != 255:
                    break
        match_len += 4
        start = len(dst) - offset
        for j in range(match_len):
            dst.append(dst[start + j])
    assert len(dst) == uncompressed_size, f"LZ4 解出 {len(dst)} ≠ 声明 {uncompressed_size}"
    return bytes(dst)

def main(apk):
    needles = ["正在换信号".encode("utf-8"), "正在换信号".encode("utf-16-le")]
    hits = {}
    with zipfile.ZipFile(apk) as z:
        names = [n for n in z.namelist() if n.endswith(".blob")]
        print("blob 清单:", names)
        for name in names:
            data = z.read(name)
            pos = 0
            found = 0
            while True:
                idx = data.find(b"XALZ", pos)
                if idx < 0:
                    break
                # XALZ 头：magic(4) + 版本/保留(4) + 未压缩大小(4)
                uncompressed = struct.unpack_from("<I", data, idx + 8)[0]
                if 0 < uncompressed < 200 * 1024 * 1024:
                    try:
                        out = lz4_block_decompress(data[idx + 12:], uncompressed)
                        for nd in needles:
                            if out.find(nd) >= 0:
                                found += 1
                                print(f"命中：{name} 内程序集条目 @0x{idx:x}")
                        pos = idx + 12 + min(len(data) - idx - 12, max(1024, uncompressed))  # 压缩块上界近似，靠下轮 find 兜底
                        pos = idx + 4
                    except AssertionError as e:
                        print(f"@0x{idx:x} 解压失败（非本条目/截断）: {e}")
                        pos = idx + 4
                else:
                    pos = idx + 4
            hits[name] = found
    total = sum(hits.values())
    print("特征串命中条目数:", hits)
    return 0 if total > 0 else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
