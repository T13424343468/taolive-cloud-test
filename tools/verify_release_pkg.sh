#!/usr/bin/env bash
# v45 包名/版本核验（总镜子配套件）：出包前对 APK 跑 aapt dump badging，
# versionCode / versionName / 包名与交付记录逐字对，任何不符即非零退出（不放行）。
# 用法： tools/verify_release_pkg.sh <apk> [--code 57] [--version 2.0.0] [--pkg com.taolive.tv]
set -euo pipefail

APK=""
EXPECT_CODE=""
EXPECT_VERSION=""
EXPECT_PKG="com.taolive.tv"

while [ $# -gt 0 ]; do
  case "$1" in
    --code) EXPECT_CODE="$2"; shift 2 ;;
    --version) EXPECT_VERSION="$2"; shift 2 ;;
    --pkg) EXPECT_PKG="$2"; shift 2 ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) APK="$1"; shift ;;
  esac
done

[ -n "$APK" ] || { echo "[v45] 缺 APK 路径"; exit 2; }
[ -f "$APK" ] || { echo "[v45] APK 不存在：$APK"; exit 2; }

AAPT="$(ls /root/workspace/.local/android-sdk/build-tools/*/aapt 2>/dev/null | sort -V | tail -1)"
[ -n "$AAPT" ] || { echo "[v45] 找不到 aapt"; exit 2; }

BADGING="$("$AAPT" dump badging "$APK")"
PKG_LINE="$(printf '%s\n' "$BADGING" | grep '^package:' | head -1)"
ACT_PKG="$(printf '%s' "$PKG_LINE" | grep -o "name='[^']*'" | head -1 | sed "s/name='//; s/'$//")"
ACT_CODE="$(printf '%s' "$PKG_LINE" | grep -o "versionCode='[^']*'" | sed "s/versionCode='//; s/'$//")"
ACT_VERSION="$(printf '%s' "$PKG_LINE" | grep -o "versionName='[^']*'" | sed "s/versionName='//; s/'$//")"

REPORT="${APK}.v45-report.txt"
{
  echo "v45 包名/版本核验 · $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
  echo "APK：$APK"
  echo "包名：$ACT_PKG（期望 $EXPECT_PKG）"
  echo "versionCode：$ACT_CODE（期望 $EXPECT_CODE）"
  echo "versionName：$ACT_VERSION（期望 $EXPECT_VERSION）"
} > "$REPORT"

FAIL=0
[ "$ACT_PKG" = "$EXPECT_PKG" ] || { echo "[v45] 包名不符：$ACT_PKG ≠ $EXPECT_PKG"; FAIL=1; }
if [ -n "$EXPECT_CODE" ] && [ "$ACT_CODE" != "$EXPECT_CODE" ]; then
  echo "[v45] versionCode 不符：$ACT_CODE ≠ $EXPECT_CODE（真机覆盖安装降级判废风险）"; FAIL=1
fi
if [ -n "$EXPECT_VERSION" ] && [ "$ACT_VERSION" != "$EXPECT_VERSION" ]; then
  echo "[v45] versionName 不符：$ACT_VERSION ≠ $EXPECT_VERSION"; FAIL=1
fi

if [ "$FAIL" -eq 0 ]; then
  echo "[v45] 通过：包名/versionCode/versionName 三项逐字一致。报告：$REPORT"
else
  echo "[v45] 不放行：详见 $REPORT"
fi
exit "$FAIL"
