#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用用户手改的 timing 训练全局 BPM 候选评分模型，并导出播放器可直接加载的 ONNX。

特征口径完全复用 `tools/bpm/bpm_common.py`（与 C# 的 `BpmFeatureBuilder` 逐位对应）：
    onset 包络 → 自相关 → tempo 显著度 → 候选集 → 14 维逐候选特征 → 「是否是该曲的正确 BPM」二分类。

真值：用户手改 timing 的第一条红线（多处不同 BPM 的曲子默认排除）。
训练：按歌曲分组 GroupKFold 做 OOF 选模型；可选把 Ballroom 数据作为额外训练行（避免 100 多首样本过拟合），
但用户数据的 OOF 才是模型选择依据。导出：`training/out/bpm.onnx` + `config.json`，加 `--install` 直接装到播放器。

用法：
  py training/train.py                 # 仅用户数据
  py training/train.py --ballroom D:\training
  py training/train.py --install       # 训练完写入 %APPDATA%/OsuPlayer/models/bpm
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import shutil
import subprocess
import sys
import tempfile
import wave

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.join(os.path.dirname(ROOT), "tools", "bpm")
sys.path.insert(0, TOOLS)

import bpm_common as bc  # noqa: E402
import baseline as bl  # noqa: E402

from sklearn.linear_model import LogisticRegression  # noqa: E402
from sklearn.model_selection import GroupKFold  # noqa: E402
from sklearn.neural_network import MLPClassifier  # noqa: E402

MANIFEST = os.path.join(ROOT, "dataset", "manifest.json")
CACHE = os.path.join(ROOT, "dataset", "cache")
OUT = os.path.join(ROOT, "out")
OVERRIDE_MARGIN = 0.10


def acc1(pred: float, gt: float) -> bool:
    return abs(pred - gt) <= 0.04 * gt


def acc2(pred: float, gt: float) -> bool:
    return min(abs(pred * k - gt) for k in (1.0, 2.0, 0.5, 3.0, 1 / 3)) <= 0.04 * gt


def find_ffmpeg(explicit: str | None) -> str | None:
    candidates = [
        explicit,
        os.environ.get("FFMPEG"),
        shutil.which("ffmpeg"),
        r"C:\Program Files\ClassIsland2\data\Cache\SystemTools\ffmpeg.exe",
    ]

    for candidate in candidates:
        if candidate and os.path.exists(candidate):
            return candidate

    return None


def read_audio(path: str, ffmpeg: str | None) -> tuple[np.ndarray, int]:
    if path.lower().endswith(".wav"):
        return bc.read_wav_mono(path)

    if ffmpeg is None:
        raise RuntimeError("需要 ffmpeg 解码 mp3，但系统里没找到；请安装 ffmpeg 或设置 FFMPEG 环境变量")

    with tempfile.TemporaryDirectory() as temp:
        target = os.path.join(temp, "audio.wav")
        command = [
            ffmpeg, "-y", "-loglevel", "error", "-i", path,
            "-ac", "1", "-ar", "44100", "-sample_fmt", "s16", target,
        ]
        subprocess.run(command, check=True, capture_output=True)
        return bc.read_wav_mono(target)


def features_for(cache_key: str, audio: str, ffmpeg: str | None):
    stamp = f"{os.path.getsize(audio)}-{int(os.path.getmtime(audio))}"
    cache_file = bc.cache_path(CACHE, f"{cache_key}-{stamp}")

    if os.path.exists(cache_file):
        data = np.load(cache_file)
        return data["onset"], float(data["fps"]), data["salience"]

    samples, sample_rate = read_audio(audio, ffmpeg)
    onset, fps = bc.onset_envelope(samples, sample_rate)
    salience = bc.tempo_salience(onset, fps)
    os.makedirs(CACHE, exist_ok=True)
    np.savez_compressed(cache_file, onset=onset, fps=fps, salience=salience)
    return onset, fps, salience


def load_user_songs(manifest_path: str, ffmpeg: str | None):
    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    songs = []
    skipped = []

    for entry in manifest["songs"]:
        audio = entry.get("audio") or {}
        path = audio.get("path")

        if audio.get("status") not in ("local", "downloaded") or not path or not os.path.exists(path):
            skipped.append((entry, "音频未就绪"))
            continue

        if entry.get("variable_tempo"):
            skipped.append((entry, "多 BPM（不适合全局 BPM 训练）"))
            continue

        try:
            _, _, salience = features_for(entry["id"], path, ffmpeg)
        except Exception as e:
            skipped.append((entry, f"特征提取失败：{e}"))
            continue

        candidates = bc.candidate_bpms(salience)

        if not candidates:
            skipped.append((entry, "没有候选 BPM"))
            continue

        labels = np.array([1 if acc1(c, entry["bpm"]) else 0 for c in candidates], dtype=np.int64)

        songs.append({
            "name": entry["id"],
            "label": entry.get("name") or entry["id"],
            "bpm": float(entry["bpm"]),
            "candidates": candidates,
            "features": bc.candidate_features(salience, candidates),
            "labels": labels,
            "baseline": float(bc.BPM_GRID[int(np.argmax(salience))]),
        })

    return songs, skipped, manifest


def load_ballroom(root: str):
    try:
        items = bl.make_split(bl.build_index(root))
    except FileNotFoundError:
        print(f"Ballroom 数据不存在，忽略：{root}")
        return []

    rows = []

    for item in items:
        try:
            _, _, salience = bl.features(item, os.path.join(os.environ["TEMP"], "bpm", "cache"))
        except Exception:
            continue

        candidates = bc.candidate_bpms(salience)

        if not candidates:
            continue

        rows.append({
            "name": "ballroom:" + item["name"],
            "bpm": float(item["bpm"]),
            "candidates": candidates,
            "features": bc.candidate_features(salience, candidates),
            "labels": np.array([1 if acc1(c, item["bpm"]) else 0 for c in candidates], dtype=np.int64),
            "split": item["split"],
        })

    return rows


def choose(candidates, probabilities, margin=OVERRIDE_MARGIN):
    best = int(np.argmax(probabilities))

    if best == 0 or probabilities[best] - probabilities[0] >= margin:
        return candidates[best]

    return candidates[0]


def make_models():
    return {
        "logreg": lambda: LogisticRegression(
            C=1.0, max_iter=1000, class_weight="balanced", solver="lbfgs", random_state=42),
        "mlp16": lambda: MLPClassifier(
            hidden_layer_sizes=(16,), alpha=1e-3, learning_rate_init=3e-3,
            max_iter=300, random_state=42),
    }


def evaluate_user(songs, predictions) -> tuple[float, float]:
    if not songs:
        return 0.0, 0.0

    a1 = sum(1 for s in songs if acc1(predictions[s["name"]], s["bpm"])) / len(songs)
    a2 = sum(1 for s in songs if acc2(predictions[s["name"]], s["bpm"])) / len(songs)
    return a1, a2


def train_oof(user_songs, extra_train_rows):
    """用户歌曲按歌分组做 OOF；每一折都额外带上 Ballroom 训练行。"""
    predictions = {name: {} for name in make_models()}
    n_splits = min(5, len(user_songs))

    if n_splits < 2:
        return predictions, {}

    # 一次性拼好所有行，OOF 的验证集只从用户歌曲里取。
    all_features = []
    all_labels = []
    all_groups = []

    for index, song in enumerate(user_songs):
        all_features.append(song["features"])
        all_labels.append(song["labels"])
        all_groups.append(np.full(len(song["labels"]), index))

    base_x = np.vstack(all_features)
    base_y = np.concatenate(all_labels)
    base_groups = np.concatenate(all_groups)

    extra_x = extra_y = None

    if extra_train_rows:
        extra_x = np.vstack([row["features"] for row in extra_train_rows])
        extra_y = np.concatenate([row["labels"] for row in extra_train_rows])

    scores = {}

    for name, factory in make_models().items():
        fold_predictions = {song["name"]: [] for song in user_songs}

        for train_idx, valid_idx in GroupKFold(n_splits=n_splits).split(base_x, base_y, base_groups):
            x = base_x[train_idx]
            y = base_y[train_idx]

            if extra_x is not None:
                x = np.vstack([x, extra_x])
                y = np.concatenate([y, extra_y])

            model = factory()

            if len(np.unique(y)) < 2:
                # 某一折里全是同一类标签时分类器无法拟合，跳过该折（极少数小样本情况）。
                continue

            model.fit(x, y)

            for group in sorted({int(g) for g in base_groups[valid_idx]}):
                song = user_songs[group]
                probabilities = model.predict_proba(song["features"])[:, 1]
                fold_predictions[song["name"]].append(choose(song["candidates"], probabilities))

        for song in user_songs:
            values = fold_predictions[song["name"]]
            predictions[name][song["name"]] = values[0] if values else song["baseline"]

        scores[name] = evaluate_user(user_songs, predictions[name])

    return predictions, scores


def export_onnx(model, model_name: str, out_dir: str, user_count: int, ballroom_count: int) -> str:
    from skl2onnx import convert_sklearn
    from skl2onnx.common.data_types import FloatTensorType

    onnx_model = convert_sklearn(
        model,
        initial_types=[("X", FloatTensorType([None, bc.CANDIDATE_FEATURE_COUNT]))],
        options={id(model): {"zipmap": False}},
    )

    os.makedirs(out_dir, exist_ok=True)
    model_path = os.path.join(out_dir, "bpm.onnx")

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
        "model": model_name,
        "override_margin": OVERRIDE_MARGIN,
        "trained_at": dt.datetime.now().isoformat(timespec="seconds"),
        "user_songs": user_count,
        "ballroom_songs": ballroom_count,
    }

    with open(os.path.join(out_dir, "config.json"), "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)

    return model_path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", default=MANIFEST)
    parser.add_argument("--out", default=OUT)
    parser.add_argument("--ballroom", default=r"D:\training",
                        help="Ballroom 根目录（含 BallroomData / BallroomAnnotations），传 none 关闭")
    parser.add_argument("--install", action="store_true", help="导出后复制到 %%APPDATA%%/OsuPlayer/models/bpm")
    parser.add_argument("--ffmpeg", default=None)
    args = parser.parse_args()

    if not os.path.exists(args.manifest):
        print(f"清单不存在：{args.manifest}\n请先运行 py training/collect.py")
        return 1

    ffmpeg = find_ffmpeg(args.ffmpeg)
    print(f"ffmpeg：{ffmpeg or '未找到（只能训练 wav）'}")

    user_songs, skipped, manifest = load_user_songs(args.manifest, ffmpeg)
    print(f"用户数据：可用 {len(user_songs)} 首，跳过 {len(skipped)} 首")

    for entry, reason in skipped[:10]:
        print(f"  跳过 {entry.get('name') or entry['id']}：{reason}")

    if len(user_songs) < 2:
        print("可用样本太少，无法训练。请先在播放器里为更多歌曲手动调整并保存 timing。")
        return 2

    ballroom = []

    if args.ballroom and args.ballroom.lower() != "none":
        ballroom = load_ballroom(args.ballroom)
        print(f"Ballroom 额外训练行：{len(ballroom)} 首")

    print("提取候选特征并做 OOF ...")
    predictions, scores = train_oof(user_songs, ballroom)

    print()
    print(f"{'模型':<10}{'用户 OOF Acc1':>16}{'Acc2':>10}")

    for name, (a1, a2) in scores.items():
        print(f"{name:<10}{a1:>16.3f}{a2:>10.3f}")

    base_pred = {song["name"]: song["baseline"] for song in user_songs}
    base_a1, base_a2 = evaluate_user(user_songs, base_pred)
    print(f"{'基线(最强峰)':<10}{base_a1:>16.3f}{base_a2:>10.3f}")

    best_name = max(scores, key=lambda name: (scores[name][0], scores[name][1]))
    print(f"\n按用户 OOF 选定：{best_name}")

    # 全量重训：用户全部 + Ballroom 训练集（Ballroom 测试集留作独立体检）。
    x = np.vstack([song["features"] for song in user_songs])
    y = np.concatenate([song["labels"] for song in user_songs])
    used_ballroom = [row for row in ballroom if row.get("split") == "train"]

    if used_ballroom:
        x = np.vstack([x, np.vstack([row["features"] for row in used_ballroom])])
        y = np.concatenate([y, np.concatenate([row["labels"] for row in used_ballroom])])

    final = make_models()[best_name]()
    final.fit(x, y)

    model_path = export_onnx(final, best_name, args.out, len(user_songs), len(used_ballroom))

    if ballroom:
        test_rows = [row for row in ballroom if row.get("split") == "test"]

        if test_rows:
            a1 = a2 = 0

            for row in test_rows:
                probabilities = final.predict_proba(row["features"])[:, 1]
                pred = choose(row["candidates"], probabilities)
                a1 += acc1(pred, row["bpm"])
                a2 += acc2(pred, row["bpm"])

            a1 /= len(test_rows)
            a2 /= len(test_rows)
            print(f"Ballroom 测试集体检（未参与训练）：Acc1 {a1:.3f}  Acc2 {a2:.3f}（n={len(test_rows)}）")

    report = []

    for song in user_songs:
        pred = predictions[best_name][song["name"]]
        report.append(
            f"{song['label']}\t真值 {song['bpm']:.1f}\t基线 {song['baseline']:.1f}\t模型 {pred:.1f}"
            f"\t{'✓' if acc1(pred, song['bpm']) else ('倍频' if acc2(pred, song['bpm']) else '✗')}"
        )

    report_path = os.path.join(args.out, "report.txt")

    with open(report_path, "w", encoding="utf-8") as f:
        f.write(f"训练时间：{dt.datetime.now().isoformat(timespec='seconds')}\n")
        f.write(f"用户样本：{len(user_songs)} 首；Ballroom 训练行：{len(used_ballroom)} 首\n")
        f.write(f"模型：{best_name}\n")
        f.write(f"用户 OOF：Acc1 {scores[best_name][0]:.3f}  Acc2 {scores[best_name][1]:.3f}\n")
        f.write(f"基线：Acc1 {base_a1:.3f}  Acc2 {base_a2:.3f}\n\n")
        f.write("\n".join(report) + "\n")

    print(f"\n已导出：{model_path}")
    print(f"配置：{os.path.join(args.out, 'config.json')}")
    print(f"逐曲报告：{report_path}")

    if args.install:
        target = os.path.join(
            os.environ.get("APPDATA", os.path.expanduser("~")), "OsuPlayer", "models", "bpm"
        )
        os.makedirs(target, exist_ok=True)

        for name in ("bpm.onnx", "config.json"):
            shutil.copy2(os.path.join(args.out, name), os.path.join(target, name))

        print(f"已安装到播放器：{target}（重启播放器生效）")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
