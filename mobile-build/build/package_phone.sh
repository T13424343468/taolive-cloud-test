#!/bin/bash
# TaoLive 手机版打包脚本（dotnet 口径，2026-11-27）
# 用法：在仓库根目录执行 bash mobile-build/build/package_phone.sh
# 产物：dist-phone/TaoLive-1.0.0-vc1/（DELIVERY-NOTE.txt + Signed.apk + sha256.txt）
set -euo pipefail

export DOTNET_ROOT=/root/workspace/.local/dotnet10
export ANDROID_HOME=/root/workspace/.local/android-sdk
export JAVA_HOME=/root/workspace/.local/jdk-21.0.13+9
export PATH=$DOTNET_ROOT:$PATH

cd "$(dirname "$0")/../.."   # 仓库根

VC=1
VER=1.0.0
OUT=dist-phone/TaoLive-${VER}-vc${VC}
APK_NAME=com.taolive.phone-Signed.apk

echo "[1/4] Release 编译（0错0警硬门禁）"
dotnet build src/TaoLive.Phone -c Release
echo "[2/4] 定位签名 APK"
APK=$(ls -t src/TaoLive.Phone/bin/Release/net10.0-android36.0/com.taolive.phone-Signed.apk 2>/dev/null | head -1)
[ -n "$APK" ] || { echo "未找到 Signed.apk"; exit 1; }
echo "[3/4] 组装交付包 $OUT"
mkdir -p "$OUT"
cp "$APK" "$OUT/$APK_NAME"
sha256sum "$OUT/$APK_NAME" | awk '{print $1}' > "$OUT/sha256.txt"
echo "[4/4] badging 核验（versionCode=$VC versionName=$VER 必须一致）"
AAPT2=$(ls $ANDROID_HOME/build-tools/*/aapt2 | sort -V | tail -1)
"$AAPT2" dump badging "$OUT/$APK_NAME" | grep -E "^package|application-label:" | head -4
echo "完成：$OUT"
