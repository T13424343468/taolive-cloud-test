#!/usr/bin/env bash
# v46 特征关扫描（总镜子配套件）：对 APK 全量比对，产出报告随包。
# 三道闸（W1 断点文档口径）：
#   ① 屏幕信息行（一期特征）：不在——资源表与 dex 字符串全量搜禁词，命中即不放行；
#   ② 应用标签纯英文：application-label 必须逐字等于 TaoLive（含中文即不放行）；
#   ③ 电视横幅：@drawable/tv_banner 随包（badging 可见 application-banner）。
# 用法： tools/verify_feature_gate.sh <apk>
set -euo pipefail

APK="${1:-}"
[ -n "$APK" ] && [ -f "$APK" ] || { echo "用法：tools/verify_feature_gate.sh <apk>"; exit 2; }

AAPT="$(ls /root/workspace/.local/android-sdk/build-tools/*/aapt 2>/dev/null | sort -V | tail -1)"
[ -n "$AAPT" ] || { echo "[v46] 找不到 aapt"; exit 2; }

# 一期特征禁词：屏幕信息行及其别名（新增禁词直接续行，禁止在调用方临时拼）
BANNED_PATTERNS=("屏幕信息行" "信息行（" "现帧率" "原帧率")

BADGING="$("$AAPT" dump badging "$APK")"
LABEL="$(printf '%s\n' "$BADGING" | grep '^application-label:' | head -1 | sed 's/^application-label://; s/^'\''//; s/'\''$//' || true)"
# 横幅在 application: 行的 banner= 属性（aapt 无独立 application-banner 行）
APP_LINE="$(printf '%s\n' "$BADGING" | grep '^application:' | head -1)"
BANNER_OK="$(printf '%s' "$APP_LINE" | grep -o "banner='[^']*'" | grep -c . || true)"

TMPDIR_V46="$(mktemp -d)"
trap 'rm -rf "$TMPDIR_V46"' EXIT
unzip -q "$APK" resources.arsc 'classes*.dex' -d "$TMPDIR_V46" 2>/dev/null || true

REPORT="${APK}.v46-report.txt"
{
  echo "v46 特征关扫描 · $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
  echo "APK：$APK"
  echo "① 屏幕信息行：资源表与 dex 全量扫禁词（${BANNED_PATTERNS[*]}）"
  echo "② 应用标签：$LABEL（期望逐字 TaoLive，纯英文）"
  echo "③ 电视横幅：application-banner 可见性=$([ "$BANNER_OK" -gt 0 ] && echo 是 || echo 否)"
} > "$REPORT"

FAIL=0

# ① 禁词扫描：resources.arsc（文本可见）与 classes*.dex（编译期常量）都查
for f in "$TMPDIR_V46/resources.arsc" "$TMPDIR_V46"/classes*.dex; do
  [ -f "$f" ] || continue
  for pat in "${BANNED_PATTERNS[@]}"; do
    if grep -q "$pat" "$f" 2>/dev/null; then
      echo "[v46] 特征关命中：$(basename "$f") 含禁词「$pat」（屏幕信息行特征应不在）"
      echo "  命中：$pat 于 $(basename "$f")" >> "$REPORT"
      FAIL=1
    fi
  done
done

# ② 标签逐字对 + 纯英文校验
if [ "$LABEL" != "TaoLive" ]; then
  echo "[v46] 应用标签不符：$LABEL ≠ TaoLive"
  echo "  标签不符：$LABEL" >> "$REPORT"; FAIL=1
fi
if printf '%s' "$LABEL" | grep -qP '[\x{4e00}-\x{9fff}]'; then
  echo "[v46] 应用标签含中文，非纯英文：$LABEL"
  echo "  标签含中文" >> "$REPORT"; FAIL=1
fi

# ③ 横幅随包
if [ "$BANNER_OK" -eq 0 ]; then
  echo "[v46] 电视横幅缺失：badging 未见 application-banner"
  echo "  横幅缺失" >> "$REPORT"; FAIL=1
fi

if [ "$FAIL" -eq 0 ]; then
  echo "[v46] 通过：信息行不在 / 标签 TaoLive 纯英文 / 横幅随包。报告：$REPORT"
else
  echo "[v46] 不放行：详见 $REPORT"
fi
exit "$FAIL"
