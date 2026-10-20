#!/usr/bin/env python3
"""同步 README 下载区的版本号（稳定版 / 测试版）。

两条通道的语义不同，所以不能都写成"最新"：

- **稳定版**：由维护者人工确认。版本号存在仓库根 `STABLE_VERSION`，是唯一事实源。
  链接用**显式 tag**（`releases/download/v<版本>/...`），因此固定不变——
  只有维护者提升稳定版时才需要改。
- **测试版**：永远是"最近一次发布"。用 GitHub 的 `releases/latest` 通道，
  下载地址里不含版本号，所以不需要随每次发版改 README。

用法：
    python scripts/sync_readme_downloads.py            # 按 STABLE_VERSION 与 VERSION 重写下载区
    python scripts/sync_readme_downloads.py --check    # 只检查 README 是否与两个版本号一致

为什么要有 --check：README 是玩家第一眼看到的东西，版本号写错等于把人指向错误的包。
发版脚本与 release_check 都调用 --check，防止 README 与实际发布脱节。
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
README = ROOT / "README.md"
STABLE_FILE = ROOT / "STABLE_VERSION"
VERSION_FILE = ROOT / "VERSION"
REPO = "mj8724/CMO_CN"

BEGIN = "<!-- downloads:begin -->"
END = "<!-- downloads:end -->"


def read_version(path: Path, label: str) -> str:
    if not path.is_file():
        print(f"ERROR: 缺少 {label}（{path.name}）", file=sys.stderr)
        raise SystemExit(2)
    text = path.read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", text):
        print(f"ERROR: {label} 不是 x.y.z 形式：{text!r}", file=sys.stderr)
        raise SystemExit(2)
    return text


def render(stable: str, latest: str) -> str:
    setup = "CMO_Chinese_Setup_v{v}.exe"
    pack = "CMO_Chinese_Pack_{v}.zip"
    stable_setup = f"https://github.com/{REPO}/releases/download/v{stable}/{setup.format(v=stable)}"
    stable_pack = f"https://github.com/{REPO}/releases/download/v{stable}/{pack.format(v=stable)}"
    # 测试版走 releases/latest 通道：地址不含版本号，发版后自动指向新版本。
    latest_setup = f"https://github.com/{REPO}/releases/latest/download/{setup.format(v=latest)}"
    latest_pack = f"https://github.com/{REPO}/releases/latest/download/{pack.format(v=latest)}"

    return f"""{BEGIN}
## 下载

| 通道 | 适合谁 | 版本 | 下载 |
|---|---|---|---|
| **稳定版** | 想安稳玩、不想折腾 | **`{stable}`**（维护者人工确认） | [安装器]({stable_setup}) · [绿色包]({stable_pack}) |
| **测试版** | 想第一时间拿到最新修复 | **`{latest}`**（每次发布的最新版） | [安装器]({latest_setup}) · [绿色包]({latest_pack}) |

> **稳定版**链接固定指向 `v{stable}`，只有维护者确认新版本稳定后才会提升。
> **测试版**始终指向最近一次发布，可能包含尚未充分验证的修复。
>
> 不确定选哪个？**选稳定版**。想帮忙验证修复，再用测试版。

全部历史版本见 [Releases](https://github.com/{REPO}/releases)。
{END}"""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true",
                    help="只检查 README 与 STABLE_VERSION/VERSION 是否一致")
    args = ap.parse_args()

    stable = read_version(STABLE_FILE, "STABLE_VERSION")
    latest = read_version(VERSION_FILE, "VERSION")

    if not README.is_file():
        print("ERROR: 缺少 README.md", file=sys.stderr)
        return 2
    raw = README.read_bytes()
    crlf = b"\r\n" in raw
    text = raw.decode("utf-8").replace("\r\n", "\n")
    want = render(stable, latest)

    m = re.search(re.escape(BEGIN) + r".*?" + re.escape(END), text, re.S)
    if not m:
        print("ERROR: README 缺少下载区标记（downloads:begin/end）", file=sys.stderr)
        return 2

    current = m.group(0)
    if current == want:
        if args.check:
            print(f"PASS: README 下载区一致（稳定版 {stable} / 测试版 {latest}）")
        else:
            print(f"README 下载区已是最新（稳定版 {stable} / 测试版 {latest}）")
        return 0

    if args.check:
        # 只报差异要点，避免把整段 Markdown 刷到终端
        problems = []
        if f"v{stable}/" not in current:
            problems.append(f"稳定版链接未指向 v{stable}")
        if f"**`{stable}`**" not in current:
            problems.append(f"稳定版版本号未标为 {stable}")
        if f"**`{latest}`**" not in current:
            problems.append(f"测试版版本号未标为 {latest}")
        if not problems:
            problems.append("下载区文本与模板不一致（可能是手工改动）")
        print("FAIL: " + "；".join(problems), file=sys.stderr)
        print("      运行 python scripts/sync_readme_downloads.py 重新生成", file=sys.stderr)
        return 1

    text = text[:m.start()] + want + text[m.end():]
    # README 在仓库里是 CRLF：必须原样写回，否则每次同步都把整个文件改一遍
    # （表现为几千行 diff，把真正的改动埋掉）。
    if crlf:
        text = text.replace("\n", "\r\n")
    README.write_bytes(text.encode("utf-8"))
    print(f"README 下载区已更新（稳定版 {stable} / 测试版 {latest}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
