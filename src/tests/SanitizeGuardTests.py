#!/usr/bin/env python3
"""R76：词典构建期两道守卫的「过宽误杀」回归。

背景（2026-10-28）：`scan_english_residue` 里 scenario 面 152 条缺口逐个对源后
发现 11 条**源里明明有中文译文**，玩家却看不到——它们在 `build_dict.sanitize()`
阶段被丢掉，回落到英文：

  · `triage.CODE_EXPR` 的 `;\\s*$` 把以分号结尾的**自然语言句子**当代码；
  · `triage.MEMBER_CALL` 的 `\\.\\w+\\s*\\(` 把 `.003 (`（番号/小数后的括号）当成员调用；
  · `build_dict` 的括号平衡逐形状比对，把「ASCII 括号译成全角」当结构差。

三道守卫都必须继续拦住真代码/真结构差，只放过这三类误杀。
本用例同时锁住两个方向，防止以后为了「多译几条」把它们整体拆掉。
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))

import build_dict as B  # noqa: E402
import triage  # noqa: E402

failures = 0


def check(label: str, ok: bool, detail: str) -> None:
    global failures
    print(("--> OK   " if ok else "--> FAIL ") + label + " | " + detail)
    if not ok:
        failures += 1


def main() -> int:
    # ---- 1. 句尾分号：自然语言句子放行，代码仍然拦住 ----
    for text in (
        "There is risk that North Korea will launch an immediate ground attack;",
        "The probable firing elements are not located;",
        "We have no real idea of which targets the Russians will strike or when;",
    ):
        check("R76 分号句尾的散文不算代码: " + text[:38],
              triage.is_script_or_schema(text) is False,
              f"is_script_or_schema={triage.is_script_or_schema(text)}")

    for text in ("int i;", "x = 1;", "for (i=0;i<n;i++);", "if (a == b) then;",
                 "obj.ToString();", "local yaw = 45"):
        check("R76 真代码仍被拦住: " + text,
              triage.is_script_or_schema(text) is True,
              f"is_script_or_schema={triage.is_script_or_schema(text)}")

    # 带 `;` 但另有代码特征（==、::、null）的串，即使有虚词也必须拦住
    tricky = "if there is a value == null;"
    check("R76 分号+代码特征仍算代码", triage.is_script_or_schema(tricky) is True,
          f"is_script_or_schema={triage.is_script_or_schema(tricky)}")

    # ---- 2. 小数点/番号后的括号不是成员调用；真成员调用仍拦住 ----
    for text in (
        "Abeche: Escadron EC 01.003 (Mirage 2000D), 3e RHC (Tigre), Chadian Air Force Mi-35 units",
        "Task Unit 544.01.06 (Based at Brunei Int Airport.)",
        "1 Lynx HAS.3(GM)",
    ):
        check("R76 番号后括号不算成员调用: " + text[:38],
              triage.is_script_or_schema(text) is False,
              f"is_script_or_schema={triage.is_script_or_schema(text)}")

    for text in ("obj.ToString();", "foo.Bar(", "RunTest()"):
        check("R76 真成员调用仍被拦住: " + text,
              triage.MEMBER_CALL.search(text) is not None,
              f"member_call={bool(triage.MEMBER_CALL.search(text))}")

    # ---- 3. 括号：全角/ASCII 互写放行，结构差仍然丢弃 ----
    for en, zh in (
        ("Bomb Wing) (B-2A)", "轰炸机联队）（B-2A）"),
        (") is operating out of T'oejo-dong Naval Base", "）以退潮洞海军基地为活动基地"),
        ("x MQ-4C Triton UAV)", "×MQ-4C“特里同”无人机）"),
        ("A-10 Warthog", "A-10 疣猪"),
    ):
        check("R76 全角/ASCII 括号互写不丢译文: " + en[:30],
              B.sanitize(en, zh) is not None,
              f"sanitize={B.sanitize(en, zh)!r}")

    for en, zh in (
        # 源缺右括号、译文补上了 —— 结构真不同，必须丢
        ("HMS Brazen (F 88", "皇家海军勇敢号（F 88）"),
        ("AAA Plt (Korkut (Twin-35mm SPAAG)", "高炮排 (科尔库特 双联装35mm 自行高炮)"),
        ("M75 [10 x 40mm Grenade Salvo", "M75 [10 × 40mm 榴弹齐射]"),
    ):
        check("R76 结构差仍被丢弃: " + en[:30],
              B.sanitize(en, zh) is None,
              f"sanitize={B.sanitize(en, zh)!r}")

    # ---- 4. 端到端：本轮修复的三条实机想定句必须真的进了构建产物 ----
    import json
    built = json.loads((ROOT / "dict/CMOLocalizer.json").read_text(encoding="utf-8"))["exact"]
    for fragment in (
        "There is risk that North Korea will launch an immediate ground attack;",
        "The probable firing elements are not located;",
    ):
        check("R76 构建产物含实机想定句: " + fragment[:30],
              built.get(fragment) is not None, f"in_exact={fragment in built}")

    print("\nR76 ALL PASS" if failures == 0 else f"\nR76 FAILURES: {failures}")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
