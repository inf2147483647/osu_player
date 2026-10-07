#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""阶段 5 后处理验证：保守覆盖策略。

现象：模型把 Quickstep（本来就对，基线 Acc1 0.750）压到 0.062 —— 说明
「按概率 argmax 无条件覆盖显著度最强的候选」过于激进。
这里扫描：只有当模型概率比最强候选的优势超过 margin 时才覆盖，否则保留显著度最优解。
"""
import os
import sys

import numpy as np
from sklearn.neural_network import MLPClassifier

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpm_common as bc
import baseline as bl
from train_bpm import acc1, acc2

cache_dir = os.path.join(os.environ["TEMP"], "bpm", "cache")
items = bl.make_split(bl.build_index(r"D:\training"))

songs = []

for it in items:
    _, _, salience = bl.features(it, cache_dir)
    cands = bc.candidate_bpms(salience)
    songs.append({
        "item": it,
        "cands": cands,
        "features": bc.candidate_features(salience, cands),
    })

train = [s for s in songs if s["item"]["split"] == "train"]
test = [s for s in songs if s["item"]["split"] == "test"]

x = np.vstack([s["features"] for s in train])
y = np.concatenate([np.array([1 if abs(c - s["item"]["bpm"]) <= 0.04 * s["item"]["bpm"] else 0
                              for c in s["cands"]]) for s in train])

model = MLPClassifier(hidden_layer_sizes=(16,), alpha=1e-3, learning_rate_init=3e-3,
                      max_iter=300, random_state=42)
model.fit(x, y)


def predict(s, margin):
    probs = model.predict_proba(s["features"])[:, 1]
    best = int(np.argmax(probs))

    # 候选按显著度降序，所以 0 号就是传统基线的选择。
    if margin < 0 or best == 0 or probs[best] - probs[0] >= margin:
        return s["cands"][best]

    return s["cands"][0]


print(f"{'margin':>8}{'Acc1':>8}{'Acc2':>8}")
for margin in (-1.0, 0.0, 0.05, 0.10, 0.20, 0.30, 0.50, 0.99):
    preds = {s["item"]["name"]: predict(s, margin) for s in test}
    a1 = np.mean([acc1(preds[s["item"]["name"]], s["item"]["bpm"]) for s in test])
    a2 = np.mean([acc2(preds[s["item"]["name"]], s["item"]["bpm"]) for s in test])
    print(f"{margin:>8.2f}{a1:>8.3f}{a2:>8.3f}")
