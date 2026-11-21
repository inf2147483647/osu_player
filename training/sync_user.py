#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一键同步「手动 timing 集」：收集 → 下载缺失音频 → 导出 C# 原生特征。

与 osz 谱面集严格分离：只写 `training/dataset/` 与 `training/out/user-features.csv`，
不会动 `beatmap-features*.csv`，两套训练数据永不混合。

用法：
    python training/sync_user.py                  # 默认 核数-4 线程导出（20 核 → 16）
    python training/sync_user.py --threads 8
    python training/sync_user.py --no-download    # 只收集 + 导出，不联网下载
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
DLL = os.path.join(REPO, "OsuPlayer.Desktop", "bin", "Release", "net10.0", "OsuPlayer.dll")


def run(step: str, cmd: list[str]) -> None:
    print(f"\n== {step} ==")
    print("$", " ".join(cmd))

    # 让子进程的中文输出保持 UTF-8，避免 PowerShell 里出现乱码。
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    code = subprocess.run(cmd, cwd=REPO, env=env).returncode

    if code != 0:
        raise SystemExit(f"\n{step} 失败（退出码 {code}）")


def count_rows(path: str) -> tuple[int, int]:
    """返回 (曲目数, 候选行数)。"""
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

    parser = argparse.ArgumentParser(description="同步手动 timing 训练集")
    parser.add_argument("--threads", type=int, default=0,
                        help="特征导出线程数（默认 核数-4，至少 1）")
    parser.add_argument("--no-download", action="store_true", help="跳过音频下载")
    args = parser.parse_args()

    threads = args.threads if args.threads > 0 else max(1, (os.cpu_count() or 4) - 4)

    if not os.path.exists(DLL):
        raise SystemExit(
            "找不到 Release 构建：\n  " + DLL +
            "\n请先运行：dotnet build OsuPlayer.Desktop\\OsuPlayer.Desktop.csproj -c Release"
        )

    run("1/3 收集手改 timing", [sys.executable, os.path.join(ROOT, "collect.py")])

    if args.no_download:
        print("\n== 2/3 下载缺失音频：已跳过（--no-download）==")
    else:
        run("2/3 下载缺失音频", [sys.executable, os.path.join(ROOT, "download.py")])

    run("3/3 导出 18 维原生特征",
        ["dotnet", DLL, "--export-bpm-features", MANIFEST, FEATURES,
         "--export-threads", str(threads)])

    songs, rows = count_rows(FEATURES)
    print("\n同步完成：")
    print(f"  特征集：{FEATURES}")
    print(f"  曲目 {songs} 首 / 候选 {rows} 条（{threads} 线程导出）")
    print("  下一步：python training/train_user.py")

    return 0


if __name__ == "__main__":
    sys.exit(main())
