#!/usr/bin/env bash
# TaoLive 手机版逐屏走查出图 v12（机型矩阵基线版，2026-11-28 老板令：ACE5 重点+全主力机型适配）
# v12 改法（机型矩阵三根治）：
#   ① ☰/⚙/控制条坐标不再写死 1080x2400 实测值——vc142 让位改 30dp、vc144 右组重排（列表、锁定、
#      全屏、设置）后 v8 实测坐标全部过期（vc149 云测实拍实锤：⚙810 点在「锁」上，抽屉/设置从未
#      打开，03~06 全拍错态）。自绘图标无文本 uiautomator 不可见，v12 改运行时实测：呼出控制条后
#      dump，取下半屏本应用 clickable 节点，右簇（cx>55%屏宽）最左=☰ 最右=⚙，行中心=BAR_Y；
#      实测失败回退按屏比例缩放（0.61W/0.96W/0.966H，vc149 云测 1080x2400 实拍标定）。
#   ② 控制条显隐是开关量：v11 open_drawer 先点画面「重显」，控制条已显示时这一下反而切没——
#      v12 改 show_bar 先判态（画面时刻=条独有文本），不可见才点，可见直接用。
#   ③ 抽屉滚动、HUD 手势 y 全部按屏比例换算，不再写死像素；其余沿用 v11（tr 断行根治、
#      fresh_snap 防陈旧、open_sub 状态机、诊断页签精确闭合）。
# 必截状态：正常/加载/失败/换台信息条/亮度HUD/音量HUD × 横竖屏；退后台态（vc112）。
set -u
OUT=${1:-walk}
mkdir -p "$OUT"
SHOT() { adb shell screencap -p /sdcard/w.png && adb pull /sdcard/w.png "$OUT/$1.png" >/dev/null 2>&1 && echo "SHOT $1"; }

SIZE=$(adb shell wm size | grep -o '[0-9]*x[0-9]*' | tail -1)
W=${SIZE%x*}; H=${SIZE#*x}
CX=$((W/2)); CY=$((H/2))
echo "SCREEN ${W}x${H}"
echo "SCREEN ${W}x${H}" > "$OUT/screen.txt"
ACT="com.taolive.phone/crc64b53a6f6c1e1300c1.MainActivity"

# —— 控制条按钮坐标（v12：运行时实测，回退按 vc149 实拍比例） ——
LIST_X=$((W*61/100)); GEAR_X=$((W*96/100)); BAR_Y=$((H*966/1000))

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

# fresh_snap：防陈旧 dump——先删旧文件再 dump，返回当前界面快照文本（dump 失败=空串）
fresh_snap() {
  adb shell rm -f /sdcard/ui.xml
  timeout 12 adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || return 0
  timeout 6 adb shell cat /sdcard/ui.xml 2>/dev/null
  return 0
}

# show_bar：确保控制条可见（画面时刻=条独有文本）。已显示不点（点了会切没），不可见才点画面
show_bar() { # $1=呼出x $2=呼出y
  local try=1
  while [ $try -le 3 ]; do
    if fresh_snap | grep -qF '画面时刻'; then
      echo "BAR visible (try $try)"
      return 0
    fi
    adb shell input tap "$1" "$2"; sleep 1
    try=$((try+1))
  done
  fresh_snap | grep -qF '画面时刻'
}

# measure_bar：实测控制条按钮（自绘无文本，按 clickable 节点几何找）
# 前提：控制条已显示（先 show_bar）。$1=呼出x $2=呼出y $3=当前方向屏宽 $4=当前方向屏高
# 产出全局 LIST_X（右簇最左=☰）、GEAR_X（右簇最右=⚙）、BAR_Y（按钮行中心y）；不足3节点保持回退值
measure_bar() {
  local mcx="$1" mcy="$2" mw="$3" mh="$4"
  show_bar "$mcx" "$mcy" || { echo "MEASURE skip: bar not visible"; return 1; }
  local nodes cx cy gx=0 lx=0 by=0 cnt=0 x1 y1 x2 y2
  nodes=$(fresh_snap | tr '>' '\n' | grep 'package="com.taolive.phone"' | grep 'clickable="true"' \
    | grep -oE 'bounds="\[[0-9]+,[0-9]+\]\[[0-9]+,[0-9]+\]"' | grep -oE '[0-9]+' | paste - - - -)
  while read -r x1 y1 x2 y2; do
    [ -n "$x1" ] && [ -n "$x2" ] && [ -n "$y1" ] && [ -n "$y2" ] || continue
    cx=$(( (x1 + x2) / 2 )); cy=$(( (y1 + y2) / 2 ))
    # 按钮行在底部 12% 且右半屏（左组 ◀▶ 在左缘，被 cx 闸排除）
    if [ "$cy" -gt $((mh*88/100)) ] && [ "$cx" -gt $((mw*55/100)) ]; then
      if [ "$cx" -gt "$gx" ]; then gx=$cx; fi
      if [ "$lx" -eq 0 ] || [ "$cx" -lt "$lx" ]; then lx=$cx; fi
      by=$cy; cnt=$((cnt+1))
    fi
  done << EOF
$nodes
EOF
  if [ "$cnt" -ge 3 ]; then
    LIST_X=$lx; GEAR_X=$gx; BAR_Y=$by
    echo "MEASURED list=$lx gear=$gx bar_y=$by (nodes=$cnt)"
    return 0
  fi
  echo "MEASURE fallback (nodes=$cnt) list=$LIST_X gear=$GEAR_X bar_y=$BAR_Y"
  return 1
}

# open_drawer：实测坐标点 ☰→fresh_snap 验证「全部」页签在，没开重试一次
open_drawer() { # $1=呼出x $2=呼出y $3=屏宽 $4=屏高（当前方向）
  local try=1 snap
  while [ $try -le 2 ]; do
    measure_bar "$1" "$2" "$3" "$4" || true
    adb shell input tap "$LIST_X" "$BAR_Y"
    sleep 2
    snap=$(fresh_snap)
    if echo "$snap" | grep -qF 'text="全部"'; then
      echo "DRAWER open (try $try)"
      return 0
    fi
    echo "DRAWER miss (try $try)"
    try=$((try+1))
  done
  return 1
}

# open_settings：实测坐标点 ⚙→验证「开机自启」在
open_settings() { # $1=呼出x $2=呼出y $3=屏宽 $4=屏高（当前方向）
  local try=1
  while [ $try -le 2 ]; do
    measure_bar "$1" "$2" "$3" "$4" || true
    adb shell input tap "$GEAR_X" "$BAR_Y"
    sleep 3
    if fresh_snap | grep -qF 'text="开机自启"'; then
      echo "SETTINGS open (try $try)"
      return 0
    fi
    echo "SETTINGS miss (try $try)"
    try=$((try+1))
  done
  return 1
}

# tap_text：uiautomator 找文本控件中心点击（fresh dump 防陈旧；重试2次）；失败可带坐标兜底
tap_text() {
  local text="$1" try=1 fx="${2:-}" fy="${3:-}" line nums
  while [ $try -le 2 ]; do
    line=$(fresh_snap | tr '>' '\n' | grep -F "text=\"$text\"" | head -1)
    if [ -n "$line" ]; then
      nums=$(echo "$line" | grep -oE 'bounds="\[[0-9]+,[0-9]+\]\[[0-9]+,[0-9]+\]"' | grep -oE '[0-9]+')
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

# dump_ui：落档用（防陈旧：先删再 dump；失败落空文件留痕）
dump_ui() { adb shell rm -f /sdcard/ui.xml; timeout 12 adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1; timeout 8 adb shell cat /sdcard/ui.xml > "$OUT/$1" 2>/dev/null; return 0; }

# open_sub：状态机式进子页（v10 ②）——先判态再动作，绝不盲按返回键
#   子页已开（verify 片段在快照里）→成功返回；
#   设置主页（text="开机自启" 在）→点入口行；
#   未知态（播放页/桌面/抽屉）→ensure_fg+重开设置面板。
# $1=入口文本 $2=入口兜底x $3=入口兜底y $4=子页验证 grep 片段（带 text=" 边界） $5=重试次数
open_sub() {
  local entry="$1" fx="$2" fy="$3" verify="$4" max="${5:-4}" try=1 snap
  while [ $try -le $max ]; do
    snap=$(fresh_snap)
    if echo "$snap" | grep -qF "$verify"; then
      echo "SUB $entry open (try $try)"
      return 0
    fi
    if echo "$snap" | grep -qF 'text="开机自启"'; then
      echo "SUB $entry: on settings main, tap entry (try $try)"
      tap_text "$entry" "$fx" "$fy" || true
      sleep 1.5
    else
      echo "SUB $entry: unknown state, reopen settings (try $try)"
      ensure_fg
      open_settings "$CX" "$CY" "$W" "$H" || true
    fi
    try=$((try+1))
  done
  echo "SUB $entry FAILED after $max tries"
  return 1
}

sleep 3
SHOT 01-play-portrait

# —— 竖屏：控制条（show_bar 判态呼出，快拍） ——
show_bar "$CX" "$CY"
sleep 0.3
SHOT 02-controls-portrait

# —— 竖屏：抽屉（83台全量=极端数据）——实测 ☰ 坐标+验证重试 ——
open_drawer "$CX" "$CY" "$W" "$H"
SHOT 03-drawer-portrait
dump_ui ui-drawer-portrait.xml
adb shell input swipe $((W*3/10)) $((H*3/4)) $((W*3/10)) $((H/4)) 600; sleep 1.2
SHOT 03b-drawer-scrolled
adb shell input keyevent 4; sleep 1.5
SHOT 03c-drawer-closed
ensure_fg

# —— 竖屏：设置页（实测 ⚙ 坐标，等 3s 让面板入场动画走稳） ——
open_settings "$CX" "$CY" "$W" "$H"
sleep 0.5
SHOT 04-settings-portrait
dump_ui ui-settings-portrait.xml
# 进阶调优子页（v10：状态机进页，verify=旋钮行独有文本「自愈门槛」）
open_sub "进阶调优" 425 792 'text="自愈门槛' 4
sleep 1
SHOT 05-settings-advanced
dump_ui ui-advanced-portrait.xml
adb shell input keyevent 4; sleep 1.5   # 收进阶子页回设置（open_sub 验证过子页已开，返回键安全）
ensure_fg
# 诊断日志页（v10：verify=text="运行台账" 精确闭合，副标题整串不再误命中）
open_sub "诊断日志" 867 651 'text="运行台账"' 4
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
adb shell input keyevent 4; sleep 1.5   # 收诊断页回设置
SHOT 06b-settings-closed
ensure_fg

# —— 竖屏：换台信息条（左滑=上一台） ——
adb shell input swipe $((W*4/5)) "$CY" $((W/5)) "$CY" 300
sleep 0.7
SHOT 11-switch-toast
sleep 9

# —— 竖屏：亮度 HUD（左半区慢拖 3s，拖中三连拍；y 按屏比例） ——
adb shell input swipe $((W/4)) $((H*3/5)) $((W/4)) $((H*2/5)) 3000 &
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
adb shell input swipe $((W*3/4)) $((H*3/5)) $((W*3/4)) $((H*2/5)) 3000 &
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

# —— 返回退后台（vc112 拍板：播放页 Back=停播攒流退后台） ——
adb shell input keyevent 4
sleep 1.5
SHOT 17-back-background
dump_ui ui-back-background.xml
ensure_fg
sleep 1
SHOT 17b-recovered-play

# —— 横屏段：系统旋转口径（可靠），应用 OnConfigurationChanged 重建 ——
# 横屏当前方向：屏宽=H、屏高=W（物理竖屏 W<H）
L_CX=$((H/2)); L_CY=$((W/2))
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1
sleep 4
ensure_fg
SHOT 07-play-landscape
adb shell input tap "$L_CX" "$L_CY"; sleep 0.8
SHOT 08-controls-landscape

open_drawer "$L_CX" "$L_CY" "$H" "$W"
SHOT 09-drawer-landscape
adb shell input keyevent 4; sleep 1.5   # 关抽屉（返回键）
ensure_fg

open_settings "$L_CX" "$L_CY" "$H" "$W"
sleep 0.5
SHOT 10-settings-landscape
dump_ui ui-settings-landscape.xml
# v10 ④：横屏设置向下滚动一屏，定论入口组卡片「滚动可达（折叠）还是真缺失」
adb shell input swipe "$L_CX" $((L_CY+200)) "$L_CX" $((L_CY-400)) 500; sleep 1.2
SHOT 10b-settings-landscape-scrolled
dump_ui ui-settings-landscape-scrolled.xml
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
