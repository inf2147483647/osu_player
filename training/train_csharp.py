#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""训练 C# 原生 BPM 检测模型（特征由 C# 导出，Python 只做分类器拟合与 ONNX 导出）。

为什么单独一个脚本：`tools/bpm/bpm_common.py` 是 C# `BpmFeatureBuilder` 的 Python 复刻，
复刻一旦漂移，训练出来的模型在 C# 里就会静默劣化。这里改为 **C# 自己导出特征**：

  OsuPlayer.Desktop.exe --export-bpm-features training\\dataset\\manifest.json training\\out\\native-features.csv
  py training\\train_csharp.py --install
  OsuPlayer.Desktop.exe --eval-user-bpm training\\dataset\\manifest.json training\\out\\csharp-eval.txt

第一行由 C# 跑真实推理管线（BASS 解码 → onset → 显著度 → 候选 → 14 维特征），
第二行只在给定特征上训练二分类器并导出 `bpm.onnx`，第三行用完整 C# 原生管线做端到端验收。
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import os
import sys

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, ROOT)

import train as base  # noqa: E402

from sklearn.model_selection import GroupKFold  # noqa: E402

FEATURES = os.path.join(ROOT, "out", "native-features.csv")
OUT = os.path.join(ROOT, "out")

# 倍频抑制阈值（tune_doubling.py 在 OOF 上搜出来：只向下修正，不碰半频）。
DOUBLING_PROBABILITY_MIN = 0.10
DOUBLING_GAP_MAX = 0.16


def half_index(candidates, bpm, factor):
    target = bpm / factor
    index = min(range(len(candidates)), key=lambda i: abs(candidates[i] - target))
    return index if abs(candidates[index] / target - 1) <= 0.01 else None


def choose_with_doubling(candidates, probabilities, margin=base.OVERRIDE_MARGIN):
    best = int(np.argmax(probabilities))
    chosen = best if best == 0 or probabilities[best] - probabilities[0] >= margin else 0

    for _ in range(2):
        options = []

        for factor in (2.0, 4.0):
            index = half_index(candidates, candidates[chosen], factor)

            if index is not None and index != chosen \
                    and probabilities[index] >= DOUBLING_PROBABILITY_MIN \
                    and probabilities[best] - probabilities[index] <= DOUBLING_GAP_MAX:
                options.append((probabilities[index], index))

        if not options:
            break

        chosen = max(options)[1]

    return candidates[chosen]


def load_features(path: str):
    songs: dict[str, dict] = {}
    feature_count = 0

    with open(path, "r", encoding="utf-8", newline="") as f:
        lines = [line for line in f if not line.startswith("#")]

    reader = csv.DictReader(lines)
    feature_columns = [c for c in reader.fieldnames or [] if c.startswith("f") and c[1:].isdigit()]
    feature_count = len(feature_columns)

    for row in reader:
        song_id = row["song_id"]
        song = songs.setdefault(song_id, {
            "name": song_id,
            "label": row["song_name"] or song_id,
            "bpm": float(row["truth_bpm"]),
            "candidates": [],
            "rows": [],
            "labels": [],
        })
        song["candidates"].append(float(row["candidate_bpm"]))
        song["rows"].append([float(row[c]) for c in feature_columns])
        song["labels"].append(int(row["label"]))

    for song in songs.values():
        song["features"] = np.asarray(song["rows"], dtype=np.float32)
        song["labels"] = np.asarray(song["labels"], dtype=np.int64)
        song["baseline"] = song["candidates"][0] if song["candidates"] else 120.0

    return list(songs.values()), feature_count


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--features", default=FEATURES)
    parser.add_argument("--out", default=OUT)
    parser.add_argument("--install", action="store_true")
    args = parser.parse_args()

    if not os.path.exists(args.features):
        print(f"找不到 C# 导出的特征：{args.features}")
        print("请先运行：OsuPlayer.Desktop.exe --export-bpm-features <manifest.json> <features.csv>")
        return 1

    songs, feature_count = load_features(args.features)
    print(f"C# 原生特征：{len(songs)} 首 / {feature_count} 维 / "
          f"{sum(len(s['labels']) for s in songs)} 条候选")

    if feature_count != base.bc.CANDIDATE_FEATURE_COUNT:
        print(f"特征维度不符：CSV {feature_count}，契约 {base.bc.CANDIDATE_FEATURE_COUNT}")
        return 2

    if len(songs) < 2:
        print("可用歌曲太少，无法训练。")
        return 2

    n_splits = min(5, len(songs))
    base_groups = np.concatenate([np.full(len(s["labels"]), i) for i, s in enumerate(songs)])
    base_x = np.vstack([s["features"] for s in songs])
    base_y = np.concatenate([s["labels"] for s in songs])

    predictions: dict[str, dict[str, float]] = {name: {} for name in base.make_models()}
    scores: dict[str, tuple[float, float]] = {}

    for model_name, factory in base.make_models().items():
        fold_probabilities = {s["name"]: [] for s in songs}

        for train_idx, valid_idx in GroupKFold(n_splits=n_splits).split(base_x, base_y, base_groups):
            if len(np.unique(base_y[train_idx])) < 2:
                continue

            model = factory()
            model.fit(base_x[train_idx], base_y[train_idx])

            for group in sorted({int(g) for g in base_groups[valid_idx]}):
                song = songs[group]
                fold_probabilities[song["name"]].append(model.predict_proba(song["features"])[:, 1])

        for song in songs:
            values = fold_probabilities[song["name"]]
            predictions[model_name][song["name"]] = (
                choose_with_doubling(song["candidates"], values[0]) if values else song["baseline"]
            )

        scores[model_name] = base.evaluate_user(songs, predictions[model_name])

    print(f"\n{'模型':<10}{'用户 OOF Acc1':>16}{'Acc2':>10}")

    for name, (a1, a2) in scores.items():
        print(f"{name:<10}{a1:>16.3f}{a2:>10.3f}")

    baseline = {s["name"]: s["baseline"] for s in songs}
    base_a1, base_a2 = base.evaluate_user(songs, baseline)
    print(f"{'基线(最强峰)':<10}{base_a1:>16.3f}{base_a2:>10.3f}")

    best_name = max(scores, key=lambda name: (scores[name][0], scores[name][1]))
    print(f"\n按用户 OOF 选定：{best_name}")

    final = base.make_models()[best_name]()
    final.fit(base_x, base_y)
    model_path = base.export_onnx(final, best_name, args.out, len(songs), 0)

    config_path = os.path.join(args.out, "config.json")

    with open(config_path, "r", encoding="utf-8") as f:
        config = json.load(f)

    config.update({
        "feature_source": "csharp-native",
        "source_features": os.path.abspath(args.features),
        "user_songs": len(songs),
        "doubling_probability_min": DOUBLING_PROBABILITY_MIN,
        "doubling_gap_max": DOUBLING_GAP_MAX,
    })

    with open(config_path, "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)

    report = []

    for song in songs:
        pred = predictions[best_name][song["name"]]
        report.append(
            f"{song['label']}\t真值 {song['bpm']:.1f}\t基线 {song['baseline']:.1f}\t模型 {pred:.1f}"
            f"\t{'✓' if base.acc1(pred, song['bpm']) else ('倍频' if base.acc2(pred, song['bpm']) else '✗')}"
        )

    report_path = os.path.join(args.out, "csharp-train-report.txt")

    with open(report_path, "w", encoding="utf-8") as f:
        f.write(f"训练时间：{dt.datetime.now().isoformat(timespec='seconds')}\n")
        f.write(f"特征来源：C# BpmFeatureBuilder（{os.path.abspath(args.features)}）\n")
        f.write(f"模型：{best_name}\n")
        f.write(f"用户 OOF：Acc1 {scores[best_name][0]:.3f}  Acc2 {scores[best_name][1]:.3f}\n")
        f.write(f"基线：Acc1 {base_a1:.3f}  Acc2 {base_a2:.3f}\n\n")
        f.write("\n".join(report) + "\n")

    print(f"\n已导出：{model_path}")
    print(f"配置：{config_path}")
    print(f"逐曲报告：{report_path}")

    if args.install:
        import shutil
        target = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")),
                              "OsuPlayer", "models", "bpm")
        os.makedirs(target, exist_ok=True)

        for name in ("bpm.onnx", "config.json"):
            shutil.copy2(os.path.join(args.out, name), os.path.join(target, name))

        print(f"已安装到 C# 播放器：{target}（重启后原生路径生效）")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
