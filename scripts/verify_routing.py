#!/usr/bin/env python3
"""路由表门禁：确保每个受版本控制的文件都在 docs/ROUTING.md 里登记过职责。

**为什么需要这道门禁**：路由表靠人工维护必然腐烂——新加一个脚本忘了登记，
半年后 agent（和人）就不知道那文件干什么用、能不能删。
所以"每写一个新文件都要记录到路由表"必须是**机械可检查**的，不能靠自觉。

匹配规则（刻意做得很简单，避免写正则）
    `scripts/build.sh`        精确路径
    `src/tools/Inspect*`      `*` 匹配任意字符（**包括 `/`**），所以这是"以 Inspect 开头的全部"
    `docs/*.md`               该目录下全部 .md（含子目录，因为 `*` 跨 `/`）
    `dict/src/*.json`         该目录下全部 json

用法
    python scripts/verify_routing.py               # 校验（门禁用）
    python scripts/verify_routing.py --list-gaps   # 只列出未登记文件（写文档时用）
"""

from __future__ import annotations

import argparse
import re
import subprocess
import proc_util
proc_util.utf8_stdout()
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ROUTING = ROOT / "docs" / "ROUTING.md"

# 不参与登记的文件：由工具链自动生成/管理的元数据，人工维护没有意义。
SKIP_PREFIXES = (
    ".workflow/",   # maestro 知识库（specs/knowhow/embedding），由 maestro 命令管理
    ".git")
SKIP_EXACT = {".gitignore", ".git-blame-ignore-revs"}


def tracked_files() -> list[str]:
    """受控文件 = 已跟踪 + 未跟踪但未被忽略。

    包含未跟踪文件是刻意的：规矩是"新增文件在**提交前**就要登记"，
    只看已跟踪文件的话，新建文件在 `git add` 之前根本不会被检查，
    等于放行了最该拦截的时刻。
    """
    out = proc_util.run(["git", "ls-files", "--cached", "--others", "--exclude-standard"],
                         cwd=ROOT, capture_output=True, )
    if out.returncode != 0:
        print("ERROR: git ls-files 失败（不在 git 仓库里？）", file=sys.stderr)
        raise SystemExit(2)
    return [ln.strip() for ln in (out.stdout or "").splitlines() if ln.strip()]


def routing_patterns(text: str) -> tuple[list[str], list[tuple[str, str]]]:
    """从路由表里抽出所有路径模式。

    只认表格里**第一个单元格**、且被反引号包裹的内容：`path`。
    这样文档正文里随便提到路径不会污染匹配。
    返回 (patterns, [(pattern, 职责描述)])。
    """
    pats: list[str] = []
    with_desc: list[tuple[str, str]] = []
    in_path_table = False
    for line in text.splitlines():
        s = line.strip()
        if not s.startswith("|"):
            in_path_table = False
            continue
        cells = [c.strip() for c in s.strip("|").split("|")]
        if not cells:
            continue
        # 只解析**表头第一格是「路径」**的表格。这样文档里其它表格
        # （引擎诊断命令表、不入库目录表等）不会污染匹配。
        if cells[0] in ("路径", "文件", "path"):
            in_path_table = True
            continue
        if not in_path_table:
            continue
        if re.fullmatch(r"[-: ]+", cells[0] or "-"):
            continue
        # 一行可以写多个路径（用「、」分隔），提取该单元格内**所有**反引号内容。
        found = re.findall(r"`([^`]+)`", cells[0])
        if not found:
            continue
        desc = cells[1] if len(cells) > 1 else ""
        for pat in (f.strip() for f in found):
            if not pat or pat in ("路径", "文件", "path"):
                continue
            pats.append(pat)
            with_desc.append((pat, desc))
    return pats, with_desc


def to_regex(pattern: str) -> re.Pattern:
    """`*` 匹配任意字符（含 `/`），`?` 匹配单字符，其余按字面量。"""
    parts = []
    for ch in pattern:
        if ch == "*":
            parts.append(".*")
        elif ch == "?":
            parts.append(".")
        else:
            parts.append(re.escape(ch))
    return re.compile("^" + "".join(parts) + "$")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--list-gaps", action="store_true", help="只列出未登记文件")
    args = ap.parse_args()

    if not ROUTING.is_file():
        print(f"ERROR: 缺少路由表 {ROUTING.relative_to(ROOT)}", file=sys.stderr)
        return 1

    text = ROUTING.read_text(encoding="utf-8")
    pats, with_desc = routing_patterns(text)
    if not pats:
        print("ERROR: 路由表里没解析到任何条目（表格首个单元格需用反引号包裹路径）",
              file=sys.stderr)
        return 1

    regexes = [(p, to_regex(p)) for p in pats]
    files = [f for f in tracked_files()
             if f not in SKIP_EXACT and not f.startswith(SKIP_PREFIXES)]

    gaps = [f for f in files if not any(r.match(f) for _, r in regexes)]

    # 反向检查：登记了但一个文件都匹配不上的条目 = 文档过期（文件改名/删除后没更新）。
    stale = [p for p, r in regexes if not any(r.match(f) for f in files)]

    if args.list_gaps:
        for f in gaps:
            print(f)
        if stale:
            print("--- 过期条目（匹配不到任何文件）---", file=sys.stderr)
            for p in stale:
                print(p, file=sys.stderr)
        return 0 if not gaps else 1

    ok = True
    print(f"路由表: {len(pats)} 条模式 | 受控文件 {len(files)} 个")
    if gaps:
        ok = False
        print(f"  [FAIL] {len(gaps)} 个文件未在 {ROUTING.name} 登记：")
        for f in gaps[:40]:
            print(f"         {f}")
        if len(gaps) > 40:
            print(f"         …还有 {len(gaps) - 40} 个")
        print("  → 新增文件必须写进 docs/ROUTING.md 并说明职责；"
              "确属一次性/遗留的，归到「遗留与一次性文件」小节。")
    else:
        print("  [PASS] 所有受控文件均已登记")

    if stale:
        ok = False
        print(f"  [FAIL] {len(stale)} 条登记匹配不到任何文件（文档过期）：")
        for p in stale[:20]:
            print(f"         {p}")

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
