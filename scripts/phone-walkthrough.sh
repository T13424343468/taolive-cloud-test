#!/usr/bin/env bash
# TaoLive 手机版逐屏走查出图（制度：视觉模型比对与评分制度 2026-11-26）
# 必截状态：正常/空态/加载/失败/焦点(触屏无焦点态,记录)/主题(单水墨主题,记录)/极端数据(83台全量列表)
# 覆盖：竖屏+横屏 × 播放/控制条/抽屉/设置/进阶调优/诊断/换台信息条/亮度HUD/音量HUD/失败态/加载态
set -u
OUT=${1:-walk}
mkdir -p "$OUT"
SHOT() { adb shell screencap -p /sdcard/w.png && adb pull /sdcard/w.png "$OUT/$1.png" >/dev/null && echo "SHOT $1"; }

# tap_text：uiautomator 找含指定文本控件的中心点并点击；找不到返回1
tap_text() {
  local text="$1"
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || return 1
  local bounds
  bounds=$(adb shell cat /sdcard/ui.xml 2>/dev/null | tr '>' '>\n' | grep -F "text=\"$text\"" | head -1 | grep -o 'bounds="\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]"' | head -1)
  [ -n "$bounds" ] || return 1
  local nums x1 y1 x2 y2 cx cy
  nums=$(echo "$bounds" | grep -o '[0-9]*')
  set -- $nums
  x1=$1; y1=$2; x2=$3; y2=$4
  cx=$(( (x1 + x2) / 2 )); cy=$(( (y1 + y2) / 2 ))
  adb shell input tap "$cx" "$cy"
  echo "TAP_TEXT $text @ $cx,$cy"
}

SIZE=$(adb shell wm size | grep -o '[0-9]*x[0-9]*' | tail -1)
W=${SIZE%x*}; H=${SIZE#*x}
CX=$((W/2)); CY=$((H/2))
echo "SCREEN ${W}x${H}"

# —— 竖屏段 ——
sleep 3
SHOT 01-play-portrait

# 控制条：单击呼出
adb shell input tap "$CX" "$CY"; sleep 1.5
SHOT 02-controls-portrait

# 抽屉（83台全量列表=极端数据态）
tap_text "☰" || adb shell input tap $((W-80)) $((H-100))
sleep 2.5
SHOT 03-drawer-portrait
adb shell input keyevent 4; sleep 1.5   # 关抽屉回播放

# 设置页
adb shell input tap "$CX" "$CY"; sleep 1
tap_text "⚙" || { adb shell input tap "$CX" "$CY"; sleep 1; tap_text "⚙"; }
sleep 2
SHOT 04-settings-portrait

# 设置页下半（进阶调优）
adb shell input swipe "$CX" $((H*3/4)) "$CX" $((H/4)) 600; sleep 1.5
SHOT 05-settings-advanced.png
SHOT 05-settings-advanced
adb shell input swipe "$CX" $((H*3/4)) "$CX" $((H/4)) 600; sleep 1.5
SHOT 06-diagnostics
adb shell input keyevent 4; sleep 1.5   # 关设置回播放

# 换台信息条（左滑=上一台，整改第7项口径）
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 0.8
SHOT 11-switch-toast
sleep 8

# 亮度 HUD（左半区上滑）
adb shell input swipe $((W/4)) $((H*2/3)) $((W/4)) $((H/3)) 400
sleep 0.6
SHOT 12-hud-brightness
sleep 3

# 音量 HUD（右半区上滑）
adb shell input swipe $((W*3/4)) $((H*2/3)) $((W*3/4)) $((H/3)) 400
sleep 0.6
SHOT 13-hud-volume
sleep 3

# —— 横屏段（用应用内 ⟳ 按钮，走真实转屏路径） ——
adb shell input tap "$CX" "$CY"; sleep 1
tap_text "⟳" || { adb shell input tap "$CX" "$CY"; sleep 1; tap_text "⟳"; }
sleep 4
SHOT 07-play-landscape

adb shell input tap "$CX" "$CY"; sleep 1.5
SHOT 08-controls-landscape

tap_text "☰" || adb shell input tap $((W-80)) $((H-100))
sleep 2.5
SHOT 09-drawer-landscape
adb shell input keyevent 4; sleep 1.5

adb shell input tap "$CX" "$CY"; sleep 1
tap_text "⚙" || { adb shell input tap "$CX" "$CY"; sleep 1; tap_text "⚙"; }
sleep 2
SHOT 10-settings-landscape
adb shell input keyevent 4; sleep 1.5

# 转回竖屏
adb shell input tap "$CX" "$CY"; sleep 1
tap_text "⟳" || { adb shell input tap "$CX" "$CY"; sleep 1; tap_text "⟳"; }
sleep 4
SHOT 00-back-portrait

# —— 失败态（断网后切台） ——
adb shell svc wifi disable 2>/dev/null; adb shell svc data disable 2>/dev/null
sleep 2
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 15
SHOT 14-fail-state
adb shell svc wifi enable 2>/dev/null; adb shell svc data enable 2>/dev/null
sleep 8

# —— 加载态（恢复网络后切台瞬间） ——
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 1
SHOT 15-loading
sleep 10
SHOT 16-recovered-play

# 控件树留证（焦点/可达性复核用）
adb shell uiautomator dump /sdcard/ui-final.xml >/dev/null 2>&1
adb shell cat /sdcard/ui-final.xml > "$OUT/ui-tree-final.xml" 2>/dev/null || true
adb logcat -d > "$OUT/walkthrough-logcat.txt" 2>/dev/null || true
echo "WALKTHROUGH_DONE"
