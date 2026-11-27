#!/usr/bin/env bash
# TaoLive 手机版逐屏走查出图 v10（制度：视觉模型比对与评分制度 2026-11-26）
# v10 改法（对照 v9 实拍复盘，run 46082366938 证据包）：
#   ① 根因：uiautomator dump 在面板动画期超时失败时，/sdcard/ui.xml 残留上一次内容——v9 设置页
#      dump 实为抽屉陈旧文件，tap_text「进阶调优」据此 MISS，open_sub 盲按返回键三次把应用退后台，
#      05/06/06c/06d 四张全拍桌面。v10 一切 dump 前 rm -f /sdcard/ui.xml，文件不存在=dump 失败按
#      miss 处理，陈旧文件不再可能。
#   ② open_sub 改状态机：先 dump 判当前态——子页已开（验证文本在）→成功；设置主页（开机自启在）
#      →点入口；未知态（播放页/桌面/抽屉）→ensure_fg+重开设置。绝不盲按返回键（vc112 返回=退后台）。
#   ③ 诊断页 verify 改带引号边界的 text="运行台账" 精确匹配——设置主页副标题整串
#      「运行台账 · 崩溃日志 · 自诊断」不再误命中（v9 ②遗留坑）。
#   ④ 横屏设置段加滚动后补拍 10b+dump：v9 实拍+dump 实证横屏设置入口组卡片不可见（开关组外无
#      入口文本），v10 滚动一屏定论「滚动可达（折叠）还是真缺失」。
# v9 改法：进阶调优点后 dump 验证独有文本「自愈门槛」；诊断页先点入口行再逐页签点拍；横屏设置加 dump。
# v8 改法：坐标按 v7 实拍全量更新（竖屏 y2334/☰710/⚙810；横屏 y1016/☰2025/⚙2131）。
# v7 改法：ensure_fg 每段核对前台；抽屉打开 dump 验证重试；17 改拍退后台真实态。
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

# open_drawer：重显控制条→点 ☰→fresh_snap 验证「全部」页签在，没开重试一次
open_drawer() { # $1=☰x  $2=☰y
  local try=1 snap
  while [ $try -le 2 ]; do
    adb shell input tap "$CX" "$CY"; sleep 0.8
    adb shell input tap "$1" "$2"
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

# tap_text：uiautomator 找文本控件中心点击（fresh dump 防陈旧；重试2次）；失败可带坐标兜底
tap_text() {
  local text="$1" try=1 fx="${2:-}" fy="${3:-}" line nums
  while [ $try -le 2 ]; do
    line=$(fresh_snap | tr '>' '>\n' | grep -F "text=\"$text\"" | head -1)
    if [ -n "$line" ]; then
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
      adb shell input tap "$CX" "$CY"; sleep 0.8
      adb shell input tap 810 $P_BAR_Y
      sleep 3
    fi
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

# —— 竖屏：抽屉（83台全量=极端数据）——open_drawer 带验证重试 ——
open_drawer 710 $P_BAR_Y
SHOT 03-drawer-portrait
dump_ui ui-drawer-portrait.xml
adb shell input swipe 324 $((H*3/4)) 324 $((H/4)) 600; sleep 1.2
SHOT 03b-drawer-scrolled
adb shell input keyevent 4; sleep 1.5
SHOT 03c-drawer-closed
ensure_fg

# —— 竖屏：设置页（坐标直点 ⚙，等 3s 让面板入场动画走稳） ——
adb shell input tap "$CX" "$CY"; sleep 0.8
adb shell input tap 810 $P_BAR_Y
sleep 3
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

# —— 竖屏：亮度 HUD（左半区慢拖 3s，拖中三连拍） ——
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

# —— 返回退后台（vc112 拍板：播放页 Back=停播攒流退后台） ——
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
