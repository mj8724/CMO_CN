#!/usr/bin/env python3
"""子进程统一入口：让子进程输出 UTF-8，并容错解码父进程收到的字节。

Windows 上 Python 子进程写管道时按 ANSI 代码页（cp936）编码，父进程若按 UTF-8
解码就会抛 UnicodeDecodeError（reader 线程异常）或得到乱码（见 R63）。本模块做两件事：

  * child_env()     给子进程注入 PYTHONIOENCODING=utf-8 / PYTHONUTF8=1，让它输出 UTF-8；
  * decode_output() 对收到的字节做 utf-8 → cp936 → utf-8(replace) 三级回退解码。

外来工具（csc/tasklist 等仍输出 GBK）因此不会再让门禁因编码失败而中断。

用法：
    import proc_util
    proc_util.utf8_stdout()
    p = proc_util.run([sys.executable, "-B", "scripts/verify_release.py"])
    text = proc_util.check_output(["git", "log", "--oneline", "-1"])
"""
from __future__ import annotations

import os
import subprocess
import sys

_UTF8_ENV = {"PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1"}
_DECODE_KWARGS = ("text", "universal_newlines", "encoding", "errors")


def utf8_stdout():
    """把本进程 stdout/stderr 切成 UTF-8 容错，避免打印中文时自身抛 UnicodeEncodeError。"""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except (ValueError, OSError):
            pass


def child_env(extra=None):
    """os.environ + UTF-8 强制项（+ 调用方额外变量）。"""
    env = dict(os.environ)
    env.update(_UTF8_ENV)
    if extra:
        env.update({str(k): str(v) for k, v in extra.items()})
    return env


def decode_output(data):
    """bytes → str，永不抛编码异常。"""
    if data is None:
        return ""
    if isinstance(data, str):
        return data
    for encoding in ("utf-8", "cp936"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", "replace")


def _prepare(kwargs):
    kw = dict(kwargs)
    for key in _DECODE_KWARGS:
        kw.pop(key, None)
    kw["env"] = child_env(kw.get("env"))
    if isinstance(kw.get("input"), str):
        kw["input"] = kw["input"].encode("utf-8")
    return kw


def run(cmd, **kwargs):
    """subprocess.run 的替代：捕获字节、按 UTF-8 容错解码后返回 CompletedProcess。"""
    try:
        proc = subprocess.run(cmd, **_prepare(kwargs))
    except subprocess.CalledProcessError as exc:
        exc.stdout = decode_output(exc.stdout)
        exc.stderr = decode_output(exc.stderr)
        exc.output = exc.stdout
        raise
    proc.stdout = decode_output(proc.stdout)
    proc.stderr = decode_output(proc.stderr)
    return proc


def check_output(cmd, **kwargs):
    """subprocess.check_output 的替代：返回解码后的文本（强制捕获）。"""
    kwargs.setdefault("capture_output", True)
    return run(cmd, **kwargs).stdout
