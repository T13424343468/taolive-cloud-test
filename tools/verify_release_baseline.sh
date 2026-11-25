#!/usr/bin/env bash
# v49 出包基线 diff（总镜子配套件）：出包 commit 区间逐文件 hash 对照，产出基线对照清单随包。
# 防的是 vc54 式基线污染——出包树与声明区间不一致时必须拦下，无清单不放行。
# 用法： tools/verify_release_baseline.sh <base_commit> [head_commit]（默认 HEAD）
set -euo pipefail

BASE="${1:-}"
HEAD="${2:-HEAD}"
[ -n "$BASE" ] || { echo "用法：tools/verify_release_baseline.sh <base_commit> [head_commit]"; exit 2; }

git rev-parse --verify "$BASE^{commit}" >/dev/null 2>&1 || { echo "[v49] base 不是有效 commit：$BASE"; exit 2; }
git rev-parse --verify "$HEAD^{commit}" >/dev/null 2>&1 || { echo "[v49] head 不是有效 commit：$HEAD"; exit 2; }

# 工作树必须干净：脏树出包=说不清包里是什么，直接不放行
if [ -n "$(git status --porcelain)" ]; then
  echo "[v49] 不放行：工作树有未提交改动（vc54 式基线污染风险）"
  git status --short | head -20
  exit 1
fi

BASE_ID="$(git rev-parse "$BASE")"
HEAD_ID="$(git rev-parse "$HEAD")"
if [ "$BASE_ID" = "$HEAD_ID" ]; then
  echo "[v49] 不放行：base 与 head 同一 commit，区间为空无意义"; exit 1
fi

REPORT="v49-基线对照清单-$(git rev-parse --short "$HEAD").txt"
{
  echo "v49 出包基线对照 · $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
  echo "区间：$BASE → $HEAD"
  echo ""
  printf '%-8s %-12s %-12s %s\n' "状态" "base hash" "head hash" "文件"
  git diff --name-status "$BASE" "$HEAD" | while IFS=$'\t' read -r st file; do
    BH="$(git rev-parse --short "$BASE:$file" 2>/dev/null || echo '(新增)')"
    HH="$(git rev-parse --short "$HEAD:$file" 2>/dev/null || echo '(删除)')"
    printf '%-8s %-12s %-12s %s\n' "$st" "$BH" "$HH" "$file"
  done
  echo ""
  echo "统计：$(git diff --name-only "$BASE" "$HEAD" | wc -l) 个文件变更；HEAD=$(git rev-parse --short "$HEAD") 工作树干净。"
} > "$REPORT"

echo "[v49] 基线对照清单已产出：$REPORT"
echo "[v49] 出包声明区间须与清单逐字一致；清单随包归档。"
