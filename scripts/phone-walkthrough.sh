#!/usr/bin/env bash
# TaoLive 手机版逐屏走查出图 v9（制度：视觉模型比对与评分制度 2026-11-26）
# v9 改法（对照 v8 实拍复盘，run 46080373435 证据包）：
#   ① 05 进阶调优：v8 拍到的是诊断日志页——设置面板入场动画未稳，tap_text dump 的 bounds 落指时
#      内容已下移约 247px，点进调优坐标落到诊断日志行。v9 点「进阶调优」后 dump 验证该页独有文本
#      「自愈门槛」（SettingsUi.cs 进阶调优面板旋钮行），不对则返回键收掉误开页重试（仿 open_drawer）。
#   ② 06 诊断页签：v8 tap_text「崩溃日志」在设置页无独立文本（只有入口副标题整串
#      「运行台账 · 崩溃日志 · 自诊断」），MISS 后坐标兜底落空。v9 改先点入口行「诊断日志」
#      （独立文本，SettingsUi.cs:631），进诊断页后三页签为独立 TextView，再分别点
#      「崩溃日志」「自诊断」页签各拍一张（dump 验证页签文本在才点）。
#   ③ 10 横屏设置：v8 疑似入口组卡片不可见，加 dump ui-settings-landscape.xml 核实
#      （未渲染/滚动折叠/真缺陷，v9 定论）。
#   ④ 设置面板打开后等待 2→3s，让入场动画走稳再 dump+点按，从根上压时序竞态。
# v8 改法（对照 v7 实拍复盘，vc121 改版后按钮盒 44dp、两端让位、信息三行 → 旧坐标全落空）：
#   坐标全部按 v7 实拍截图裁剪测量更新（1080x2400 竖屏 / 2400x1080 横屏）：
#   竖屏按钮行 y 2189→2334；☰ x 504→710；⚙ x 656→810（实测 ⏮70 ⏭170 ☰710 ⚙810 ⛶910 🔒1010）。
#   横屏按钮行 y 991→1016；☰ x 1346→2025；⚙ x 1498→2131（实测 ☰2025 ⚙2131 ⛶2238 🔒2344）。
# v7 改法：ensure_fg 每段核对前台；抽屉打开加 dump 验证重试；17 改拍退后台真实态。
# v6 改法：浮层关闭一律 keyevent 4；HUD 拖 3s 三连拍；03c 返回键关抽屉。
# 必截状态：正常/加载/失败/换台信息条/亮度HUD/音量HUD/极端数据(83台) × 横竖屏；退后台态（vc112）。
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
P_BAR_Y=2334; P_VOL_Y=2311
L_BAR_Y=1016
ACT="com.taolive.phone/crc64b53a6f6c1e1300c1.MainActivity"

# ensure_fg：vc112 返回=退后台，走查任何一步都可能把应用按回桌面；每段开始前核对并拉回
ensure_fg() {
  local resumed
  resumed=$(adb shell dumpsys activity activities 2>/dev/null | grep -m1 'ResumedActivity' || true)
  case "$resumed" in
    *taolive*) echo "FG ok" ;;
    *)
      echo "FG lost -> relaunch"
      adb shell am start -n "$ACT" >/dev/null 2>&1
      sleep 4
      ;;
  esac
}

# open_drawer：重显控制条→点 ☰→dump 验证「全部」页签在，没开重试一次
open_drawer() { # $1=☰x  $2=☰y
  local try=1
  while [ $try -le 2 ]; do
    adb shell input tap "$CX" "$CY"; sleep 0.8
    adb shell input tap "$1" "$2"
    sleep 2
    if timeout 10 adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 \
       && timeout 6 adb shell cat /sdcard/ui.xml 2>/dev/null | grep -qF 'text="全部"'; then
      echo "DRAWER open (try $try)"
      return 0
    fi
    echo "DRAWER miss (try $try)"
    try=$((try+1))
  done
  return 1
}

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

# open_sub：点子页入口→dump 验证该页独有文本在；不对则返回键收掉误开页重试（v9 ①，仿 open_drawer）
open_sub() { # $1=入口文本 $2=入口兜底x $3=入口兜底y $4=子页独有验证文本 $5=重试次数(默认3)
  local entry="$1" fx="$2" fy="$3" verify="$4" max="${5:-3}" try=1
  while [ $try -le $max ]; do
    tap_text "$entry" "$fx" "$fy" || true
    sleep 1.5
    if timeout 10 adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 \
       && timeout 6 adb shell cat /sdcard/ui.xml 2>/dev/null | grep -qF "$verify"; then
      echo "SUB $entry open (try $try, verify=$verify)"
      return 0
    fi
    echo "SUB $entry miss (try $try, verify=$verify) -> back+retry"
    adb shell input keyevent 4; sleep 1.5
    try=$((try+1))
  done
  echo "SUB $entry FAILED after $max tries"
  return 1
}

sleep 3
SHOT 01-play-portrait

# —— 竖屏：控制条（点画面呼出，3s 窗口内快拍） ——
adb shell input tap "$CX" "$CY"; sleep 0.8
SHOT 02-controls-portrait

# —— 竖屏：抽屉（83台全量=极端数据）——v7：open_drawer 带验证重试 ——
open_drawer 710 $P_BAR_Y
SHOT 03-drawer-portrait
dump_ui ui-drawer-portrait.xml
adb shell input swipe 324 $((H*3/4)) 324 $((H/4)) 600; sleep 1.2
SHOT 03b-drawer-scrolled
# 关抽屉：返回键（抽屉开着=收抽屉；万一没开，vc112 返回=退后台，下一段 ensure_fg 兜底拉回）
adb shell input keyevent 4; sleep 1.5
SHOT 03c-drawer-closed
ensure_fg

# —— 竖屏：设置页（坐标直点 ⚙；v9 ④ 等待 3s 让面板入场动画走稳） ——
adb shell input tap "$CX" "$CY"; sleep 0.8
adb shell input tap 810 $P_BAR_Y
sleep 3
SHOT 04-settings-portrait
dump_ui ui-settings-portrait.xml
# 进阶调优子页（v9 ①：点后 dump 验证「自愈门槛」，不对重试）
open_sub "进阶调优" 425 792 "自愈门槛" 3
sleep 1
SHOT 05-settings-advanced
dump_ui ui-advanced-portrait.xml
adb shell input keyevent 4; sleep 1.5   # 收进阶子页回设置
# 诊断日志页（v9 ②：先点入口行「诊断日志」，进页后三页签为独立文本，逐页签点拍）
open_sub "诊断日志" 867 651 "崩溃日志" 3
sleep 1
SHOT 06-diagnostics            # 默认页签=运行台账
dump_ui ui-diagnostics-ledger.xml
tap_text "崩溃日志" || true
sleep 1
SHOT 06c-diagnostics-crash
dump_ui ui-diagnostics-crash.xml
tap_text "自诊断" || true
sleep 1.5
SHOT 06d-diagnostics-selfcheck
dump_ui ui-diagnostics-selfcheck.xml
adb shell input keyevent 4; sleep 1.5   # 收诊断页（返回键）
SHOT 06b-settings-closed
ensure_fg

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

# —— 返回退后台（vc112 拍板：播放页 Back=停播攒流退后台，确认弹窗已删；v6 在等不存在的弹窗） ——
adb shell input keyevent 4
sleep 1.5
SHOT 17-back-background
dump_ui ui-back-background.xml
ensure_fg
sleep 1
SHOT 17b-recovered-play

# —— 横屏段：系统旋转口径（可靠），应用 OnConfigurationChanged 重建 ——
L_CX=$((H/2)); L_CY=$((W/2))
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1
sleep 4
ensure_fg
SHOT 07-play-landscape
adb shell input tap "$L_CX" "$L_CY"; sleep 0.8
SHOT 08-controls-landscape

open_drawer 2025 $L_BAR_Y
SHOT 09-drawer-landscape
adb shell input keyevent 4; sleep 1.5   # 关抽屉（返回键）
ensure_fg

adb shell input tap "$L_CX" "$L_CY"; sleep 0.8
adb shell input tap 2131 $L_BAR_Y
sleep 3
SHOT 10-settings-landscape
dump_ui ui-settings-landscape.xml       # v9 ③：横屏设置 dump，核实入口组卡片是否缺失
adb shell input keyevent 4; sleep 1.5   # 收设置（返回键）
ensure_fg

# 转回竖屏
adb shell settings put system user_rotation 0
sleep 4
ensure_fg
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
ensure_fg
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 1
SHOT 15-loading
sleep 12
SHOT 16-recovered-play

adb logcat -d > "$OUT/walkthrough-logcat.txt" 2>/dev/null || true
echo "WALKTHROUGH_DONE"
