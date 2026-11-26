#!/bin/bash
# vc89 统一验证：全测 + 壳层编译核验（统一验证令：全部落码后一次跑）
cd /root/workspace/taolive-work/taolive-2p0 || exit 9
DOTNET=/root/workspace/.local/dotnet10/dotnet
LOG=.verify/unified-run2.log
echo "=== [1/2] dotnet test (net10) $(date -u +%H:%M:%S)" > "$LOG"
$DOTNET test src/TaoLive.Tests --nologo 2>&1 | tail -15
echo "TEST_EXIT=${PIPESTATUS[0]}" >> "$LOG"
echo "=== [2/2] dotnet build Tv (net10-android) $(date -u +%H:%M:%S)" >> "$LOG"
$DOTNET build src/TaoLive.Tv -p:AndroidSdkDirectory=/root/workspace/.local/android-sdk -p:JavaSdkDirectory=/root/workspace/.local/jdk-21.0.13+9 --nologo 2>&1 | tail -12
echo "BUILD_EXIT=${PIPESTATUS[0]}" >> "$LOG"
echo "=== DONE $(date -u +%H:%M:%S)" >> "$LOG"
