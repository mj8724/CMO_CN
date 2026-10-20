#!/usr/bin/env python3
"""Publish a GitHub Release and upload installer/pack assets for the current VERSION."""
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BUILD_DIR = ROOT / "build"
VERSION_FILE = ROOT / "VERSION"
CHANGELOG_FILE = ROOT / "docs/CHANGELOG.md"

PROXY = "http://127.0.0.1:7890"

def get_github_token() -> str:
    # 1. Environment variable
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if token:
        return token
    # 2. Git credential helper
    try:
        p = subprocess.Popen(
            ["git", "credential", "fill"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        out, _ = p.communicate(input="protocol=https\nhost=github.com\n\n")
        for line in out.splitlines():
            if line.startswith("password="):
                return line[9:].strip()
    except Exception as e:
        print(f"Warning: git credential fill failed: {e}")
    return ""

def get_opener():
    handlers = []
    if PROXY:
        handlers.append(urllib.request.ProxyHandler({"http": PROXY, "https": PROXY}))
    return urllib.request.build_opener(*handlers)

def check_regression_report(version: str, force: bool) -> None:
    """发版前必须有 24h 内、同版本、含 static/installer/live 且全通过的回归报告。

    判定逻辑统一放在 scripts/release_check.py（发版统一入库检查），避免两处门禁漂移；
    这里只负责把问题列表打印成发布流程的语气并决定是否中止。
    """
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import release_check

    problems = list(release_check.check_regression_report())
    if problems and str(VERSION_FILE.read_text(encoding="utf-8").strip()) != version:
        problems.append(f"发布版本 {version} != VERSION {VERSION_FILE.read_text(encoding='utf-8').strip()}")
    if problems:
        print("==> 发版前回归校验未通过：")
        for p in problems:
            print(f"    - {p}")
        print("    请按 docs/RELEASE_TEST_SOP.md 执行："
              "先 scripts/verify_regressions.py（static+live），再 scripts/package.sh。")
        if not force:
            print("    （确需绕过请加 --force，并在提交信息/发布说明写明原因）")
            sys.exit(1)
        print("    !! --force 已启用：本次绕过回归校验，风险自负 !!")
    else:
        print("==> 回归报告校验通过（版本匹配 / static+installer+live 全通过 / 24h 内）")


def verify_release(tag_name: str) -> int:
    """只读校验已发布的 Release：资产齐全、哈希与本地构建一致、latest 指向该 tag。

    严格只发 GET 请求，绝不创建/删除/上传任何东西——这是 `--verify-only` 存在的意义。
    """
    token = get_github_token()
    if not token:
        print("ERROR: 需要 GitHub token 才能读 Release（私有?）或提升速率限制。", file=sys.stderr)
        return 2
    opener = get_opener()
    api_base = "https://api.github.com/repos/mj8724/CMO_CN"
    headers = {"Authorization": f"token {token}", "Accept": "application/vnd.github.v3+json"}

    def _get(url: str) -> dict | None:
        try:
            with opener.open(urllib.request.Request(url, headers=headers), timeout=60) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise

    rel = _get(f"{api_base}/releases/tags/{tag_name}")
    if not rel:
        print(f"FAIL: Release {tag_name} 不存在", file=sys.stderr)
        return 1

    problems: list[str] = []
    if rel.get("draft"):
        problems.append("仍是 draft（草稿）")
    if rel.get("prerelease"):
        problems.append("被标记为 prerelease")

    assets = {a["name"]: a for a in rel.get("assets", [])}
    version = tag_name.lstrip("v")
    need = [f"CMO_Chinese_Setup_v{version}.exe", f"CMO_Chinese_Pack_{version}.zip",
            f"CMO_Chinese_Setup_v{version}.exe.sha256", f"CMO_Chinese_Pack_{version}.zip.sha256"]
    for n in need:
        if n not in assets:
            problems.append(f"缺少资产 {n}")
    print(f"资产: {len(assets)} 个" + ("" if not problems else f"，问题: {problems}"))

    # 哈希核对：GitHub 的 digest 字段 vs 本地构建产物
    import hashlib  # noqa: PLC0415
    for n in (need[0], need[1]):
        local = BUILD_DIR / n
        if n not in assets or not local.is_file():
            continue
        want = hashlib.sha256(local.read_bytes()).hexdigest()
        got = (assets[n].get("digest") or "").replace("sha256:", "")
        if got and got != want:
            problems.append(f"{n} 哈希不一致（远端 {got[:16]}… vs 本地 {want[:16]}…）")
        else:
            print(f"  {n}: 哈希一致")

    latest = _get(f"{api_base}/releases/latest")
    latest_tag = (latest or {}).get("tag_name")
    if latest_tag != tag_name:
        problems.append(f"latest 指向 {latest_tag}，不是 {tag_name}")
    else:
        print(f"  latest 指向 {latest_tag}")

    if problems:
        print("FAIL: " + "；".join(problems), file=sys.stderr)
        return 1
    print(f"PASS: {tag_name} 资产齐全、哈希一致、latest 正确（本次未做任何写入）")
    return 0


def main():
    version = VERSION_FILE.read_text(encoding="utf-8").strip()
    tag_name = f"v{version}"
    print(f"==> Target Release: {tag_name}")

    argv = sys.argv[1:]
    # --verify-only 必须是**纯只读**校验：历史 bug（2026-10-20 实测）——该参数从未被实现，
    # 而脚本也不校验未知参数，于是 `publish_release.py --verify-only` 会照常执行发布，
    # 把已上传的资产删除重传。做"发布后自检"时意外触发了写操作。
    # 现在：真正实现只读校验并**提前返回**，绝不进入上传分支。
    verify_only = "--verify-only" in argv
    known = {"--force", "--verify-only"}
    unknown = [a for a in argv if a not in known]
    if unknown:
        print(f"ERROR: 未知参数 {unknown}；可用参数：--force / --verify-only", file=sys.stderr)
        return 2

    check_regression_report(version, "--force" in argv)

    if verify_only:
        return verify_release(tag_name)

    # README 下载区：测试版走 releases/latest 通道（地址不含版本号），发版后自动指向新版本；
    # 但它的**显示版本号**仍要随发版更新，所以这里同步一次并校验。
    # 稳定版链接是显式 tag，日常发版**不动**它——只有维护者人工提升 STABLE_VERSION 才变。
    sync = ROOT / "scripts/sync_readme_downloads.py"
    if sync.is_file():
        r = subprocess.run([sys.executable, str(sync)], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=120)
        print(((r.stdout or "") + (r.stderr or "")).strip())
        if r.returncode != 0:
            print("ERROR: README 下载区同步失败，请先修复再发布。")
            sys.exit(1)

    token = get_github_token()
    if not token:
        print("ERROR: No GitHub token found. Please configure git credential or set GITHUB_TOKEN.")
        sys.exit(1)

    opener = get_opener()

    # Extract changelog
    changelog = CHANGELOG_FILE.read_text(encoding="utf-8")
    m = re.search(r"## v" + re.escape(version) + r"\s+([^\n]*)\n(.*?)(?=\n## v|\Z)", changelog, re.S)
    notes = m.group(0).strip() if m else f"## v{version}\n\n《现代行动：指挥》汉化更新包发布。"

    # 发版说明必须与我们的版本号并列写清「适配的游戏内部版本」（构建号），
    # 与 README / 安装器标题 / 更新助手保持同一单一来源（仓库根 GAME_BUILD）。
    game_build = ""
    _gb = ROOT / "GAME_BUILD"
    if _gb.is_file():
        game_build = _gb.read_text(encoding="utf-8").strip()

    # CHANGELOG 小标题自带破折号（"## v4.1.8 — 引擎性能与门禁加固"）时，
    # 不能再拼一个：否则发布标题会变成 "v4.1.8 — — 引擎性能与门禁加固"
    # （历史 v4.1.5/v4.1.7 就带着这个双破折号）。
    subtitle = re.sub(r"^[—–\-]\s*", "", m.group(1).strip()) if m else ""
    title = f"v{version} — {subtitle}" if subtitle else f"v{version}"
    build_line = f"> **适配游戏内部版本：{game_build}**（游戏标题里的 Build 号；不一致说明游戏已更新，请等对应版本的补丁）\n\n" if game_build else ""
    body = f"""{build_line}{notes}

### 安装方式

- **一键安装器（推荐）**：下载并运行 `CMO_Chinese_Setup_v{version}.exe`，自动识别 Steam 游戏目录并一键完成汉化。
- **绿色免安装包**：下载 `CMO_Chinese_Pack_{version}.zip`，解压后覆盖到游戏根目录即可。
"""

    # Check if release exists
    repo = "mj8724/CMO_CN"
    api_base = f"https://api.github.com/repos/{repo}"
    headers = {
        "Authorization": f"Bearer {token}",
        "User-Agent": "CMO-Localizer-Release-Tool",
        "Accept": "application/vnd.github.v3+json"
    }

    release_info = None
    check_req = urllib.request.Request(f"{api_base}/releases/tags/{tag_name}", headers=headers)
    try:
        with opener.open(check_req) as resp:
            release_info = json.loads(resp.read().decode("utf-8"))
            print(f"==> Release {tag_name} already exists (ID: {release_info['id']})")
    except urllib.error.HTTPError as e:
        if e.code == 404:
            print(f"==> Release {tag_name} does not exist yet. Creating...")
        else:
            print(f"ERROR checking release: {e}")
            sys.exit(1)

    if not release_info:
        create_payload = json.dumps({
            "tag_name": tag_name,
            "target_commitish": "master",
            "name": title,
            "body": body,
            "draft": False,
            "prerelease": False
        }).encode("utf-8")
        create_req = urllib.request.Request(
            f"{api_base}/releases",
            data=create_payload,
            headers={**headers, "Content-Type": "application/json"}
        )
        with opener.open(create_req) as resp:
            release_info = json.loads(resp.read().decode("utf-8"))
            print(f"==> Successfully created Release: {release_info['html_url']}")

    upload_url_template = release_info["upload_url"] # e.g. https://uploads.github.com/.../assets{?name,label}
    upload_base = upload_url_template.split("{")[0]

    # Existing assets
    existing_assets = {a["name"]: a["id"] for a in release_info.get("assets", [])}

    # Files to upload
    setup_file = BUILD_DIR / f"CMO_Chinese_Setup_v{version}.exe"
    if not setup_file.is_file():
        setup_file = BUILD_DIR / f"CMO_Chinese_Setup_{version}.exe"
    if not setup_file.is_file():
        setup_file = BUILD_DIR / "CMO_Chinese_Setup.exe"

    files_to_upload = [
        setup_file,
        BUILD_DIR / f"CMO_Chinese_Pack_{version}.zip"
    ]

    # SHA256 摘要资产：自动升级（CMO_Updater.exe）下载后据此校验安装包完整性
    for base in list(files_to_upload):
        digest = base.with_name(base.name + ".sha256")
        if digest.is_file():
            files_to_upload.append(digest)
        else:
            print(f"WARN: 缺少摘要文件 {digest.name}，更新助手将跳过摘要校验")

    for file_path in files_to_upload:
        if not file_path.is_file():
            print(f"ERROR: Asset file missing: {file_path}")
            continue

        file_name = file_path.name
        file_size = file_path.stat().st_size
        print(f"\n==> Preparing upload for {file_name} ({file_size / 1024 / 1024:.2f} MB)...")

        # If asset already exists, delete it first to ensure update
        if file_name in existing_assets:
            asset_id = existing_assets[file_name]
            print(f"  Deleting existing asset {file_name} (ID: {asset_id})...")
            del_req = urllib.request.Request(
                f"{api_base}/releases/assets/{asset_id}",
                headers=headers,
                method="DELETE"
            )
            try:
                with opener.open(del_req) as resp:
                    print("  Deleted.")
            except Exception as e:
                print(f"  Warning deleting asset: {e}")

        # Upload
        content_type = "application/vnd.microsoft.portable-executable" if file_name.endswith(".exe") else "application/zip"
        upload_url = f"{upload_base}?name={file_name}"
        data = file_path.read_bytes()

        upload_req = urllib.request.Request(
            upload_url,
            data=data,
            headers={
                **headers,
                "Content-Type": content_type,
                "Content-Length": str(file_size)
            }
        )

        t0 = time.time()
        with opener.open(upload_req) as resp:
            asset_res = json.loads(resp.read().decode("utf-8"))
            t1 = time.time()
            print(f"  [OK] Uploaded {file_name} in {t1 - t0:.1f}s")
            print(f"       Download URL: {asset_res['browser_download_url']}")

    print(f"\n==> All release assets published successfully to {release_info['html_url']} !")

if __name__ == "__main__":
    # 必须把返回值交给 sys.exit：否则脚本永远以 0 退出，
    # main() 里 return 1/2 的错误分支在 CI 或 && 链里会被当成成功（历史 bug）。
    sys.exit(main())
