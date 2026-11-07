#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""训练场一键流程（C# 原生特征版）：

  1. collect.py      从 player-state 收集手改 timing
  2. download.py     顺序下载音频（不并行，触发 20028 会停下等人验证）
  3. C# 导出特征     OsuPlayer.exe --export-bpm-features ...（BASS + BpmFeatureBuilder，真实原生管线）
  4. train_csharp.py 只在 C# 导出的特征上训练分类器，导出/安装 ONNX
  5. C# 端到端评测   OsuPlayer.exe --eval-bpm-features ...（直接在 C# 特征上跑 BpmMl，秒级）

用法：
  py training/run_all.py                 # 全流程，训练完自动安装
  py training/run_all.py --no-install    # 只导出到 training/out
  py training/run_all.py --skip-download # 音频已就绪时跳过下载
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(ROOT)
MANIFEST = os.path.join(ROOT, "dataset", "manifest.json")
OUT = os.path.join(ROOT, "out")


def run(script: str, *args: str) -> int:
    command = [sys.executable, os.path.join(ROOT, script), *args]
    print(f"\n$ {' '.join(command)}", flush=True)
    return subprocess.call(command)


def find_app(configuration: str) -> str:
    candidates = [
        os.path.join(PROJECT, "OsuPlayer.Desktop", "bin", configuration, "net10.0", "OsuPlayer.dll"),
        os.path.join(PROJECT, "OsuPlayer.Desktop", "bin", "Debug", "net10.0", "OsuPlayer.dll"),
    ]

    for candidate in candidates:
        if os.path.exists(candidate):
            return candidate

    raise FileNotFoundError("找不到 OsuPlayer.dll，请先 dotnet build OsuPlayer.Desktop/OsuPlayer.Desktop.csproj")


def run_app(app: str, *args: str) -> int:
    command = ["dotnet", app, *args]
    print(f"\n$ {' '.join(command)}", flush=True)
    return subprocess.call(command, cwd=PROJECT)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--no-install", action="store_true")
    parser.add_argument("--skip-download", action="store_true")
    parser.add_argument("--skip-collect", action="store_true")
    parser.add_argument("--skip-eval", action="store_true")
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--configuration", default="Debug")
    args = parser.parse_args()

    if not args.skip_collect:
        code = run("collect.py")

        if code != 0:
            return code

    if not args.skip_download:
        code = run("download.py", "--limit", str(args.limit))

        if code == 42:
            print("\n下载因酷狗安全验证中止。完成验证后重新运行 py training/run_all.py 即可续传。")
            return code

        if code != 0:
            return code

    os.makedirs(OUT, exist_ok=True)
    app = find_app(args.configuration)
    features = os.path.join(OUT, "native-features.csv")
    code = run_app(app, "--export-bpm-features", MANIFEST, features)

    if code != 0:
        return code

    code = run("train_csharp.py", "--features", features, *([] if args.no_install else ["--install"]))

    if code != 0:
        return code

    if not args.skip_eval:
        code = run_app(app, "--eval-bpm-features", features, os.path.join(OUT, "csharp-eval.txt"))

    return code


if __name__ == "__main__":
    raise SystemExit(main())
