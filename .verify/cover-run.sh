#!/bin/bash
# vc90 分支覆盖出账（coverlet 全局工具口径，对齐 2026-11-25 重测）
cd /root/workspace/taolive-work/taolive-2p0 || exit 9
export DOTNET_ROOT=/root/workspace/.local/dotnet10
export PATH="$DOTNET_ROOT:/root/workspace/.dotnet/tools:$PATH"
OUT=${1:-.verify/coverage-vc90.cobertura.xml}
LOG=${2:-.verify/cover-run.log}
coverlet src/TaoLive.Tests/bin/Debug/net10.0/TaoLive.Tests.dll \
  --target "$DOTNET_ROOT/dotnet" \
  --targetargs 'test src/TaoLive.Tests --nologo --blame --logger "console;verbosity=normal"' \
  --format cobertura \
  --output "$OUT" > "$LOG" 2>&1
echo "COVER_EXIT=$?" >> "$LOG"
