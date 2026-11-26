#!/bin/bash
# 全表 59 路完整摸排（老板令 2026-11-17：完整真——清单/分片/TS魔数/可解码/抽帧五级证据）
cd /root/workspace/taolive-work/taolive-2p0/probe20261126
python3 - <<'PY' > sources.txt
import re
src=open('../src/TaoLive.Foundation/FallbackSourceRegistry.cs').read()
for m in re.finditer(r'new\("([^"]+)",\s*"([^"]+)"(.*?)\)', src):
    n,u,rest=m.groups()
    obs='ObservationRank: true' in rest
    print(f"{'OBS' if obs else 'NORM'}|{n}|{u}")
PY
LEDGER=probe_ledger.tsv
echo -e "idx\tlevel\tname\turl\t_playlist\t_segbytes\t_tsmagic\t_codec\t_frame\tverdict\tmin\thand" > $LEDGER
n=0
while IFS='|' read -r LEVEL NAME URL; do
  n=$((n+1))
  (
    playlist="" ; segb=0 ; magic="no" ; codec="" ; frame="no" ; mins="" ; hand=""
    case "$NAME" in
      *"江苏"*) hand="jstv" ;;
      *"钱江"*|*cztv*) hand="cztv" ;;
      *"浙江"*) hand="cztv" ;;
      *"CCTV13新闻"*) hand="cztv" ;;
      *"CCTV4"*) hand="antik" ;;
      *"黑龙江"*) hand="hljtv" ;;
      *"东方"*) hand="bestv" ;;
      *"东南"*) hand="zohi" ;;
      *"内蒙古"*) hand="nmtv" ;;
      *"教科影视"*) hand="cztv" ;;
      *"深圳"*) hand="gmxw" ;;
      *"安徽"*) hand="gmxw" ;;
      *"广东"*) hand="gmxw" ;;
      *"山西"*) hand="gmxw" ;;
      *"新疆"*) hand="gmxw" ;;
      *"西藏"*) hand="gmxw" ;;
      *"湖北"*) hand="gmxw" ;;
      *"CCTV12社会与法"*) case "$URL" in *183.11*|*gmxw*) hand="gmxw";; esac ;;
      *"CCTV5体育"*) case "$URL" in *gmxw*) hand="gmxw";; esac ;;
    esac
    PL=$(curl -4 -m 15 -s "$URL")
    if echo "$PL" | head -1 | grep -q "EXTM3U"; then playlist="ok"; else playlist="no"; fi
    if [ "$playlist" = "ok" ]; then
      SEG=$(echo "$PL" | grep -E '\.ts' | tail -2 | head -1 | tr -d '\r')
      if [ -n "$SEG" ]; then
        BASE=$(echo "$URL" | grep -oE '.*/')
        case "$SEG" in http*) SURL="$SEG" ;; *) SURL="${BASE}${SEG}" ;; esac
        curl -4 -m 30 -s -o /tmp/seg_${n}.bin "$SURL"
        segb=$(stat -c%s /tmp/seg_${n}.bin 2>/dev/null || echo 0)
        if [ "$segb" -gt 10000 ] && [ "$(head -c 1 /tmp/seg_${n}.bin | od -A n -t x1 | tr -d ' ')" = "47" ]; then
          magic="yes"
          codec=$(ffprobe -v error -show_entries stream=codec_name -of csv=p=0 /tmp/seg_${n}.bin 2>/dev/null | head -2 | tr '\n' ',')
          if ffmpeg -v error -y -i /tmp/seg_${n}.bin -frames:v 1 -q:v 4 "frames/idx${n}_${hand}.jpg" 2>/dev/null; then
            frame="ok"; mins="min: $(ffprobe -v error -show_entries stream=width -of csv=p=0 /tmp/seg_${n}.bin 2>/dev/null | head -1)"
          else
            ffmpeg -v error -y -f mpegts -i /tmp/seg_${n}.bin -frames:v 1 -q:v 4 "frames/idx${n}_${hand}.jpg" 2>/dev/null && frame="ok(ts)" || frame="no"
          fi
        fi
      fi
    fi
    if [ "$magic" = "yes" ] && [ -n "$codec" ]; then verdict="REAL"; elif [ "$playlist" = "ok" ] && [ "$segb" -gt 10000 ]; then verdict="SUSPECT"; else verdict="DEAD"; fi
    echo -e "${n}\t${LEVEL}\t${NAME}\t${URL}\t${playlist}\t${segb}\t${magic}\t${codec}\t${frame}\t${verdict}\t${mins}\t${hand}" >> $LEDGER
    rm -f /tmp/seg_${n}.bin
  ) &
  # 并发 6，避免把好源探测限速误判死
  if [ $((n % 6)) -eq 0 ]; then wait; fi
done < sources.txt
wait
sort -t$'\t' -k1 -n $LEDGER -o $LEDGER
echo "PROBE_ALL_DONE $(date +%H:%M:%S)" >> $LEDGER
