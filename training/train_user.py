#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一键训练「大众歌曲」BPM 模型（只用手动 timing 集，不与 osz 混合）。

流程：
1. 显示 CPU 逻辑线程数，让用户确认这次任务用多少线程；
2. 特征集不存在、比清单旧或指定 --refresh 时，自动调用 sync_user.py 同步；
3. 用 train_csharp.py 训练并安装到 %APPDATA%\\OsuPlayer\\models\\bpm。

用法：
    python training/train_user.py                 # 交互输入线程数
    python training/train_user.py --threads 16    # 跳过输入
    python training/train_user.py --refresh       # 强制重新同步特征
    python training/train_user.py --no-install    # 只训练，不安装
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(ROOT)
MANIFEST = os.path.join(ROOT, "dataset", "manifest.json")
FEATURES = os.path.join(ROOT, "out", "user-features.csv")
MODEL_DIR = os.path.join(ROOT, "out", "user-model")
SYNC = os.path.join(ROOT, "sync_user.py")
TRAIN = os.path.join(ROOT, "train_csharp.py")


def ask_threads(total: int) -> int:
    default = max(1, total - 4)

    while True:
        answer = input(f"训练使用线程数（1-{total}，回车默认 {default}）：").strip()

        if not answer:
            return default

        if answer.isdigit() and 1 <= int(answer) <= total:
            return int(answer)

        print(f"  请输入 1 到 {total} 之间的整数。")


def find_training_python() -> list[str]:
    """找一个装了 numpy / sklearn / onnx 的解释器（本机通常是 py -3.14）。"""
    candidates = [[sys.executable]]

    for version in ("3.14", "3.13", "3.12", "3.11"):
        candidates.append(["py", f"-{version}"])

    for command in candidates:
        try:
            probe = subprocess.run(command + ["-c", "import numpy, sklearn, onnx"],
                                   capture_output=True, text=True)
        except OSError:
            continue

        if probe.returncode == 0:
            return command

    raise SystemExit(
        "找不到装有 numpy / scikit-learn / onnx 的 Python。\n"
        "请先安装：py -3.14 -m pip install numpy scikit-learn onnx"
    )


def count_rows(path: str) -> tuple[int, int]:
    songs: set[str] = set()
    rows = 0

    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip() or line.startswith("song_id,"):
                continue

            songs.add(line.split(",", 1)[0])
            rows += 1

    return len(songs), rows


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(line_buffering=True, encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description="一键训练大众歌曲 BPM 模型")
    parser.add_argument("--threads", type=int, default=0, help="同步/导出线程数（0 = 交互输入）")
    parser.add_argument("--refresh", action="store_true", help="强制重新同步特征集")
    parser.add_argument("--no-install", action="store_true", help="只训练，不安装到播放器")
    args = parser.parse_args()

    total = os.cpu_count() or 4
    threads = args.threads if args.threads > 0 else ask_threads(total)
    threads = min(threads, total)
    print(f"\n使用 {threads} 线程（CPU 共 {total} 个逻辑线程）")

    env = dict(os.environ, PYTHONIOENCODING="utf-8")

    stale = (not os.path.exists(FEATURES)
             or (os.path.exists(MANIFEST) and os.path.getmtime(MANIFEST) > os.path.getmtime(FEATURES)))

    if args.refresh or stale:
        reason = "手动指定 --refresh" if args.refresh else "特征集缺失或比清单旧"
        print(f"\n== 同步手动 timing 集（{reason}）==")
        code = subprocess.run([sys.executable, SYNC, "--threads", str(threads)],
                              cwd=REPO, env=env).returncode

        if code != 0:
            raise SystemExit(f"同步失败（退出码 {code}）")
    else:
        print(f"\n使用已有特征集：{FEATURES}")

    if not os.path.exists(FEATURES):
        raise SystemExit("特征集不存在，且同步后仍未生成，请检查上面的日志。")

    songs, rows = count_rows(FEATURES)
    print(f"训练集：{songs} 首 / {rows} 条候选")

    python = find_training_python()
    command = python + [TRAIN, "--features", FEATURES, "--out", MODEL_DIR]

    if not args.no_install:
        command.append("--install")

    print("\n== 训练并导出 ONNX ==")
    print("$", " ".join(command))
    code = subprocess.run(command, cwd=REPO, env=env).returncode

    if code != 0:
        raise SystemExit(f"训练失败（退出码 {code}）")

    print("\n训练完成：")
    print(f"  ONNX：{os.path.join(MODEL_DIR, 'bpm.onnx')}")

    if not args.no_install:
        appdata = os.environ.get("APPDATA", "")
        print(f"  已安装：{os.path.join(appdata, 'OsuPlayer', 'models', 'bpm')}")

    print("  重新启动播放器后生效。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
