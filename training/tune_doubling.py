#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""调「倍频抑制」后处理规则。

用户反馈：实际使用只有「2× 倍频」错误，没有半频错误。
本脚本在 C# 原生特征的 OOF 概率上网格搜索一个保守规则：

  当模型首选 BPM 恰好是另一个候选的 2×（或 4×），且那个低候选的概率也够高、
  与首选差距不大时，改选低候选。

只调两个阈值（低候选概率下限、概率差上限），避免在小数据上过拟合。
"""

from __future__ import annotations

import argparse
import os
import sys

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, ROOT)

import train as base  # noqa: E402
import train_csharp  # noqa: E402

from sklearn.model_selection import GroupKFold  # noqa: E402


def oof_probabilities(songs, model_name):
    groups = np.concatenate([np.full(len(s["labels"]), i) for i, s in enumerate(songs)])
    x = np.vstack([s["features"] for s in songs])
    y = np.concatenate([s["labels"] for s in songs])
    probabilities = {}

    for train_idx, valid_idx in GroupKFold(n_splits=min(5, len(songs))).split(x, y, groups):
        if len(np.unique(y[train_idx])) < 2:
            continue

        model = base.make_models()[model_name]()
        model.fit(x[train_idx], y[train_idx])

        for group in sorted({int(g) for g in groups[valid_idx]}):
            song = songs[group]
            probabilities[song["name"]] = model.predict_proba(song["features"])[:, 1]

    return probabilities


def half_index(candidates, bpm, factor):
    target = bpm / factor
    best = min(range(len(candidates)), key=lambda i: abs(candidates[i] - target))
    return best if abs(candidates[best] / target - 1) <= 0.01 else None


def select(song, probabilities, lower_min, gap_max):
    candidates = song["candidates"]
    best = int(np.argmax(probabilities))
    chosen = best if best == 0 or probabilities[best] - probabilities[0] >= base.OVERRIDE_MARGIN else 0

    # 最多连续降两级（256→128→64 这种 4× 误差）。
    for _ in range(2):
        current = candidates[chosen]
        options = []

        for factor in (2.0, 4.0):
            index = half_index(candidates, current, factor)

            if index is not None and probabilities[index] >= lower_min \
                    and probabilities[best] - probabilities[index] <= gap_max:
                options.append((probabilities[index], index))

        if not options:
            break

        chosen = max(options)[1]

    return candidates[chosen]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--features", default=os.path.join(ROOT, "out", "native-features.csv"))
    args = parser.parse_args()

    songs, _ = train_csharp.load_features(args.features)
    print(f"{len(songs)} 首")

    for model_name in ("logreg", "mlp16"):
        probabilities = oof_probabilities(songs, model_name)
        base_pred = {s["name"]: select(s, probabilities[s["name"]], 2.0, -1.0) for s in songs}
        base_acc, base_acc2 = base.evaluate_user(songs, base_pred)

        best = None

        for lower_min in np.arange(0.10, 0.61, 0.02):
            for gap_max in np.arange(0.0, 0.61, 0.02):
                pred = {s["name"]: select(s, probabilities[s["name"]], lower_min, gap_max) for s in songs}
                acc, acc2 = base.evaluate_user(songs, pred)
                key = (acc, acc2, -lower_min, -gap_max)

                if best is None or key > best[0]:
                    best = (key, lower_min, gap_max, pred)

        _, lower_min, gap_max, pred = best
        acc, acc2 = base.evaluate_user(songs, pred)
        print(f"\n{model_name}: 无抑制 Acc1 {base_acc:.3f} Acc2 {base_acc2:.3f}")
        print(f"  最优规则 lower_min={lower_min:.2f} gap_max={gap_max:.2f} "
              f"→ Acc1 {acc:.3f} Acc2 {acc2:.3f}")

        for song in songs:
            old, new = base_pred[song["name"]], pred[song["name"]]

            if abs(old - new) > 0.5:
                print(f"  {song['label']}: {old:g} → {new:g}（真值 {song['bpm']:g}）")

        remaining = [
            (s["label"], s["bpm"], pred[s["name"]])
            for s in songs if not base.acc1(pred[s["name"]], s["bpm"])
        ]
        print(f"  剩余错误 {len(remaining)} 首：")
        for label, truth, got in remaining:
            print(f"    {label}: 真值 {truth:g} 预测 {got:g}")

        if model_name == "mlp16":
            print("\n倍频候选对诊断（首选 / 一半候选）：")

            for song in songs:
                probs = probabilities[song["name"]]
                best = int(np.argmax(probs))
                half = half_index(song["candidates"], song["candidates"][best], 2.0)

                if half is None:
                    continue

                print(f"  {song['label']}: 真值 {song['bpm']:g} "
                      f"首选 {song['candidates'][best]:g}(p={probs[best]:.3f}) "
                      f"一半 {song['candidates'][half]:g}(p={probs[half]:.3f}) "
                      f"gap={probs[best] - probs[half]:.3f}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
