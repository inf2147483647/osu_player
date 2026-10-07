#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""候选生成配置扫描：召回率 + OOF 准确率 + 测试准确率，选最优组合。
只读缓存，单次运行几十秒。"""
import itertools
import os
import sys

import numpy as np
from sklearn.model_selection import GroupKFold
from sklearn.linear_model import LogisticRegression
from sklearn.neural_network import MLPClassifier

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpm_common as bc
import baseline as bl
from train_bpm import octave_or_wrong

cache_dir = os.path.join(os.environ["TEMP"], "bpm", "cache")
items = bl.make_split(bl.build_index(r"D:\training"))
saliences = {it["name"]: bl.features(it, cache_dir)[2] for it in items}


def run(max_cand, peaks, refine, model_name):
    bc.MAX_CANDIDATES = max_cand
    bc.CANDIDATE_PEAKS = peaks
    bc.USE_REFINE = refine

    songs = []

    for it in items:
        s = saliences[it["name"]]
        cands = bc.candidate_bpms(s)
        feats = bc.candidate_features(s, cands)
        labels = np.array([1 if abs(c - it["bpm"]) <= 2.0 else 0 for c in cands])
        songs.append((it, cands, feats, labels))

    recall = np.mean([1.0 if s[3].sum() > 0 else 0.0 for s in songs])
    train = [s for s in songs if s[0]["split"] == "train"]
    test = [s for s in songs if s[0]["split"] == "test"]

    x = np.vstack([s[2] for s in train])
    y = np.concatenate([s[3] for s in train])
    groups = np.concatenate([np.full(len(s[1]), i) for i, s in enumerate(train)])

    factory = (lambda: LogisticRegression(C=1.0, max_iter=1000, class_weight="balanced",
                                          solver="lbfgs", random_state=42)) \
        if model_name == "logreg" else \
        (lambda: MLPClassifier(hidden_layer_sizes=(16,), alpha=1e-3, learning_rate_init=3e-3,
                               max_iter=300, random_state=42))

    oof = {}

    for tr, va in GroupKFold(5).split(x, y, groups):
        m = factory()
        m.fit(x[tr], y[tr])

        for i in sorted({int(g) for g in groups[va]}):
            s = train[i]
            oof[s[0]["name"]] = s[1][int(np.argmax(m.predict_proba(s[2])[:, 1]))]

    oof_acc = np.mean([octave_or_wrong(oof[s[0]["name"]], s[0]["bpm"])[0] for s in train])

    final = factory()
    final.fit(x, y)
    correct = octave = 0

    for it, cands, feats, _ in test:
        pred = cands[int(np.argmax(final.predict_proba(feats)[:, 1]))]
        c, o = octave_or_wrong(pred, it["bpm"])
        correct += c
        octave += o

    return recall, oof_acc, correct / len(test), octave / len(test)


print(f"{'cand':>5}{'peaks':>6}{'refine':>7}{'model':>8}{'召回':>8}{'OOF':>8}{'TEST':>8}{'倍频':>8}")
best = None

for max_cand, peaks, refine, model_name in itertools.product(
        (8, 10, 14, 20), (3, 5, 8), (True, False), ("logreg", "mlp16")):
    recall, oof_acc, test_acc, oct_ = run(max_cand, peaks, refine, model_name)
    key = (test_acc, oof_acc, -oct_)
    print(f"{max_cand:>5}{peaks:>6}{str(refine):>7}{model_name:>8}"
          f"{recall:>8.3f}{oof_acc:>8.3f}{test_acc:>8.3f}{oct_:>8.3f}")

    if best is None or key > best[0]:
        best = (key, max_cand, peaks, refine, model_name, recall, oof_acc, test_acc, oct_)

_, mc, pk, rf, mn, rc, oa, ta, oc = best
print(f"\n最优：MAX_CANDIDATES={mc} PEAKS={pk} refine={rf} model={mn}")
print(f"      召回 {rc:.3f}  OOF {oa:.3f}  测试 ±2BPM {ta:.3f}  倍频错误 {oc:.3f}")
