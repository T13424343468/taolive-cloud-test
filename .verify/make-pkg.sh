#!/bin/bash
# vc89 交付包组装（坑29 校验三元组 + 报告随包）
set -euo pipefail
cd /root/workspace/taolive-work/taolive-2p0/.verify
rm -rf pkg && mkdir -p pkg/TaoLive-1.4-vc89
cd pkg/TaoLive-1.4-vc89
APKSRC=../../apk89/src/TaoLive.Tv/bin/Release/net10.0-android36.0/publish/com.taolive.tv-Signed.apk
cp "$APKSRC" .
cp ../../apk89/apk.sha256 .
cp "$APKSRC.v45-report.txt" .
cp "$APKSRC.v46-report.txt" .
cp ../../v49-基线对照清单-9c19a10.txt .
{
  echo "TaoLive 1.4 (vc89) 交付包 · $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
  echo "版本锚：csproj 89/1.4 = manifest 89/1.4 = badging 89/1.4（三处对账一致）"
  echo "提交：9c19a10（区间 5c694e6..9c19a10，7 文件）"
  echo "统一验证：242/242 全绿 0 失败；壳层编译 0 错 0 警；云端构建 run 44061063280 全绿"
  echo "老板可见特征：label=TaoLive；包内「正在换信号」在 TaoLive.Session.dll（UTF-16 串池命中）；58 路目录在包（m3u8×82）"
  echo "校验三元组："
  echo "  字节数：$(stat -c %s com.taolive.tv-Signed.apk)"
  echo "  SHA-256：$(sha256sum com.taolive.tv-Signed.apk | cut -d' ' -f1)"
  echo "  顶层清单：$(ls | tr '\n' ' ')"
} > 交付说明.txt
cd ..
rm -f TaoLive-1.4-vc89.zip
zip -q -r TaoLive-1.4-vc89.zip TaoLive-1.4-vc89
ls -l TaoLive-1.4-vc89.zip
sha256sum TaoLive-1.4-vc89.zip
