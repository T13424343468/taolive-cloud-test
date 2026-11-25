# tools/ —— 出包核验脚本（总镜子配套件 v45/v46/v49）

出包前三道脚本闸，任何一道非零退出即不放行：

```bash
# v45 包名/版本核验：versionCode/versionName/包名与交付记录逐字对
tools/verify_release_pkg.sh out.apk --code 57 --version 2.0.0

# v46 特征关扫描：信息行不在 / 标签 TaoLive 纯英文 / 横幅随包；报告随包
tools/verify_feature_gate.sh out.apk

# v49 基线对照：出包区间逐文件 hash 清单（工作树脏=拒绝）；清单随包归档
tools/verify_release_baseline.sh <base_commit> [head_commit]
```

- v45 报告落在 APK 旁：`<apk>.v45-report.txt`。
- v46 禁词表在脚本头部 `BANNED_PATTERNS`，新增禁词改那里，不临时拼。
- v49 清单命名 `v49-基线对照清单-<head短哈希>.txt`，须与出包声明区间逐字一致。
