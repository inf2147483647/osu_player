#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Ballroom 全局 BPM 基线与评估（方案1 的「先复现传统算法基线」）。

用法：py baseline.py --root D:\\training [--rebuild-cache]

产出：
- %TEMP%\\bpm\\split.json   固定种子(42)的 80/20 训练/测试划分，后续训练复用同一份
- %TEMP%\\bpm\\cache\\*.npz  onset 包络 + tempo 显著度缓存（重跑不必重新解码/FFT）
- 报告：±2BPM 准确率、倍频错误率（总集/测试集/分流派）

指标的倍频容忍：|pred - k*gt| <= max(2, 4% * k*gt)，k ∈ {1, 1/2, 2, 1/3, 3}。
k=1 命中即「正确」；命中其它 k 记「倍频错误」；都不命中记「完全错误」。
"""

import argparse
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpm_common as bc

SPLIT_SEED = 42
TEST_FRACTION = 0.2


def build_index(root):
    """扫描 BallroomData/<Genre>/*.wav，配对 ballroomGroundTruth/<name>.bpm。"""
    audio_root = os.path.join(root, "BallroomData")
    gt_root = os.path.join(root, "BallroomAnnotations", "ballroomGroundTruth")
    items = []

    for genre in sorted(os.listdir(audio_root)):
        genre_dir = os.path.join(audio_root, genre)

        if not os.path.isdir(genre_dir):
            continue

        for name in sorted(os.listdir(genre_dir)):
            if not name.lower().endswith(".wav"):
                continue

            stem = os.path.splitext(name)[0]
            bpm_path = os.path.join(gt_root, stem + ".bpm")

            if not os.path.exists(bpm_path):
                print(f"跳过（无标注）：{genre}/{name}")
                continue

            with open(bpm_path) as f:
                bpm = float(f.read().strip())

            items.append({
                "name": stem,
                "genre": genre,
                "audio": os.path.join(genre_dir, name),
                "bpm": bpm,
            })

    return items


def make_split(items):
    """按流派分层、固定种子的 80/20 划分。"""
    rng = np.random.default_rng(SPLIT_SEED)
    test_names = []

    for genre in sorted({it["genre"] for it in items}):
        names = sorted(it["name"] for it in items if it["genre"] == genre)
        order = rng.permutation(len(names))
        n_test = int(round(len(names) * TEST_FRACTION))
        test_names.extend(names[i] for i in order[:n_test])

    test_set = set(test_names)

    for it in items:
        it["split"] = "test" if it["name"] in test_set else "train"

    return items


def features(item, cache_dir):
    """onset 包络 + tempo 显著度（带磁盘缓存）。"""
    path = bc.cache_path(cache_dir, item["name"])

    if os.path.exists(path):
        z = np.load(path)
        return z["onset"], float(z["fps"]), z["salience"]

    x, sr = bc.read_wav_mono(item["audio"])
    onset, fps = bc.onset_envelope(x, sr)
    salience = bc.tempo_salience(onset, fps)
    np.savez_compressed(path, onset=onset, fps=fps, salience=salience)
    return onset, fps, salience


def predict_baseline(salience):
    return float(bc.BPM_GRID[int(np.argmax(salience))])


METRICAL = (1.0, 0.5, 2.0, 1 / 3, 3.0)


def classify_error(pred, gt):
    """返回 (是否±2BPM正确, 是否倍频错误)。"""
    if abs(pred - gt) <= 2.0:
        return True, False

    for k in METRICAL:
        if k == 1.0:
            continue

        target = k * gt

        if abs(pred - target) <= max(2.0, 0.04 * target):
            return False, True

    return False, False


def report(items, predictions, title):
    correct = sum(c for _, (c, _) in [(it, classify_error(predictions[it["name"]], it["bpm"])) for it in items])
    octave = sum(
        1 for it in items
        if classify_error(predictions[it["name"]], it["bpm"])[1]
    )
    total = len(items)
    print(f"[{title}] n={total}  ±2BPM 准确率 {correct / total:.3f}（{correct}/{total}）  "
          f"倍频错误率 {octave / total:.3f}（{octave}/{total}）  完全错误 {total - correct - octave}")

    return correct / total, octave / total


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=r"D:\training")
    parser.add_argument("--rebuild-cache", action="store_true")
    args = parser.parse_args()

    work = os.path.join(os.environ["TEMP"], "bpm")
    cache_dir = os.path.join(work, "cache")

    if args.rebuild_cache and os.path.isdir(cache_dir):
        for f in os.listdir(cache_dir):
            os.remove(os.path.join(cache_dir, f))

    os.makedirs(cache_dir, exist_ok=True)

    items = build_index(args.root)
    items = make_split(items)
    print(f"共 {len(items)} 首；训练 {sum(1 for i in items if i['split'] == 'train')} / "
          f"测试 {sum(1 for i in items if i['split'] == 'test')}（seed={SPLIT_SEED}）")

    with open(os.path.join(work, "split.json"), "w", encoding="utf-8") as f:
        json.dump({
            "seed": SPLIT_SEED,
            "test_fraction": TEST_FRACTION,
            "test": sorted(i["name"] for i in items if i["split"] == "test"),
        }, f, ensure_ascii=False, indent=2)

    predictions = {}

    for k, it in enumerate(items):
        _, _, salience = features(it, cache_dir)
        predictions[it["name"]] = predict_baseline(salience)

        if (k + 1) % 100 == 0:
            print(f"  {k + 1}/{len(items)}", flush=True)

    print()
    report(items, predictions, "全部")
    report([i for i in items if i["split"] == "test"], predictions, "测试集")
    report([i for i in items if i["split"] == "train"], predictions, "训练集")

    print()
    print(f"{'流派':<22}{'n':>5}{'±2BPM':>9}{'倍频错误':>10}")
    for genre in sorted({i["genre"] for i in items}):
        group = [i for i in items if i["genre"] == genre]
        c = sum(1 for i in group if classify_error(predictions[i["name"]], i["bpm"])[0])
        o = sum(1 for i in group if classify_error(predictions[i["name"]], i["bpm"])[1])
        print(f"{genre:<22}{len(group):>5}{c / len(group):>9.3f}{o / len(group):>10.3f}")


if __name__ == "__main__":
    main()
