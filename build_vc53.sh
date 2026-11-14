#!/bin/bash
# vc53 出包一键脚本（幂等、防打断；2026-11-14 支援班口径，成功链三验）
# 主口径=默认 trim+AOT（OOM 137 重试即过，前台 10:44 实证）；备用=interpreter（OOM 不退时）
# 环境重启后：内存盘 /dev/shm/tlb 与 /tmp 会被清空，本脚本自动重建
SRC=/root/workspace/taolive-10-build
BLD=/dev/shm/tlb
export DOTNET=/root/workspace/.local/dotnet9/dotnet
export DOTNET_ROOT=/root/workspace/.local/dotnet9
export PATH=$DOTNET_ROOT:$PATH

# 1. 重建内存盘树（若缺）
if [ ! -d "$BLD/src" ]; then
  mkdir -p $BLD
  python3 - << 'EOF'
import shutil, os
dst='/dev/shm/tlb'
skip_dirs={'obj','bin','pkg-v52','pkg-v53','.git','dist','cloud-verify-vc49','cloud-forensics-round14','cloud-forensics-round15','cloud-forensics-round15b'}
skip_ext=('.log','.zip','.md')
n=0
for root,dirs,files in os.walk('.'):
    dirs[:] = [d for d in dirs if d not in skip_dirs]
    rel=os.path.relpath(root,'.')
    for f in files:
        if f.endswith(skip_ext) or f.startswith('.nfs'): continue
        if rel=='.': continue
        s=os.path.join(root,f); d=os.path.join(dst,rel,f)
        os.makedirs(os.path.dirname(d), exist_ok=True)
        shutil.copy2(s,d); n+=1
print('copied', n)
EOF
fi
cd $BLD

# 2. restore 三步序列（闭包互踩根治：Tv 显式 TFM restore 会把 BLL/Model assets 污染为 android TFM，
#    必须按 Tv→BLL→Model 顺序，BLL/Model 用各自 TFM 修回）
$DOTNET restore src/TaoLive.UI.Android.Tv/TaoLive.UI.Android.Tv.csproj -p:Configuration=Release -p:TargetFramework=net9.0-android35.0 > tvr.log 2>&1 || echo TV_RESTORE_FAIL
$DOTNET restore src/TaoLive.BLL/TaoLive.BLL.csproj -p:Configuration=Release -p:TargetFramework=net8.0 > blr.log 2>&1 || echo BLL_RESTORE_FAIL
$DOTNET restore src/TaoLive.Model/TaoLive.Model.csproj -p:Configuration=Release -p:TargetFramework=net8.0 > mdr.log 2>&1 || echo MODEL_RESTORE_FAIL
# 图谱验证：三工程各自 TFM 必须在位
python3 -c "
import json
bad=0
for p,tf in [('TaoLive.BLL','net8.0'),('TaoLive.Model','net8.0'),('TaoLive.UI.Android.Tv','net9.0-android35.0')]:
    d=json.load(open(f'src/{p}/obj/project.assets.json'))
    ok=tf in d['project']['frameworks']
    print(p,'OK' if ok else 'BAD', d['project']['frameworks'])
    bad += 0 if ok else 1
exit(bad)" || { echo ASSETS_MISMATCH; exit 1; }

# 3. publish（默认 trim+AOT 口径；OOM 137 重试一次；再败换 interpreter 备用口径）
PUB_ARGS="-c Release -f net9.0-android35.0 --no-restore -p:AndroidSdkDirectory=/root/workspace/.local/android-sdk -p:JavaSdkDirectory=/root/workspace/.local/jdk-21.0.13+9"
$DOTNET publish src/TaoLive.UI.Android.Tv $PUB_ARGS > publish.log 2>&1
E1=$?
if [ $E1 -ne 0 ] && grep -q "code 137" publish.log; then
  echo "OOM137_RETRY"
  $DOTNET publish src/TaoLive.UI.Android.Tv $PUB_ARGS > publish.log 2>&1
  E1=$?
fi
if [ $E1 -ne 0 ]; then
  echo "FALLBACK_INTERPRETER"
  $DOTNET publish src/TaoLive.UI.Android.Tv $PUB_ARGS -p:PublishTrimmed=false -p:RunAOTCompilation=false -p:AndroidUseInterpreter=true > publish.log 2>&1
  E1=$?
fi
echo "PUBLISH_EXIT=$E1" | tee -a publish.log
find src/TaoLive.UI.Android.Tv/bin -name "*.apk" | head -3
