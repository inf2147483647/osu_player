#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
kiai 帧分类器训练脚本（P0-2）。

输入：OsuPlayer.exe --export-kiai-data 导出的每首一个 CSV（0.5s 单元 × 40 维原始特征 + 真值标签）。
输出：kiai.onnx（sklearn 模型）+ config.json（后处理参数），拷到程序的 models/kiai 目录即可被
      KiaiMl.cs 加载（%APPDATA%\\OsuPlayer\\models\\kiai 或 <程序目录>/models/kiai）。

纪律：
- GroupKFold（按歌曲分组），同一首歌的单元绝不跨训练/验证 —— 否则评估的是「记住这首歌」。
- 逐歌 z-score、±14 单元边缘 clamp 窗口与 C# KiaiFeatureBuilder 完全同口径；
  CSV 的 `# check` 行是 C# 算好的窗口向量，这里逐文件断言两边一致。
- 后处理（游程/3s 合并/5s 最短/1s 外扩/Foote 边界吸附 ±3s）与 KiaiMl.cs 同一套参数，
  阈值在 OOF 预测上按平均 IoU 选定，再写进 config.json。
- 模型只用 logreg（曾对比单隐层 MLP，指标更差且慢 4 倍）；最终用全部数据重训。
  OOF 指标才是诚实的泛化估计，全量评估报告里的数字会偏乐观。

用法：py train_kiai.py --data %TEMP%\\kiai-data --out <模型输出目录>
"""

import argparse
import json
import os
import sys
import warnings

import numpy as np

os.environ.setdefault("OMP_NUM_THREADS", "2")
os.environ.setdefault("OPENBLAS_NUM_THREADS", "2")

from sklearn.model_selection import GroupKFold
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import average_precision_score, roc_auc_score, f1_score

warnings.filterwarnings("ignore", category=UserWarning)

# ---- 与 C# KiaiFeatureBuilder / BeatAnalyser 的契约常量 ----
RAW_DIM = 40
RADIUS = 14
WINDOW = RADIUS * 2 + 1
WINDOW_DIM = RAW_DIM * WINDOW
CELL_MS = 500
MIN_CELLS = 10
MERGE_GAP = 6
PADDING = 2
SNAP_WINDOW = 6
FOOTE_KERNEL_SIDE = 12
FOOTE_THRESHOLD_STD = 0.75
BOUNDARY_MIN_DISTANCE = 8
IOU_TOLERANCE_MS = 500.0


def load_song(path):
    """读取一个导出 CSV：原始特征矩阵、标签、check 窗口（C# 算的标准化后向量）。"""
    check_cell = None
    check_window = None
    rows = []

    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()

            if not line:
                continue

            if line.startswith("# check,"):
                parts = line.split(",")
                check_cell = int(parts[1])
                check_window = np.asarray(parts[2:], dtype=np.float64)
                continue

            if line.startswith("#") or line.startswith("time_ms,"):
                continue

            values = line.split(",")
            rows.append(np.asarray(values, dtype=np.float64))

    data = np.vstack(rows)
    times = data[:, 0]
    labels = data[:, 1].astype(np.int64)
    raw = data[:, 2:2 + RAW_DIM]

    assert raw.shape[1] == RAW_DIM, f"{path}: 特征列数 {raw.shape[1]} != {RAW_DIM}"
    assert check_window is not None and check_window.shape == (WINDOW_DIM,), \
        f"{path}: check 向量缺失或维度不对"

    return times, raw, labels, check_cell, check_window


def standardize(raw):
    """逐歌 z-score，总体标准差（ddof=0），常数维置 0 —— 对齐 C# Standardize。"""
    mean = raw.mean(axis=0)
    std = raw.std(axis=0)  # ddof=0
    z = np.zeros_like(raw)
    np.divide(raw - mean, std, out=z, where=std > 1e-8)
    return z


def build_windows(z, t):
    """±RADIUS 单元边缘 clamp，行主序先时间后特征 —— 对齐 C# BuildWindow。"""
    n = z.shape[0]
    idx = np.clip(np.arange(t - RADIUS, t + RADIUS + 1), 0, n - 1)
    return z[idx].reshape(-1)


def verify_parity(raw, check_cell, check_window, name):
    z = standardize(raw)
    mine = build_windows(z, check_cell).astype(np.float32)
    delta = np.abs(mine - check_window).max()
    assert delta < 1e-3, f"{name}: 训练/推理特征不一致（最大偏差 {delta}），先修口径再训练"


# ---- Foote 结构边界（移植自 BeatAnalyser.detectStructuralBoundaries）----

def detect_boundaries(chroma):
    n = chroma.shape[0]
    w = FOOTE_KERNEL_SIDE

    if n < 2 * w + 4:
        return []

    similarity = chroma @ chroma.T  # 行已 L2 归一化，点积即余弦
    novelty = np.zeros(n)

    for t in range(w, n - w):
        before = slice(t - w, t)
        after = slice(t, t + w)
        score = (similarity[before, before].sum()
                 + similarity[after, after].sum()
                 - similarity[before, after].sum()
                 - similarity[after, before].sum())
        novelty[t] = score

    scored = novelty[w:n - w]
    threshold = scored.mean() + FOOTE_THRESHOLD_STD * scored.std()

    candidates = []

    for t in range(w + 1, n - w - 1):
        if novelty[t] > threshold and novelty[t] >= novelty[t - 1] and novelty[t] >= novelty[t + 1]:
            candidates.append((t, novelty[t]))

    candidates.sort(key=lambda c: -c[1])
    chosen = []

    for cell, _strength in candidates:
        if all(abs(cell - e) >= BOUNDARY_MIN_DISTANCE for e in chosen):
            chosen.append(cell)

    chosen.sort()
    return chosen


def snap_edge(cell, boundaries, backward):
    best = cell
    best_distance = SNAP_WINDOW

    for boundary in boundaries:
        distance = cell - boundary if backward else boundary - cell

        if 0 <= distance <= SNAP_WINDOW and distance <= best_distance:
            best_distance = distance
            best = boundary

    return best


def postprocess(probs, boundaries, threshold, duration_cells):
    """概率 → 段落（单元区间），规则与 KiaiMl.sectionsFromProbabilities 一致。
    boundaries 由调用方按歌曲预计算（只取决于 chroma，与模型/阈值无关）。"""
    active = probs >= threshold
    runs = []
    start = None

    for i, on in enumerate(active):
        if on and start is None:
            start = i
        elif not on and start is not None:
            runs.append((start, i - 1))
            start = None

    if start is not None:
        runs.append((start, len(active) - 1))

    if not runs:
        return []

    merged = [list(runs[0])]

    for first, last in runs[1:]:
        if first - merged[-1][1] - 1 <= MERGE_GAP:
            merged[-1][1] = last
        else:
            merged.append([first, last])

    # None 表示不吸附（snap=False）；[] 表示吸附但这首歌没有可用边界。
    snap = boundaries is not None
    boundaries = boundaries or []
    sections = []

    for first, last in merged:
        if last - first + 1 < MIN_CELLS:
            continue

        if snap:
            first = snap_edge(first, boundaries, True)
            last = snap_edge(last, boundaries, False)

        start_cell = max(0, first - PADDING)
        end_cell = min(duration_cells - 1, last + 1 + PADDING)
        sections.append((start_cell, end_cell))

    return sections


def label_intervals(labels):
    """真值标签游程 → 单元区间。"""
    runs = []
    start = None

    for i, value in enumerate(labels):
        if value == 1 and start is None:
            start = i
        elif value == 0 and start is not None:
            runs.append((start, i - 1))
            start = None

    if start is not None:
        runs.append((start, len(labels) - 1))

    return runs


def cells_to_ms(sections):
    return [(first * CELL_MS, (last + 1) * CELL_MS) for first, last in sections]


def section_iou(a, b):
    """与 BeatmapTraining.intersectionOverUnion 同一口径（边界起点容差 500ms）。"""
    if not a and not b:
        return 1.0
    if not a or not b:
        return 0.0

    union = sum(end - start for start, end in a)
    union += sum(end - start for start, end in b)
    intersection = 0.0
    tol = IOU_TOLERANCE_MS

    for a_start, a_end in a:
        for b_start, b_end in b:
            # 严格照抄 C#：起点各退 500ms，终点不退（区间均为闭区间、无 null 兜底）。
            overlap = min(a_end, b_end) - max(a_start - tol, b_start - tol)

            if overlap > 0:
                intersection += overlap

    return min(1.0, max(0.0, intersection / union)) if union > 0 else 0.0


def evaluate_oof(songs, probs_by_song, threshold, snap_boundaries):
    """返回平均 IoU、段数一致率、真值0段误报率。

    注意：不要用「整曲门控」去优化 0 段误报 —— IoU 里空对空算 1.0，而 302 首里有 78 首真值
    0 段，于是「所有歌都不报段」会拿到 IoU≈78/302 的虚高分（实测过，ML 会变成什么都不检测）。
    实测 0 段歌与有 kiai 歌的逐帧/整曲置信度分布几乎重合，逐帧模型分辨不了「这首歌有没有高潮」。
    """
    ious = []
    count_matches = 0
    zero_truth = 0
    zero_truth_false_alarms = 0

    for song in songs:
        truth = cells_to_ms(label_intervals(song["labels"]))
        boundaries = song["boundaries"] if snap_boundaries else None
        cells = postprocess(probs_by_song[song["name"]], boundaries, threshold, song["n"])
        detected = cells_to_ms(cells)

        ious.append(section_iou(truth, detected))

        if len(truth) == len(detected):
            count_matches += 1

        if len(truth) == 0:
            zero_truth += 1
            if len(detected) > 0:
                zero_truth_false_alarms += 1

    return {
        "iou": float(np.mean(ious)),
        "count_match": count_matches / len(songs),
        "zero_truth_false_alarm": zero_truth_false_alarms / max(1, zero_truth),
        "zero_truth_songs": zero_truth,
    }


def make_models():
    return {
        # 1160 维窗口输入 → 线性 logreg，12 KB、可解释、lbfgs 收敛快（BLAS 多线程）。
        # 曾对比单隐层 MLP(32)：这台机器上拟合慢 4 倍且 AP/AUC/F1 与段落 IoU 全部更差
        # （AP 0.408 vs 0.471），故只保留 logreg。
        "logreg": lambda: LogisticRegression(
            C=1.0, max_iter=500, class_weight="balanced", solver="lbfgs", random_state=42),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True, help="导出 CSV 所在目录")
    parser.add_argument("--out", required=True, help="模型输出目录（kiai.onnx + config.json）")
    args = parser.parse_args()

    paths = sorted(
        os.path.join(args.data, f)
        for f in os.listdir(args.data)
        if f.endswith(".csv")
    )

    if not paths:
        sys.exit(f"{args.data} 里没有 CSV")

    songs = []

    for path in paths:
        times, raw, labels, check_cell, check_window = load_song(path)
        verify_parity(raw, check_cell, check_window, os.path.basename(path))

        z = standardize(raw)
        n = z.shape[0]
        windows = np.vstack([build_windows(z, t) for t in range(n)])

        songs.append({
            "name": os.path.basename(path),
            "z": z,
            "chroma": raw[:, 16:28].copy(),  # 已 L2 归一化的 chroma
            "labels": labels,
            "n": n,
            "windows": windows,
            # Foote 结构边界只取决于 chroma，在网格搜索前算一次。
            "boundaries": detect_boundaries(raw[:, 16:28]),
        })

    print(f"载入 {len(songs)} 首，{sum(s['n'] for s in songs)} 个单元，"
          f"正样本比例 {np.mean(np.concatenate([s['labels'] for s in songs])):.3f}")
    print("训练/推理特征一致性断言全部通过（# check 窗口逐位匹配）。")

    groups = np.concatenate([np.full(s["n"], i) for i, s in enumerate(songs)])
    x = np.vstack([s["windows"] for s in songs]).astype(np.float64)
    y = np.concatenate([s["labels"] for s in songs])

    folds = GroupKFold(n_splits=5, shuffle=True, random_state=42)
    make_model = make_models()["logreg"]

    oof = np.full(len(y), np.nan, dtype=np.float32)

    for fold, (train_idx, valid_idx) in enumerate(folds.split(x, y, groups), 1):
        groups_valid = np.unique(groups[valid_idx])
        print(f"--- fold {fold}/5（验证 {len(groups_valid)} 首，训练 {len(x) - len(valid_idx)} 单元）---",
              flush=True)

        t0 = __import__("time").monotonic()
        model = make_model()
        model.fit(x[train_idx], y[train_idx])
        oof[valid_idx] = model.predict_proba(x[valid_idx])[:, 1].astype(np.float32)
        print(f"    logreg 拟合 {__import__('time').monotonic() - t0:.0f}s", flush=True)

    frame_metrics = {
        "ap": float(average_precision_score(y, oof)),
        "auc": float(roc_auc_score(y, oof)),
        "f1@0.5": float(f1_score(y, oof >= 0.5)),
    }
    print(f"[帧级 OOF] logreg: AP={frame_metrics['ap']:.4f} "
          f"AUC={frame_metrics['auc']:.4f} F1@0.5={frame_metrics['f1@0.5']:.4f}")

    probs_by_song = {}
    offset = 0

    for song in songs:
        probs_by_song[song["name"]] = oof[offset:offset + song["n"]]
        offset += song["n"]

    # 在 OOF 上选 (阈值, 是否吸附结构边界)：最大化平均 IoU，平手看段数一致率。
    threshold_grid = np.arange(0.40, 0.86, 0.05)
    best = None

    for threshold in threshold_grid:
        for snap in (True, False):
            metrics = evaluate_oof(songs, probs_by_song, float(threshold), snap)
            key = (metrics["iou"], metrics["count_match"])

            if best is None or key > best[0]:
                best = (key, float(threshold), snap, metrics)

    _, best_threshold, best_snap, best_metrics = best

    print(f"[段落 OOF] 最优：threshold={best_threshold:.2f} snap={best_snap}")
    print(f"           平均 IoU={best_metrics['iou']:.4f}　段数一致率={best_metrics['count_match']:.3f}　"
          f"0段误报率={best_metrics['zero_truth_false_alarm']:.3f}（共 {best_metrics['zero_truth_songs']} 首真值0段）")

    # 用全部数据重训最终模型并导出 ONNX。
    print("用全部数据重训最终模型 …")
    final_model = make_model()
    final_model.fit(x, y)

    from skl2onnx import convert_sklearn
    from skl2onnx.common.data_types import FloatTensorType
    import onnx

    options = {id(final_model): {"zipmap": False}}
    onnx_model = convert_sklearn(
        final_model,
        initial_types=[("X", FloatTensorType([None, WINDOW_DIM]))],
        options=options,
    )

    os.makedirs(args.out, exist_ok=True)
    model_path = os.path.join(args.out, "kiai.onnx")

    with open(model_path, "wb") as f:
        f.write(onnx_model.SerializeToString())

    config = {
        "window_radius": RADIUS,
        "window_size": WINDOW,
        "raw_dim": RAW_DIM,
        "threshold": round(best_threshold, 4),
        "min_cells": MIN_CELLS,
        "merge_gap_cells": MERGE_GAP,
        "padding_cells": PADDING,
        "snap_boundaries": bool(best_snap),
        "model": "logreg",
        "oof_metrics": {
            **best_metrics,
            "frame": frame_metrics,
        },
    }

    with open(os.path.join(args.out, "config.json"), "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)

    size_kb = os.path.getsize(model_path) / 1024
    print(f"已写出 {model_path}（{size_kb:.0f} KB）与 config.json")


if __name__ == "__main__":
    main()
