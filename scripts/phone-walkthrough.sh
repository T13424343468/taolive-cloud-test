#!/usr/bin/env bash
# TaoLive 手机版逐屏走查出图 v3（制度：视觉模型比对与评分制度 2026-11-26）
# v3 改法（对照 v2 实拍缺陷）：
#   ① 控制条按钮改「源码布局推算坐标」直点——uiautomator dump 对视频页常空/超时，且控制条 3s 自动收起，
#      dump 再点必 miss（v2 抽屉/设置全没打开的根因）。坐标按 1080x2400@420dpi、UiScale 手机重定标
#      （PxScale=2.769、按钮定高 122px、左右内距 50px）推算，音量行右对齐。
#   ② HUD 改「手势进行中快拍」——ShowHud 700ms 自隐，v2 手势结束再拍必空；改后台慢拖 2s、拖中 0.8s 快拍。
#   ③ 退出确认走真实返回键（keyevent 4，源码：播放页 Back=ExitOrCollapseList→ConfirmExit；再按 Back=取消）。
#   ④ 浮层点外关闭按实际方位：抽屉左贴→点右侧遮罩；设置面板贴底全宽→点上半屏遮罩。
#   ⑤ dump 一律带 timeout 防视频页挂死；证据补传 walk/*.xml（工作流同步加匹配式）。
# 必截状态：正常/加载/失败/退出确认/换台信息条/亮度HUD/音量HUD/极端数据(83台) × 横竖屏。
set -u
OUT=${1:-walk}
mkdir -p "$OUT"
SHOT() { adb shell screencap -p /sdcard/w.png && adb pull /sdcard/w.png "$OUT/$1.png" >/dev/null 2>&1 && echo "SHOT $1"; }

SIZE=$(adb shell wm size | grep -o '[0-9]*x[0-9]*' | tail -1)
W=${SIZE%x*}; H=${SIZE#*x}
CX=$((W/2)); CY=$((H/2))
echo "SCREEN ${W}x${H}"
echo "SCREEN ${W}x${H}" > "$OUT/screen.txt"

# —— 控制条按钮坐标（源码推算，1080x2400@420dpi） ——
# 竖屏：行1 [◀120 ▶272 | ☰504 ⚙656 ⟳808 锁960] y=2189；行2 音量 [−680 +915] y=2311（右对齐）
# 横屏：单行8钮 y=991 [◀120 ▶272 | ☰1346 ⚙1498 ⟳1650 锁1802 音量−1998 音量+2236]
P_BAR_Y=2189; P_VOL_Y=2311
L_BAR_Y=991

# tap_text：uiautomator 找文本控件中心点击（带 timeout 防挂死；重试2次）；失败返回1
tap_text() {
  local text="$1" try=1
  while [ $try -le 2 ]; do
    timeout 10 adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || { sleep 1; try=$((try+1)); continue; }
    local line
    line=$(timeout 6 adb shell cat /sdcard/ui.xml 2>/dev/null | tr '>' '>\n' | grep -F "text=\"$text\"" | head -1)
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

# dump_ui：控件树留证（带 timeout，视频页 uiautomator 可能挂死）
dump_ui() { timeout 12 adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1; timeout 8 adb shell cat /sdcard/ui.xml > "$OUT/$1" 2>/dev/null; return 0; }

sleep 3
SHOT 01-play-portrait

# —— 竖屏：控制条（点画面呼出，3s 窗口内快拍） ——
adb shell input tap "$CX" "$CY"; sleep 0.8
SHOT 02-controls-portrait

# —— 竖屏：抽屉（83台全量=极端数据）——坐标直点 ☰，不依赖 dump ——
adb shell input tap 504 $P_BAR_Y
sleep 2.5
SHOT 03-drawer-portrait
dump_ui ui-drawer-portrait.xml
adb shell input swipe 324 $((H*3/4)) 324 $((H/4)) 600; sleep 1.2
SHOT 03b-drawer-scrolled
# 点右侧遮罩关闭（抽屉左贴）
adb shell input tap $((W-60)) "$CY"; sleep 1.5
SHOT 03c-drawer-closed

# —— 竖屏：设置页（坐标直点 ⚙） ——
adb shell input tap "$CX" "$CY"; sleep 0.8
adb shell input tap 656 $P_BAR_Y
sleep 2
SHOT 04-settings-portrait
dump_ui ui-settings-portrait.xml
# 下半：进阶调优（面板贴底，面板内上滑）
adb shell input swipe "$CX" $((H*3/4)) "$CX" $((H/2)) 600; sleep 1.5
SHOT 05-settings-advanced
# 诊断日志页（三标签；dump 对面板可能可用，miss 则跳过不阻塞）
tap_text "崩溃日志" || true
sleep 1
SHOT 06-diagnostics
# 点上半屏遮罩关闭（面板贴底全宽）
adb shell input tap "$CX" $((H/5)); sleep 1.5
SHOT 06b-settings-closed

# —— 竖屏：换台信息条（左滑=上一台） ——
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 0.7
SHOT 11-switch-toast
sleep 9

# —— 竖屏：亮度 HUD（左半区慢拖，拖中快拍；ShowHud 700ms 自隐） ——
adb shell input swipe $((W/4)) $((H*2/3)) $((W/4)) $((H/3)) 2000 &
sleep 0.8
SHOT 12-hud-brightness
wait
sleep 1.2
SHOT 12b-hud-brightness-late
sleep 2

# —— 竖屏：音量 HUD（右半区慢拖，拖中快拍） ——
adb shell input swipe $((W*3/4)) $((H*2/3)) $((W*3/4)) $((H/3)) 2000 &
sleep 0.8
SHOT 13-hud-volume
wait
sleep 1.2
SHOT 13b-hud-volume-late
sleep 2

# —— 退出确认框（真实返回键：播放页 Back=ConfirmExit；再按 Back=取消） ——
adb shell input keyevent 4
sleep 1.5
SHOT 17-exit-dialog
dump_ui ui-exit-dialog.xml
adb shell input keyevent 4
sleep 1.5
SHOT 17b-exit-dialog-dismissed

# —— 横屏段：系统旋转口径（可靠），应用 OnConfigurationChanged 重建 ——
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1
sleep 4
SHOT 07-play-landscape
adb shell input tap "$CX" "$CY"; sleep 0.8
SHOT 08-controls-landscape

adb shell input tap 1346 $L_BAR_Y
sleep 2.5
SHOT 09-drawer-landscape
adb shell input tap $((W-60)) "$CY"; sleep 1.5

adb shell input tap "$CX" "$CY"; sleep 0.8
adb shell input tap 1498 $L_BAR_Y
sleep 2
SHOT 10-settings-landscape
adb shell input tap "$CX" $((H/5)); sleep 1.5

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
