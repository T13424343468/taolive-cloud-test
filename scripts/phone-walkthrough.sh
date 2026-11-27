#!/usr/bin/env bash
# TaoLive 手机版逐屏走查出图 v6（制度：视觉模型比对与评分制度 2026-11-26）
# v6 改法（对照 v5 实拍缺陷，vc108 走查复盘）：
#   ① 浮层关闭一律 keyevent 4（返回键）——v5「点面板外」坐标实测点在面板内（设置面板顶部贴靠非贴底，
#      (540,480) 落在「源订阅」行上；抽屉全屏后无面板外），设置/抽屉从未被脚本关上（00-back 拍到面板开着的根因）。
#   ② 05 进阶调优改直点「进阶调优」（v5 只上滑 300px 没滚出内容，且从未点过入口；坐标自 ui-settings-portrait.xml 实证）。
#   ③ 06 崩溃日志 tap_text 加坐标兜底 (867,651)（同上实证；uiautomator 对视频页偶发超时）。
#   ④ HUD 拖长到 3s + 拖中三连拍（0.9/1.5/2.1s）+ 落指 0.3s 内补拍——v5 单拍 0.8s 疑时机错过（连续两版未出图）。
#   ⑤ 03c 抽屉关闭改 keyevent 4（vc109 定宽令：竖屏全屏无遮罩可点；横屏 480dp 定宽后返回键同样可靠）。
# v5 改法：横屏坐标转置；HUD 起终点改视频带内；设置滚动起点改面板内容区。
# v3 改法：控制条按钮源码推算坐标直点；HUD 手势进行中快拍；退出确认走真实返回键；dump 带 timeout；证据补传 walk/*.xml。
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
P_BAR_Y=2189; P_VOL_Y=2311
L_BAR_Y=991

# tap_text：uiautomator 找文本控件中心点击（带 timeout 防挂死；重试2次）；失败可带坐标兜底
tap_text() {
  local text="$1" try=1 fx="${2:-}" fy="${3:-}"
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
  if [ -n "$fx" ] && [ -n "$fy" ]; then
    adb shell input tap "$fx" "$fy"
    echo "TAP_TEXT FALLBACK $text @ $fx,$fy"
    return 0
  fi
  echo "TAP_TEXT MISS $text"
  return 1
}

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
# 关抽屉：返回键（v6：竖屏全屏无面板外可点；源码 Back=ExitOrCollapseList 先收抽屉）
adb shell input keyevent 4; sleep 1.5
SHOT 03c-drawer-closed

# —— 竖屏：设置页（坐标直点 ⚙） ——
adb shell input tap "$CX" "$CY"; sleep 0.8
adb shell input tap 656 $P_BAR_Y
sleep 2
SHOT 04-settings-portrait
dump_ui ui-settings-portrait.xml
# 进阶调优子页（v6：直点入口，坐标兜底自 ui-settings-portrait.xml 实证 [285,724][565,860]）
tap_text "进阶调优" 425 792 || true
sleep 1.5
SHOT 05-settings-advanced
adb shell input keyevent 4; sleep 1.2   # 收进阶子页回设置
# 诊断日志页（三标签；tap_text 坐标兜底自实证 [727,583][1007,719]）
tap_text "崩溃日志" 867 651 || true
sleep 1
SHOT 06-diagnostics
adb shell input keyevent 4; sleep 1.5   # 收诊断页（v6：返回键，v5 点「面板外」实测点在面板内）
SHOT 06b-settings-closed

# —— 竖屏：换台信息条（左滑=上一台） ——
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 0.7
SHOT 11-switch-toast
sleep 9

# —— 竖屏：亮度 HUD（左半区慢拖 3s，拖中三连拍；ShowHud 700ms 自隐；v6 加密采样防时机错过） ——
adb shell input swipe $((W/4)) 1450 $((W/4)) 950 3000 &
sleep 0.9
SHOT 12-hud-brightness
sleep 0.6
SHOT 12c-hud-brightness-mid
sleep 0.6
SHOT 12d-hud-brightness-mid2
wait
sleep 0.3
SHOT 12b-hud-brightness-late
sleep 2

# —— 竖屏：音量 HUD（右半区慢拖 3s，拖中三连拍） ——
adb shell input swipe $((W*3/4)) 1450 $((W*3/4)) 950 3000 &
sleep 0.9
SHOT 13-hud-volume
sleep 0.6
SHOT 13c-hud-volume-mid
sleep 0.6
SHOT 13d-hud-volume-mid2
wait
sleep 0.3
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
L_CX=$((H/2)); L_CY=$((W/2))
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1
sleep 4
SHOT 07-play-landscape
adb shell input tap "$L_CX" "$L_CY"; sleep 0.8
SHOT 08-controls-landscape

adb shell input tap 1346 $L_BAR_Y
sleep 2.5
SHOT 09-drawer-landscape
adb shell input keyevent 4; sleep 1.5   # 关抽屉（v6：返回键）

adb shell input tap "$L_CX" "$L_CY"; sleep 0.8
adb shell input tap 1498 $L_BAR_Y
sleep 2
SHOT 10-settings-landscape
adb shell input keyevent 4; sleep 1.5   # 收设置（v6：返回键，v5 横屏「点外」同样点在面板内）

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
