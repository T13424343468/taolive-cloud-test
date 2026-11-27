#!/usr/bin/env bash
# TaoLive 手机版逐屏走查出图 v2（制度：视觉模型比对与评分制度 2026-11-26）
# 基准分辨率 1080x2400@420dpi（真机口径）。禁用返回键回播放页（会弹退出确认），
# 浮层一律点外关闭。必截状态：正常/加载/失败/退出确认/换台信息条/亮度HUD/音量HUD/极端数据(83台) × 横竖屏。
set -u
OUT=${1:-walk}
mkdir -p "$OUT"
SHOT() { adb shell screencap -p /sdcard/w.png && adb pull /sdcard/w.png "$OUT/$1.png" >/dev/null 2>&1 && echo "SHOT $1"; }

SIZE=$(adb shell wm size | grep -o '[0-9]*x[0-9]*' | tail -1)
W=${SIZE%x*}; H=${SIZE#*x}
CX=$((W/2)); CY=$((H/2))
echo "SCREEN ${W}x${H}"
echo "SCREEN ${W}x${H}" > "$OUT/screen.txt"

# tap_text：uiautomator 找文本控件中心点击（重试3次）；失败返回1
tap_text() {
  local text="$1" try=1
  while [ $try -le 3 ]; do
    adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || { sleep 1; try=$((try+1)); continue; }
    local line
    line=$(adb shell cat /sdcard/ui.xml 2>/dev/null | tr '>' '>\n' | grep -F "text=\"$text\"" | head -1)
    if [ -n "$line" ]; then
      local nums
      nums=$(echo "$line" | grep -o 'bounds="\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]"' | grep -o '[0-9]*')
      if [ -n "$nums" ]; then
        set -- $nums
        local cx=$(( ($1 + $3) / 2 )) cy=$(( ($2 + $4) / 2 ))
        adb shell input tap "$cx" "$cy"
        echo "TAP_TEXT $text @ $cx,$cy"
        return 0
      fi
    fi
    sleep 1; try=$((try+1))
  done
  echo "TAP_TEXT MISS $text"
  return 1
}

# dump_ui：控件树留证
dump_ui() { adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 && adb shell cat /sdcard/ui.xml > "$OUT/$1" 2>/dev/null; return 0; }

sleep 3
SHOT 01-play-portrait

# —— 竖屏：控制条 ——
adb shell input tap "$CX" "$CY"; sleep 1.2
SHOT 02-controls-portrait
dump_ui ui-controls-portrait.xml

# —— 竖屏：抽屉（83台全量=极端数据） ——
tap_text "☰" || adb shell input tap $((W-120)) $((H-140))
sleep 2.5
SHOT 03-drawer-portrait
dump_ui ui-drawer-portrait.xml
adb shell input swipe "$CX" $((H*3/4)) "$CX" $((H/4)) 500; sleep 1.2
SHOT 03b-drawer-scrolled
# 点外关闭（抽屉遮罩左侧）
adb shell input tap $((W/8)) "$CY"; sleep 1.5
SHOT 03c-drawer-closed

# —— 竖屏：设置页 ——
adb shell input tap "$CX" "$CY"; sleep 1
tap_text "⚙" || adb shell input tap $((W-230)) $((H-140))
sleep 2
SHOT 04-settings-portrait
dump_ui ui-settings-portrait.xml
# 下半：进阶调优
adb shell input swipe "$CX" $((H*3/4)) "$CX" $((H/4)) 600; sleep 1.5
SHOT 05-settings-advanced
# 诊断日志页（三标签）
tap_text "崩溃日志" || true
sleep 1
SHOT 06-diagnostics
# 点外关闭设置（遮罩）
adb shell input tap $((W/10)) "$CY"; sleep 1.5
SHOT 06b-settings-closed

# —— 竖屏：换台信息条（左滑=上一台） ——
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 0.7
SHOT 11-switch-toast
sleep 9

# —— 竖屏：亮度 HUD（左半区上滑，快拍两张防淡出） ——
adb shell input swipe $((W/4)) $((H*2/3)) $((W/4)) $((H/3)) 350
sleep 0.3
SHOT 12-hud-brightness
sleep 0.6
SHOT 12b-hud-brightness-late
sleep 3

# —— 竖屏：音量 HUD（右半区上滑） ——
adb shell input swipe $((W*3/4)) $((H*2/3)) $((W*3/4)) $((H/3)) 350
sleep 0.3
SHOT 13-hud-volume
sleep 0.6
SHOT 13b-hud-volume-late
sleep 3

# —— 退出确认框（真实状态，主动抓一张再取消） ——
adb shell input keyevent 101   # KEYCODE_APP_SWITCH? 不对——用返回键弹确认
sleep 1.5
SHOT 17-exit-dialog
dump_ui ui-exit-dialog.xml
tap_text "取消" || adb shell input tap "$CX" $((H*2/3))
sleep 1.5
SHOT 17b-exit-dialog-dismissed

# —— 横屏段：系统旋转口径（可靠），应用 OnConfigurationChanged 重建 ——
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1
sleep 4
SHOT 07-play-landscape
adb shell input tap "$CX" "$CY"; sleep 1.2
SHOT 08-controls-landscape
dump_ui ui-controls-landscape.xml

tap_text "☰" || adb shell input tap $((W-120)) $((H-140))
sleep 2.5
SHOT 09-drawer-landscape
adb shell input tap $((W/8)) "$CY"; sleep 1.5

adb shell input tap "$CX" "$CY"; sleep 1
tap_text "⚙" || adb shell input tap $((W-230)) $((H-140))
sleep 2
SHOT 10-settings-landscape
adb shell input tap $((W/10)) "$CY"; sleep 1.5

# 转回竖屏
adb shell settings put system user_rotation 0
sleep 4
SHOT 00-back-portrait

# —— 失败态（断网切台，等失败终态） ——
adb shell svc wifi disable 2>/dev/null; adb shell svc data disable 2>/dev/null
sleep 2
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 35
SHOT 14-fail-state
adb shell svc wifi enable 2>/dev/null; adb shell svc data enable 2>/dev/null
sleep 10

# —— 加载态（恢复网络后切台瞬间） ——
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 1
SHOT 15-loading
sleep 12
SHOT 16-recovered-play

adb logcat -d > "$OUT/walkthrough-logcat.txt" 2>/dev/null || true
echo "WALKTHROUGH_DONE"
