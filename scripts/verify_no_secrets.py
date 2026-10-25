#!/usr/bin/env python3
"""密钥门禁：禁止任何明文凭据进入仓库（工作区、历史、发布产物）。

用法:
    python scripts/verify_no_secrets.py            # 扫描工作区（已跟踪 + 未跟踪 + 危险忽略项）
    python scripts/verify_no_secrets.py --history   # 追加扫描全部提交历史（git pickaxe）
退出码 0 = 干净，1 = 发现疑似凭据。

扫描范围三层（历史教训：只看 `git ls-files` 会漏掉真凭据）：
  1) 已跟踪文件；
  2) 未跟踪但未被忽略的文件（新增脚本/笔记）；
  3) **危险的忽略项**——`__pycache__/*.pyc`、`.env*`、`*.pem/*.key/*.pfx`、`*.log`。
     2026-10-16 的真实泄漏：`scripts/__pycache__/translate_worker.cpython-312.pyc` 里
     有明文 `sk-` 令牌，但 `__pycache__/` 被 .gitignore 忽略，第 1 层完全看不到。
所有跳过项（过大/不属于三层范围）都会打印原因，不再静默 skip。
"""
from __future__ import annotations

import re
import subprocess
import proc_util
proc_util.utf8_stdout()
import sys
from pathlib import Path

PATTERNS = [
    ("LLM API Key", re.compile(r"\bsk-[A-Za-z0-9]{20,}\b")),
    ("GitHub Token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{20,}\b")),
    ("AWS Access Key", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    ("Slack Token", re.compile(r"\bxox[baprs]-[A-Za-z0-9\-]{10,}")),
    ("Private Key", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ("JWT", re.compile(r"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")),
    ("Hardcoded key", re.compile(r"""(?i)(api[_-]?key|apikey|secret|token|passwd|password)\s*[:=]\s*["'][A-Za-z0-9_\-/+]{16,}["']""")),
]

# 允许出现的占位符/环境变量形式
ALLOW = re.compile(r"(os\.environ|getenv|Environment\.GetEnvironmentVariable|\*\*\*|REMOVED|MASKED|你的|xxxx|<[A-Za-z_]+>)")

# 危险忽略项：这些路径默认被 .gitignore 排除，但历史证明它们会藏真凭据。
DANGEROUS_GLOBS = (
    "**/__pycache__/*.pyc", "**/*.pyc", "**/*.pyo",
    ".env", ".env.*", "**/.env", "**/.env.*",
    "**/*.pem", "**/*.key", "**/*.pfx", "**/*.p12",
    "**/*.log", "**/credentials*.json", "**/*secret*.json")

MAX_BYTES = 5_000_000


def _git(*args: str) -> list[str]:
    out = proc_util.run(["git", *args], capture_output=True).stdout
    return [p for p in out.splitlines() if p.strip()]


def candidate_files() -> tuple[list[Path], dict[str, int]]:
    """待扫描文件 + 分组计数（已跟踪 / 未跟踪 / 危险忽略项）。"""
    root = Path.cwd()
    groups: list[tuple[str, list[Path]]] = []
    groups.append(("tracked", [Path(p) for p in _git("ls-files")]))
    groups.append(("untracked", [Path(p) for p in _git("ls-files", "--others", "--exclude-standard")]))
    danger: dict[Path, None] = {}
    for pat in DANGEROUS_GLOBS:
        for p in root.glob(pat):
            if p.is_file() and ".git" not in p.parts:
                danger[p] = None
    groups.append(("dangerous-ignored", sorted(danger)))

    files: list[Path] = []
    counts: dict[str, int] = {}
    seen: set[Path] = set()
    for label, items in groups:
        counts[label] = 0
        for p in items:
            if p in seen or not p.is_file():
                continue
            seen.add(p)
            files.append(p)
            counts[label] += 1
    return files, counts


def scan_worktree() -> tuple[list[str], list[str], dict[str, int]]:
    """返回 (命中, 跳过说明, 分组计数)。"""
    hits: list[str] = []
    skipped: list[str] = []
    files, counts = candidate_files()
    for f in files:
        try:
            size = f.stat().st_size
        except Exception as exc:
            skipped.append(f"{f}（无法读取大小: {exc}）")
            continue
        if size > MAX_BYTES:
            skipped.append(f"{f}（{size // 1024 // 1024}MB > {MAX_BYTES // 1000 // 1000}MB，未扫）")
            continue
        try:
            text = f.read_text(encoding="utf-8", errors="ignore")
        except Exception as exc:
            skipped.append(f"{f}（读取失败: {exc}）")
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if ALLOW.search(line):
                continue
            for label, rx in PATTERNS:
                if rx.search(line):
                    hits.append(f"{f}:{i}  [{label}]")
                    break
    return hits, skipped, counts


def scan_history(max_bytes: int = 200_000_000) -> tuple[list[str], str]:
    """扫描全部历史（按 blob 去重，避免逐提交重复读巨型文件）。

    旧实现用 `git log -G<pattern>`：git 的正则方言不认 `\b`/`{20,}` 这类写法，
    实测对真实存在的凭据提交零命中（而 `git log -S"sk-"` 能命中），等于历史门禁完全失效。
    现改为：`rev-list --objects` 取全部对象 → `--batch-check` 按大小预筛 →
    分块 `cat-file --batch` 读取并逐个 blob 跑同一套 PATTERNS（与工作区扫描同源）。
    返回 (命中, 覆盖率说明)。
    """
    shas = sorted({line.split()[0] for line in _git("rev-list", "--objects", "--all") if line.strip()})
    if not shas:
        return [], "无历史对象"
    # 先问类型与大小，剔除非 blob 与超大文件（不把 10MB 级词典反复拉进内存）。
    chunk = 200
    blobs: list[tuple[str, int]] = []
    skipped_big = 0
    for i in range(0, len(shas), chunk):
        part = shas[i:i + chunk]
        out = proc_util.run(["git", "cat-file", "--batch-check"], input="\n".join(part) + "\n",
                             capture_output=True, ).stdout
        for line in out.splitlines():
            f = line.split()
            if len(f) < 3 or f[1] != "blob":
                continue
            size = int(f[2])
            if size > MAX_BYTES:
                skipped_big += 1
                continue
            blobs.append((f[0], size))

    hits: list[str] = []
    budget = 0
    scanned_files = 0
    for i in range(0, len(blobs), chunk):
        part = blobs[i:i + chunk]
        if budget > max_bytes:
            break
        out = proc_util.run(["git", "cat-file", "--batch"],
                             input=("\n".join(s for s, _ in part) + "\n").encode(),
                             capture_output=True).stdout
        pos = 0
        for sha, size in part:
            nl = out.find(b"\n", pos)
            if nl < 0:
                break
            pos = nl + 1
            data = out[pos:pos + size]
            pos += size + 1
            budget += size
            scanned_files += 1
            text = data.decode("utf-8", errors="ignore")
            for label, rx in PATTERNS:
                m = rx.search(text)
                if m:
                    hits.append(f"[{label}] 历史 blob {sha[:10]}（{size}B）："
                                f"命中 {m.group(0)[:6]}…（长度 {len(m.group(0))}，值不展示）")
                    break
    note = (f"blob 去重后 {len(shas)} 个 → 扫描 {scanned_files} 个（{budget // 1024 // 1024}MB），"
            f"跳过 >{MAX_BYTES // 1000 // 1000}MB 的 {skipped_big} 个")
    if budget > max_bytes:
        note += f"；⚠ 达到 {max_bytes // 1024 // 1024}MB 预算上限，后续 blob 未扫"
    return hits, note


def main() -> int:
    deep = "--history" in sys.argv
    print("密钥门禁（verify_no_secrets）")
    print("-" * 62)
    w, skipped, counts = scan_worktree()
    total = sum(counts.values())
    print(f"  扫描范围: 已跟踪 {counts.get('tracked', 0)} + 未跟踪 {counts.get('untracked', 0)}"
          f" + 危险忽略项 {counts.get('dangerous-ignored', 0)} = {total} 个文件")
    if skipped:
        print(f"  跳过 {len(skipped)} 个（已列出，不再静默 skip）:")
        for s in skipped[:10]:
            print("   - " + s)
        if len(skipped) > 10:
            print(f"   - …还有 {len(skipped) - 10} 个")
    print(f"  命中 {len(w)}")
    for x in w[:20]:
        print("   ✗ " + x)
    h, hist_note = scan_history() if deep else ([], "")
    if deep:
        print(f"  提交历史: {hist_note} → 命中 {len(h)} 组")
        for x in h:
            print("   ✗ " + x)
    if w or h:
        print("-" * 62)
        print("结果: [FAIL] 发现疑似明文凭据，禁止提交/发布")
        return 1
    print("-" * 62)
    print("结果: [PASS] 未发现明文凭据")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
