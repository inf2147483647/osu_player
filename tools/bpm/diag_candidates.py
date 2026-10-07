#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""诊断候选集召回：真值 BPM 与自相关峰的 metrical 关系。
只读缓存，不训练。"""
import collections
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpm_common as bc
import baseline as bl

root = r"D:\training"
cache_dir = os.path.join(os.environ["TEMP"], "bpm", "cache")
items = bl.make_split(bl.build_index(root))

cur_recall = 0
ratios = collections.Counter()
expanded_recall = 0
missing_rank = []

EXPAND_RATIOS = (1.0, 2.0, 0.5, 3.0, 1 / 3, 4.0, 0.25, 1.5, 2 / 3)

for it in items:
    _, _, salience = bl.features(it, cache_dir)
    gt = it["bpm"]

    cands = bc.candidate_bpms(salience)
    hit = any(abs(c - gt) <= 2.0 for c in cands)
    cur_recall += hit

    best = float(bc.BPM_GRID[int(np.argmax(salience))])
    r = gt / best if best > 0 else 0
    ratios[min((1.0, 2.0, 0.5, 3.0, 1 / 3, 4.0, 0.25), key=lambda k: abs(r - k))] += 1

    # 扩大候选：top-3 峰的 9 种 metrical 亲属
    peaks = bc.find_peaks(salience, 8)
    pool = set()

    for i in peaks[:3]:
        b = float(bc.BPM_GRID[i])

        for k in EXPAND_RATIOS:
            v = b * k

            if bc.BPM_MIN <= v <= bc.BPM_MAX:
                pool.add(round(v, 1))

    expanded_recall += any(abs(p - gt) <= 2.0 for p in pool)

    if not hit:
        # 真值在显著度曲线里排第几（前多少比例）
        missing_rank.append(float((salience > bc.salience_at(salience, gt)).mean()))

n = len(items)
print(f"当前候选规则召回：{cur_recall}/{n} = {cur_recall / n:.3f}")
print(f"扩大候选（top3 峰 × 9 种亲属）召回：{expanded_recall}/{n} = {expanded_recall / n:.3f}")
print(f"\nargmax/真值 的 metrical 比分布：{dict(ratios.most_common())}")
print(f"\n未召回曲目中，真值显著度的分位（越小越显著）：中位 {np.median(missing_rank):.3f}  "
      f"p25 {np.percentile(missing_rank, 25):.3f}  p75 {np.percentile(missing_rank, 75):.3f}")

# 未召回曲目里，真值离「最近的峰」是什么关系？如果多是任意比（非 metrical），
# 说明前端显著度根本没抓住真值所在的节拍层级。
miss_ratios = collections.Counter()

for it in items:
    _, _, salience = bl.features(it, cache_dir)
    gt = it["bpm"]

    if any(abs(c - gt) <= 2.0 for c in bc.candidate_bpms(salience)):
        continue

    peaks = bc.find_peaks(salience, int(round(4.0 / bc.BPM_STEP)))
    best_r = None

    for i in peaks[:8]:
        b = float(bc.BPM_GRID[i])

        if b <= 0:
            continue

        r = gt / b
        snapped = min((1.0, 2.0, 0.5, 3.0, 1 / 3, 4.0, 0.25, 1.5, 2 / 3), key=lambda k: abs(r - k))
        err = abs(r - snapped)

        if best_r is None or err < best_r[0]:
            best_r = (err, snapped, r)

    if best_r is None:
        miss_ratios["无峰"] += 1
    elif best_r[0] <= 0.03:
        miss_ratios[f"metrical {best_r[1]:.2f}"] += 1
    else:
        miss_ratios["非 metrical"] += 1

print(f"\n未召回曲目与最近峰的关系：{dict(miss_ratios.most_common())}")
