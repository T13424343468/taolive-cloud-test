#!/usr/bin/env bash
# AB 双包发布脚本：互斥锁 + 清 bin/obj 先清后建 + 验签 + 打 zip
# 用法: publish_ab.sh <zip名> <额外publish参数...>
# 例:   publish_ab.sh TaoLive-A-v1.0-vc26.zip -p:RunAOTCompilation=false -p:AndroidUseInterpreter=true
set -u
LOCK=/tmp/publish.lock
exec 9>"$LOCK"
flock -n 9 || { echo "[ABORT] 已有发布任务在跑，互斥锁被占"; exit 2; }

ZIP_NAME="$1"; shift
DOTNET=/root/workspace/.local/dotnet9/dotnet
export DOTNET_ROOT=/root/workspace/.local/dotnet9
SDK_BT=/root/workspace/.local/android-sdk/build-tools/36.1.0
KS=keystore/debug.keystore
ROOT=/root/workspace/taolive-b20-wt
PROJ=src/TaoLive.UI.Android.Tv/TaoLive.UI.Android.Tv.csproj

cd "$ROOT" || exit 3
echo "=== [1/6] 清 bin/obj ==="
rm -rf src/TaoLive.UI.Android.Tv/bin src/TaoLive.UI.Android.Tv/obj

echo "=== [2/6] publish: $* ==="
"$DOTNET" publish "$PROJ" -f net9.0-android -c Release \
  -p:AndroidSdkDirectory=/root/workspace/.local/android-sdk \
  -p:JavaSdkDirectory=/root/workspace/.local/jdk/jdk-17.0.21.0.2+1 \
  -p:UseMonoRuntime=true -p:RuntimeIdentifier=android-arm \
  "$@" > /tmp/publish_current.log 2>&1
EXIT=$?
echo "publish EXIT=$EXIT"
tail -3 /tmp/publish_current.log
[ $EXIT -ne 0 ] && { echo "[FAIL] publish 失败"; exit $EXIT; }

APK=src/TaoLive.UI.Android.Tv/bin/Release/net9.0-android/android-arm/com.taolive.tv-Signed.apk
[ -f "$APK" ] || { echo "[FAIL] 未找到产物 APK"; exit 4; }

echo "=== [3/6] aapt2 核验版本 ==="
"$SDK_BT/aapt2" dump badging "$APK" | head -1
"$SDK_BT/aapt2" dump badging "$APK" | grep -E "package:|application-label" | head -2

echo "=== [4/6] apksigner 补签+验签 ==="
"$SDK_BT/apksigner" sign --ks "$KS" --ks-pass pass:android --ks-key-alias androiddebugkey --out "$APK" "$APK" || { echo "[FAIL] 补签失败"; exit 5; }
"$SDK_BT/apksigner" verify --print-certs "$APK" | head -5

echo "=== [5/6] 指纹档 + zip ==="
STAGE="dist/stage_ab"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp "$APK" "$STAGE/TaoLive.apk"
( cd "$STAGE" && sha256sum TaoLive.apk > SHA256SUMS )
APK_SHA=$(sha256sum "$APK" | cut -d' ' -f1)
( cd dist/stage_ab && zip -q "$ROOT/dist/$ZIP_NAME" TaoLive.apk SHA256SUMS )
ZIP_SHA=$(sha256sum "dist/$ZIP_NAME" | cut -d' ' -f1)

echo "=== [6/6] 完成 ==="
echo "APK sha256: $APK_SHA"
echo "ZIP sha256: $ZIP_SHA"
ls -l "dist/$ZIP_NAME"
