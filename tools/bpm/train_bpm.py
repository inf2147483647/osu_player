#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Ballroom 全局 BPM：候选评分模型训练 + ONNX 导出。

思路：不直接回归 BPM（回归对倍频错误无能为力），而是
    tempo 显著度曲线 → 候选集（峰 ∪ 倍频亲属）→ 逐候选二分类「它是不是正确节拍」
    → 取概率最高的候选。
基线 0.509 的错误是倍频错，本质是「层级选错」，这正是逐候选打分能学的东西。

纪律（与 kiai 管线一致）：
- 按歌曲分组做 GroupKFold，同一首歌的候选绝不跨折。
- 特征口径由 bpm_common 定义，C# 端必须逐位复现。
- 模型选择看 OOF，测试集只在最后看一次。

用法：py train_bpm.py [--root D:\\training] [--out %TEMP%\\bpm-model]
"""

import argparse
import json
import os
import sys

import numpy as np
from sklearn.model_selection import GroupKFold
from sklearn.linear_model import LogisticRegression
from sklearn.neural_network import MLPClassifier

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpm_common as bc
import baseline as bl

METRICAL = (2.0, 0.5, 3.0, 1 / 3)


def acc1(pred, gt):
    """任务书定义：|pred - gt| <= 4% · gt。"""
    return abs(pred - gt) <= 0.04 * gt


def acc2(pred, gt):
    """任务书定义：min(|pred-gt|, |pred·2-gt|, |pred/2-gt|, |pred·3-gt|, |pred/3-gt|) <= 4% · gt。"""
    return min(abs(pred * k - gt) for k in (1.0, 2.0, 0.5, 3.0, 1 / 3)) <= 0.04 * gt


def legacy(pred, gt):
    """旧口径（绝对 ±2BPM / 倍频错误），保留用于和前面几轮结果对照。"""
    if abs(pred - gt) <= 2.0:
        return True, False

    for k in METRICAL:
        target = k * gt

        if abs(pred - target) <= max(2.0, 0.04 * target):
            return False, True

    return False, False


def build_rows(items, cache_dir):
    """→ 每首歌的候选 BPM、特征矩阵、标签，以及真值 BPM。"""
    songs = []

    for it in items:
        _, _, salience = bl.features(it, cache_dir)
        candidates = bc.candidate_bpms(salience)
        features = bc.candidate_features(salience, candidates)
        labels = np.array([1 if acc1(c, it["bpm"]) else 0 for c in candidates], dtype=np.int64)
        songs.append({
            "name": it["name"],
            "split": it["split"],
            "bpm": it["bpm"],
            "candidates": candidates,
            "features": features,
            "labels": labels,
        })

    return songs


def evaluate(songs, predictions, title):
    n = len(songs)
    a1 = sum(1 for s in songs if acc1(predictions[s["name"]], s["bpm"])) / n
    a2 = sum(1 for s in songs if acc2(predictions[s["name"]], s["bpm"])) / n
    legacy_correct = legacy_octave = 0

    for s in songs:
        c, o = legacy(predictions[s["name"]], s["bpm"])
        legacy_correct += c
        legacy_octave += o

    print(f"[{title}] n={n}  Acc1 {a1:.3f}（{round(a1 * n)}/{n}）  Acc2 {a2:.3f}  "
          f"｜ 旧口径 ±2BPM {legacy_correct / n:.3f}  倍频错误 {legacy_octave / n:.3f}")
    return a1, a2


# 保守覆盖阈值：模型的概率优势不足时不推翻「显著度最强候选」。
# 实测模型会把本就选对的 Quickstep 压到 Acc1 0.06，加这个阈值后 Acc1 0.717 → 0.754。
OVERRIDE_MARGIN = 0.10


def choose(candidates, probs, margin=OVERRIDE_MARGIN):
    """候选按显著度降序，0 号即传统基线解。模型的优势不足 margin 时保留 0 号。"""
    best = int(np.argmax(probs))

    if best == 0 or probs[best] - probs[0] >= margin:
        return candidates[best]

    return candidates[0]


def pick(songs, model):
    return {s["name"]: choose(s["candidates"], model.predict_proba(s["features"])[:, 1])
            for s in songs}


def make_models():
    return {
        "logreg": lambda: LogisticRegression(
            C=1.0, max_iter=1000, class_weight="balanced", solver="lbfgs", random_state=42),
        "mlp16": lambda: MLPClassifier(
            hidden_layer_sizes=(16,), alpha=1e-3, learning_rate_init=3e-3,
            max_iter=300, random_state=42),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=r"D:\training")
    parser.add_argument("--out", default=os.path.join(os.environ["TEMP"], "bpm-model"))
    args = parser.parse_args()

    work = os.path.join(os.environ["TEMP"], "bpm")
    cache_dir = os.path.join(work, "cache")
    items = bl.make_split(bl.build_index(args.root))
    songs = build_rows(items, cache_dir)

    train = [s for s in songs if s["split"] == "train"]
    test = [s for s in songs if s["split"] == "test"]

    # 诊断：正确节拍是否在候选集里（模型的能力上限）。
    recall = np.mean([1.0 if s["labels"].sum() > 0 else 0.0 for s in songs])
    n_cand = np.mean([len(s["candidates"]) for s in songs])
    print(f"候选集召回（真值在候选内）：{recall:.3f}   平均候选数 {n_cand:.2f}")

    # OOF（按歌分组）做模型选择。
    x = np.vstack([s["features"] for s in train])
    y = np.concatenate([s["labels"] for s in train])
    groups = np.concatenate([np.full(len(s["candidates"]), i) for i, s in enumerate(train)])
    print(f"训练候选行 {len(y)}（正样本 {y.sum()}，占 {y.mean():.3f}）")

    oof_pred = {name: {} for name in make_models()}

    for name, factory in make_models().items():
        for fold, (tr, va) in enumerate(GroupKFold(5).split(x, y, groups), 1):
            model = factory()
            model.fit(x[tr], y[tr])
            fold_songs = sorted({int(g) for g in groups[va]})

            for i in fold_songs:
                s = train[i]
                oof_pred[name][s["name"]] = choose(
                    s["candidates"], model.predict_proba(s["features"])[:, 1])

    print()
    scores = {}

    for name in make_models():
        scores[name] = evaluate(train, oof_pred[name], f"OOF（{name}）")

    best_name = max(scores, key=lambda n: scores[n])
    print(f"\n按 OOF 选定模型：{best_name}")

    # 全训练集重训 → 测试集（只看一次）。
    print()
    final = make_models()[best_name]()
    final.fit(x, y)
    test_pred = pick(test, final)
    evaluate(test, test_pred, "测试集")
    evaluate(songs, {**pick(train, final), **test_pred}, "全量（训练集为拟合值，仅供参考）")

    # 与基线对比（同一测试集）。
    baseline_pred = {}

    for s in songs:
        _, _, salience = bl.features(next(i for i in items if i["name"] == s["name"]), cache_dir)
        baseline_pred[s["name"]] = float(bc.BPM_GRID[int(np.argmax(salience))])

    print()
    evaluate(test, baseline_pred, "基线（测试集）")

    # 分流派误差分析（对应任务书阶段 5 的「倍频错误分析」）。
    print()
    print(f"{'流派':<22}{'n':>5}{'Acc1':>8}{'Acc2':>8}{'基线Acc1':>10}")
    genre_of = {i["name"]: i["genre"] for i in items}

    for genre in sorted(set(genre_of.values())):
        group = [s for s in test if genre_of[s["name"]] == genre]

        if not group:
            continue

        m1 = sum(1 for s in group if acc1(test_pred[s["name"]], s["bpm"])) / len(group)
        m2 = sum(1 for s in group if acc2(test_pred[s["name"]], s["bpm"])) / len(group)
        b1 = sum(1 for s in group if acc1(baseline_pred[s["name"]], s["bpm"])) / len(group)
        print(f"{genre:<22}{len(group):>5}{m1:>8.3f}{m2:>8.3f}{b1:>10.3f}")

    # 导出 ONNX。
    from skl2onnx import convert_sklearn
    from skl2onnx.common.data_types import FloatTensorType

    onnx_model = convert_sklearn(
        final,
        initial_types=[("X", FloatTensorType([None, bc.CANDIDATE_FEATURE_COUNT]))],
        options={id(final): {"zipmap": False}},
    )

    os.makedirs(args.out, exist_ok=True)
    model_path = os.path.join(args.out, "bpm.onnx")

    with open(model_path, "wb") as f:
        f.write(onnx_model.SerializeToString())

    config = {
        "bpm_min": bc.BPM_MIN,
        "bpm_max": bc.BPM_MAX,
        "bpm_step": bc.BPM_STEP,
        "fft_n": bc.FFT_N,
        "hop": bc.HOP,
        "harmonics": bc.HARMONICS,
        "max_candidates": bc.MAX_CANDIDATES,
        "min_candidate_separation": bc.MIN_CANDIDATE_SEPARATION,
        "metrical_ratios": list(bc.METRICAL_RATIOS),
        "feature_names": list(bc.CANDIDATE_FEATURE_NAMES),
        "model": best_name,
        "override_margin": OVERRIDE_MARGIN,
    }

    with open(os.path.join(args.out, "config.json"), "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)

    print(f"\n已写出 {model_path}（{os.path.getsize(model_path) / 1024:.0f} KB）与 config.json")


if __name__ == "__main__":
    main()
