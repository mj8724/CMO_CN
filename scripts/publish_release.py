#!/usr/bin/env python3
"""Publish four validated immutable assets, or verify an existing release with GET only."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BUILD_DIR = ROOT / "build"
VERSION_FILE = ROOT / "VERSION"
CHANGELOG_FILE = ROOT / "docs/CHANGELOG.md"
REPO = "mj8724/CMO_CN"
API_BASE = f"https://api.github.com/repos/{REPO}"
PROXY = "http://127.0.0.1:7890"


def get_github_token() -> str:
    """Only called for an explicitly requested upload, after every local gate passes."""
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if token:
        return token
    try:
        p = subprocess.run(["git", "credential", "fill"],
                           input="protocol=https\nhost=github.com\n\n",
                           capture_output=True, text=True, timeout=30)
        if p.returncode == 0:
            for line in p.stdout.splitlines():
                if line.startswith("password="):
                    return line[9:].strip()
    except (OSError, subprocess.TimeoutExpired):
        pass
    return ""


def get_opener():
    handlers = []
    if PROXY:
        handlers.append(urllib.request.ProxyHandler({"http": PROXY, "https": PROXY}))
    return urllib.request.build_opener(*handlers)


def _get_json(opener, url: str, headers: dict[str, str]) -> dict | None:
    try:
        with opener.open(urllib.request.Request(url, headers=headers, method="GET"), timeout=60) as resp:
            result = json.loads(resp.read().decode("utf-8"))
            if not isinstance(result, dict):
                raise ValueError("GitHub response is not an object")
            return result
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        raise


def _remote_problems(opener, release: dict, tag: str, files: dict[str, bytes],
                     headers: dict[str, str]) -> list[str]:
    """Compare the whole four-asset set before deciding anything; absent digest requires GET hashing."""
    problems: list[str] = []
    if release.get("tag_name") != tag:
        problems.append("Release tag 不匹配")
    if release.get("draft") is not False or release.get("prerelease") is not False:
        problems.append("Release 不是正式非草稿版本")
    raw = release.get("assets")
    if not isinstance(raw, list):
        return problems + ["Release 缺少资产列表"]
    assets = {}
    for asset in raw:
        if not isinstance(asset, dict) or not isinstance(asset.get("name"), str):
            problems.append("Release 包含无效资产信息")
            continue
        name = asset["name"]
        if name in assets:
            problems.append(f"Release 同名资产重复: {name}")
        assets[name] = asset
    if set(assets) != set(files):
        problems.append("远端四资产不完整或含额外资产；已有 Release 禁止修改/补传")
    for name, local_bytes in files.items():
        asset = assets.get(name)
        if asset is None:
            problems.append(f"缺少远端资产 {name}")
            continue
        if asset.get("state") != "uploaded" or type(asset.get("size")) is not int or asset["size"] != len(local_bytes):
            problems.append(f"{name} 状态/长度不匹配")
        want = hashlib.sha256(local_bytes).hexdigest()
        digest = asset.get("digest")
        if digest is not None and (not isinstance(digest, str) or
                                   not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", digest)):
            problems.append(f"{name} 远端 digest 无效")
        elif digest is not None and digest[7:].lower() != want:
            problems.append(f"{name} 远端 SHA256 不一致，拒绝覆盖")
        # The updater reads sidecar bytes. Always GET them; if a binary has no API digest,
        # GET and hash its real bytes instead of claiming an unobserved equality.
        if name.endswith(".sha256") or digest is None:
            url = asset.get("browser_download_url")
            prefix = f"https://github.com/{REPO}/releases/download/{tag}/"
            if not isinstance(url, str) or url != prefix + urllib.parse.quote(name):
                problems.append(f"{name} 缺少合法的下载 URL，无法核验真实字节")
                continue
            try:
                req = urllib.request.Request(url, headers={"User-Agent": headers["User-Agent"]}, method="GET")
                with opener.open(req, timeout=120) as resp:
                    remote_hash = hashlib.sha256()
                    size = 0
                    for chunk in iter(lambda: resp.read(1024 * 1024), b""):
                        size += len(chunk)
                        remote_hash.update(chunk)
                if size != len(local_bytes) or remote_hash.hexdigest() != want:
                    problems.append(f"{name} 下载字节与本地核验资产不一致")
            except (OSError, urllib.error.URLError):
                problems.append(f"{name} GET 下载失败，不能确认哈希")
    return problems


def verify_release(tag_name: str) -> int:
    """Public read-only verification: no token read, report/write gates, README edits, or writes."""
    import release_check

    files, problems = release_check.read_artifacts(tag_name[1:], BUILD_DIR)
    if problems:
        for problem in problems:
            print("FAIL: " + problem, file=sys.stderr)
        return 1
    opener = get_opener()
    headers = {"User-Agent": "CMO-Localizer-Release-Tool", "Accept": "application/vnd.github+json"}
    try:
        release = _get_json(opener, f"{API_BASE}/releases/tags/{tag_name}", headers)
        if release is None:
            print(f"FAIL: Release {tag_name} 不存在", file=sys.stderr)
            return 1
        problems += _remote_problems(opener, release, tag_name, files, headers)
        latest = _get_json(opener, f"{API_BASE}/releases/latest", headers)
        if (latest or {}).get("tag_name") != tag_name:
            problems.append("latest 未指向目标 tag")
    except (OSError, ValueError, urllib.error.URLError):
        problems.append("Release GET 校验失败")
    if problems:
        for problem in problems:
            print("FAIL: " + problem, file=sys.stderr)
        return 1
    print(f"PASS: {tag_name} 四资产/真实 SHA256/latest 一致（纯 GET；未读取发布凭据或报告门禁）")
    return 0


def run_intake_check() -> bool:
    """The full shared intake gate also checks README with --check, never regenerating it."""
    p = subprocess.run([sys.executable, str(ROOT / "scripts/release_check.py")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=600)
    print(((p.stdout or "") + (p.stderr or "")).strip())
    return p.returncode == 0


def release_notes(version: str) -> tuple[str, str]:
    changelog = CHANGELOG_FILE.read_text(encoding="utf-8")
    top = re.search(r"^## v([0-9]+\.[0-9]+\.[0-9]+)\b", changelog, re.M)
    if top is None or top[1] != version:
        raise ValueError("CHANGELOG 顶部必须是当前 VERSION，禁止用默认发布说明代替")
    match = re.search(r"^## v" + re.escape(version) + r"\s+([^\n]*)\n(.*?)(?=\n## v|\Z)", changelog, re.M | re.S)
    if match is None:
        raise ValueError("缺少当前版本的发布说明")
    game_build = (ROOT / "GAME_BUILD").read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", game_build):
        raise ValueError("GAME_BUILD 缺少合法游戏构建号")
    subtitle = re.sub(r"^[—–\-]\s*", "", match[1].strip())
    title = f"v{version} — {subtitle}" if subtitle else f"v{version}"
    body = f"""> **适配游戏内部版本：{game_build}**（游戏标题里的 Build 号）

{match[0].strip()}

### 安装方式

- **一键安装器（推荐）**：运行 `CMO_Chinese_Setup_v{version}.exe`。
- **绿色包**：把 `CMO_Chinese_Pack_{version}.zip` 解压到独立临时目录，先退出游戏，执行包内同版本安装器 `CMO_Chinese_Setup_v{version}.exe --install "<游戏根目录>" --silent`，完成首次配置合并与部署；不要直接覆盖官方 config 或任何旧备份。
- 安装器会保留官方 runtime/bindingRedirects、首次备份与玩家设置并写入安装状态；`--adopt` 仅登记已有完整载荷，不代替首次 apply。详见包内 `汉化说明.md`。
"""
    return title, body


def current_commit() -> str:
    p = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True,
                       text=True, encoding="ascii", timeout=15)
    commit = p.stdout.strip()
    if p.returncode or not re.fullmatch(r"[0-9a-f]{40,64}", commit):
        raise ValueError("无法确认当前已验收源码 commit")
    return commit


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    ap.add_argument("--version", help="目标 x.y.z；上传时必须等于 VERSION")
    ap.add_argument("--verify-only", action="store_true", help="只读 GET 校验四资产与 latest；不读凭据/报告")
    args = ap.parse_args(argv)
    version = args.version or VERSION_FILE.read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        ap.error("version 必须为 x.y.z")
    tag = f"v{version}"
    print(f"==> Target Release: {tag}")
    # This branch must precede every upload-only gate, including report/source/README/credential reads.
    if args.verify_only:
        return verify_release(tag)

    import release_check

    try:
        files, problems = release_check.check_upload_preflight(version, root=ROOT, build_dir=BUILD_DIR)
        if problems:
            for problem in problems:
                print("FAIL: " + problem, file=sys.stderr)
            return 1
        title, body = release_notes(version)
        if not run_intake_check():
            print("FAIL: 完整入库门禁未通过；未读取凭据、未写远端", file=sys.stderr)
            return 1
        commit = current_commit()
        token = get_github_token()
        if not token:
            print("FAIL: 缺少 GitHub 发布授权凭据", file=sys.stderr)
            return 1
        opener = get_opener()
        headers = {"Authorization": f"Bearer {token}", "User-Agent": "CMO-Localizer-Release-Tool",
                   "Accept": "application/vnd.github+json"}
        release = _get_json(opener, f"{API_BASE}/releases/tags/{tag}", headers)
        if release is not None:
            problems = _remote_problems(opener, release, tag, files, headers)
            if problems:
                for problem in problems:
                    print("FAIL: " + problem, file=sys.stderr)
                return 1
            print(f"PASS: {tag} 四资产已是同一份 SHA256，幂等完成（0 远端写入）")
            return 0
        # Never create a release over a pre-existing tag, including known history in a shallow clone.
        if tuple(map(int, version.split("."))) <= release_check.FROZEN_THROUGH:
            print(f"FAIL: {tag} 是已冻结历史版本，不能创建/重发", file=sys.stderr)
            return 1
        if _get_json(opener, f"{API_BASE}/git/ref/tags/{tag}", headers) is not None:
            print(f"FAIL: 已有 tag {tag}，拒绝发布或补传", file=sys.stderr)
            return 1
        fresh_files, problems = release_check.check_upload_preflight(version, root=ROOT, build_dir=BUILD_DIR)
        if problems or fresh_files != files:
            print("FAIL: 发布输入在预检后改变，必须重新验收（0 远端写入）", file=sys.stderr)
            return 1
        payload = {"tag_name": tag, "target_commitish": commit, "name": title, "body": body,
                   "draft": False, "prerelease": False}
        req = urllib.request.Request(f"{API_BASE}/releases", data=json.dumps(payload).encode("utf-8"),
                                     headers={**headers, "Content-Type": "application/json"}, method="POST")
        with opener.open(req, timeout=60) as resp:
            release = json.loads(resp.read().decode("utf-8"))
        if not isinstance(release, dict) or release.get("tag_name") != tag or release.get("assets") != []:
            raise ValueError("新建 Release 状态不明确，拒绝上传（不会删除或重试）")
        base = str(release.get("upload_url", "")).split("{")[0]
        if not re.fullmatch(r"https://uploads\.github\.com/repos/" + re.escape(REPO) + r"/releases/[0-9]+/assets", base):
            raise ValueError("新建 Release upload_url 无效，拒绝上传")
        for name in release_check.artifact_names(version):
            data = files[name]
            content_type = ("text/plain" if name.endswith(".sha256") else
                            "application/zip" if name.endswith(".zip") else
                            "application/vnd.microsoft.portable-executable")
            req = urllib.request.Request(base + "?" + urllib.parse.urlencode({"name": name}), data=data,
                                         headers={**headers, "Content-Type": content_type,
                                                  "Content-Length": str(len(data))}, method="POST")
            with opener.open(req, timeout=300) as resp:
                asset = json.loads(resp.read().decode("utf-8"))
            if not isinstance(asset, dict) or asset.get("name") != name or asset.get("state") != "uploaded" or asset.get("size") != len(data):
                raise ValueError("上传响应未确认完整资产；中止且不删除任何已上传文件")
            digest = asset.get("digest")
            if digest is not None and digest != "sha256:" + hashlib.sha256(data).hexdigest():
                raise ValueError("上传响应 SHA256 不匹配；中止且不覆盖任何资产")
            print(f"  [OK] Uploaded {name} ({len(data):,d} bytes)")
        # A 2xx upload is not sufficient: inspect actual published bytes/sidecars and latest using GET.
        uploaded = _get_json(opener, f"{API_BASE}/releases/tags/{tag}", headers)
        problems = ["上传后无法 GET Release"] if uploaded is None else _remote_problems(opener, uploaded, tag, files, headers)
        latest = _get_json(opener, f"{API_BASE}/releases/latest", headers)
        if (latest or {}).get("tag_name") != tag:
            problems.append("上传后 latest 未指向目标 tag")
        if problems:
            for problem in problems:
                print("FAIL: " + problem, file=sys.stderr)
            return 1
        print(f"PASS: {tag} 四资产发布并 GET 复核通过（历史资产未修改）")
        return 0
    except (OSError, ValueError, KeyError, TypeError, urllib.error.URLError, subprocess.TimeoutExpired) as exc:
        # Do not echo request headers, response bodies, or secrets. Partial writes remain immutable;
        # any repair is a separately authorized new version, not an automatic destructive retry.
        print(f"FAIL: 发布中止（{type(exc).__name__}）；未删除或覆盖任何历史资产", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
