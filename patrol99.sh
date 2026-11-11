#!/bin/bash
# v26(1.0 正式版)源码 99 轮巡逻 v3（口径：每轮核查当轮最新源码；有修复落码 → 重建全测 → 基线重置 → 继续核验）
cd /root/workspace/taolive-b20-wt
export DOTNET_ROOT=/root/workspace/.local/dotnet9
DOTNET=/root/workspace/.local/dotnet9/dotnet
OUT=patrol_v25_99.md
BASE=/tmp/patrol99_baseline_v2
FILES="src/TaoLive.BLL/ChannelPaceLedger.cs src/TaoLive.BLL/WarmupPolicy.cs src/TaoLive.BLL/YspCkey.cs src/TaoLive.BLL/StreamResolver.cs src/TaoLive.UI.Android.Tv/Shared/LiveVideoHost.cs src/TaoLive.UI.Android.Tv/Shared/HotStandbyKeeper.cs src/TaoLive.UI.Android.Tv/MainActivity.cs tests/TaoLive.Tests/Program.cs"

echo "# v26(1.0 正式版)源码 99 轮巡逻记录（2026-11-11，基于最新源码，v3 口径：版本锚点=26/1.0）" > "$OUT"
echo "" >> "$OUT"
echo "口径：每轮核查当轮最新源码 ①九层全测 ②v25 补丁锚点 ③源码指纹与上轮比对。修复落码=源码指纹变化：该轮标注「修复」，重建全测后继续核验，基线随修复推进。" >> "$OUT"
echo "" >> "$OUT"
echo "| 轮次 | 全测 | 锚点 | 源码 | 结论 |" >> "$OUT"
echo "|---|---|---|---|---|" >> "$OUT"

anchors=(
  "src/TaoLive.BLL/ChannelPaceLedger.cs:class ChannelPaceLedger"
  "src/TaoLive.BLL/ChannelPaceLedger.cs:public bool TryAcquireGate"
  "src/TaoLive.BLL/ChannelPaceLedger.cs:public static double StableJitter"
  "src/TaoLive.BLL/ChannelPaceLedger.cs:public void RetainOnly"
  "src/TaoLive.BLL/WarmupPolicy.cs:ConcurrentDictionary<string, (long UntilMs, int Streak)>"
  "src/TaoLive.BLL/YspCkey.cs:public void ClearCooldowns"
  "src/TaoLive.BLL/YspCkey.cs:private readonly object _lock"
  "src/TaoLive.BLL/StreamResolver.cs:private async Task<string?> FetchSourceTableAsync"
  "src/TaoLive.BLL/StreamResolver.cs:public async Task<(bool Ok, bool Rejected)> ProbeHotStandbyAsync"
  "src/TaoLive.BLL/StreamResolver.cs:public long TicketRemainingCooldownMs"
  "src/TaoLive.BLL/StreamResolver.cs:public void ClearCooldowns"
  "src/TaoLive.UI.Android.Tv/Shared/LiveVideoHost.cs:private int _inventoryRunning"
  "src/TaoLive.UI.Android.Tv/Shared/LiveVideoHost.cs:private async Task<bool> TryHotStandbyAsync"
  "src/TaoLive.UI.Android.Tv/Shared/LiveVideoHost.cs:public Func<Task<(string Url, IReadOnlyList<(string Key, string Value)> Options)?>?>? HotStandbySwitch"
  "src/TaoLive.UI.Android.Tv/Shared/HotStandbyKeeper.cs:public class HotStandbyKeeper"
  "src/TaoLive.UI.Android.Tv/Shared/HotStandbyKeeper.cs:public async Task<(string Url, List<(string Key, string Value)> Options)?> TrySwitchAsync"
  "src/TaoLive.UI.Android.Tv/MainActivity.cs:private void TriggerScreenPrecheck"
  "src/TaoLive.UI.Android.Tv/MainActivity.cs:private void DrainPrefetchQueue"
  "src/TaoLive.UI.Android.Tv/MainActivity.cs:private void RegisterEnvReceiver"
  "src/TaoLive.UI.Android.Tv/MainActivity.cs:private readonly Dictionary<string, long> _deadUntilMs"
  "src/TaoLive.UI.Android.Tv/MainActivity.cs:_pace.TryAcquireGate"
  "src/TaoLive.UI.Android.Tv/MainActivity.cs:_hotStandby?.Start(seq"
)

total_fail=0
for i in $(seq 1 99); do
  fail=0
  notes=()
  cur=$(cat $FILES | sha256sum | cut -d' ' -f1)
  label="最新"
  # 源码指纹与上轮比对：变化=有修复落码 → 重建全测（跑最新源码），基线推进
  if [ -f "$BASE" ] && [ "$cur" != "$(cat $BASE)" ]; then
    label="修复"
    $DOTNET build tests/TaoLive.Tests/TaoLive.Tests.csproj -v q --nologo > /tmp/p99_build.log 2>&1 || { fail=1; notes+=("修复后全测重建编译失败"); }
    echo "$cur" > "$BASE"
  elif [ ! -f "$BASE" ]; then
    echo "$cur" > "$BASE"
  fi
  # ① 九层全测（跑最新构建产物）
  if $DOTNET tests/TaoLive.Tests/bin/Debug/net9.0/TaoLive.Tests.dll > /tmp/p99_run.log 2>&1 && tail -1 /tmp/p99_run.log | grep -q "0 FAIL"; then
    PASSN=$(tail -1 /tmp/p99_run.log | grep -oE "[0-9]+ PASS / 0 FAIL" | cut -d' ' -f1)
    [ -z "$PASSN" ] && PASSN="?"
  else
    fail=1; PASSN="红"; notes+=("九层全测非全绿")
  fi
  # ② v25 补丁锚点（对当前最新源码逐一 grep 原文）
  anchor_bad=0
  for a in "${anchors[@]}"; do
    f="${a%%:*}"; pat="${a#*:}"
    grep -qF "$pat" "$f" 2>/dev/null || { fail=1; anchor_bad=1; notes+=("锚点缺失:$pat"); }
  done
  [ "$anchor_bad" -eq 0 ] && ANCH="22/22" || ANCH="缺失"
  grep -q "<ApplicationVersion>26</ApplicationVersion>" src/TaoLive.UI.Android.Tv/TaoLive.UI.Android.Tv.csproj || { fail=1; notes+=("versionCode≠26"); }
  grep -q "<ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>" src/TaoLive.UI.Android.Tv/TaoLive.UI.Android.Tv.csproj || { fail=1; notes+=("displayVersion≠1.0"); }
  if [ "$fail" -eq 0 ]; then
    echo "| $i/99 | ${PASSN}/0 | ${ANCH} | ${label} | ✅ |" >> "$OUT"
  else
    echo "| $i/99 | ${PASSN} | ${ANCH} | ${label} | ❌ ${notes[*]} |" >> "$OUT"
    total_fail=$((total_fail+1))
  fi
done
echo "" >> "$OUT"
if [ "$total_fail" -eq 0 ]; then
  echo "## 结论：99 轮全部通过，问题数清 0（每轮均核查当轮最新源码；修复轮次自动重建全测，基线随修复推进）。" >> "$OUT"
else
  echo "## 结论：$total_fail 轮有问题，未清 0，逐轮明细见上表。" >> "$OUT"
fi
