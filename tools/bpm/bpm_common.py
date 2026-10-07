#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""全局 BPM 估计的特征契约（Python 侧）。

这里的每个常量与算法都必须能被 C# 端逐位复现（`BpmFeatureBuilder.cs`），
否则训练出来的 ONNX 在播放器里会静默劣化 —— 与 kiai 管线同一套纪律：
特征口径是硬契约，不是实现细节。

管线：
    wav(float, mono) → 分帧 STFT(1024/hop256, Hann) → log 幅度
    → 逐帧半波整流频谱通量 → 去局部均值 + 归一化 = onset 包络 o[t]
    → 无偏自相关 r[lag]
    → 谐波求和得到 tempo 显著度曲线（BPM 网格 40–260，步长 0.5）

只依赖 numpy（本机 Python 3.14 装不了 numba/librosa，也用不上）。
"""

import os
import wave

import numpy as np

# ---- 与 C# 的契约常量 ----
FFT_N = 1024
HOP = 256
BPM_MIN = 40.0
BPM_MAX = 260.0
BPM_STEP = 0.5
HARMONICS = 4

# tempo 显著度的 BPM 网格（所有下游模型/评估共用同一条曲线）
BPM_GRID = np.arange(BPM_MIN, BPM_MAX + 1e-9, BPM_STEP)


def read_wav_mono(path):
    """读 16-bit PCM wav 为 float32 单声道 + 采样率。Ballroom 全是 44.1k/16bit/mono，
    这里只做必要校验，遇到别的位深直接报错而不是猜。"""
    with wave.open(path, "rb") as f:
        channels = f.getnchannels()
        width = f.getsampwidth()
        sr = f.getframerate()
        raw = f.readframes(f.getnframes())

    if width != 2:
        raise ValueError(f"{path}: 只支持 16-bit PCM，实际 {width * 8}-bit")

    x = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0

    if channels > 1:
        x = x.reshape(-1, channels).mean(axis=1)

    return x, sr


def onset_envelope(x, sr):
    """频谱通量 onset 包络。返回 (o, fps)。"""
    n = FFT_N
    frames = 1 + (len(x) - n) // HOP

    if frames < 4:
        return np.zeros(1, dtype=np.float32), sr / HOP

    # 分帧：用 stride 视图避免复制（内存友好），乘窗后再拷贝。
    idx = np.arange(n)[None, :] + HOP * np.arange(frames)[:, None]
    windowed = x[idx] * np.hanning(n).astype(np.float32)
    mag = np.abs(np.fft.rfft(windowed, axis=1)).astype(np.float32)

    logmag = np.log1p(mag)

    # 逐帧半波整流频谱通量（所有频点求和）。
    diff = np.diff(logmag, axis=0)
    np.maximum(diff, 0, out=diff)
    flux = diff.sum(axis=1)

    # 去局部均值（窗口约 1 秒）后整流、按标准差归一化：抑制整体响度起伏，只留打击点。
    local = _moving_average(flux, max(1, int(round(sr / HOP))))
    o = np.maximum(flux - local, 0)

    std = float(o.std())

    if std > 1e-8:
        o = o / std

    return o.astype(np.float32), sr / HOP


def _moving_average(a, w):
    """居中滑动平均，边界用收缩窗口（与 C# 端保持一致）。"""
    n = len(a)
    out = np.empty(n, dtype=np.float64)
    cumsum = np.concatenate([[0.0], np.cumsum(a, dtype=np.float64)])

    for i in range(n):
        lo = max(0, i - w)
        hi = min(n, i + w + 1)
        out[i] = (cumsum[hi] - cumsum[lo]) / (hi - lo)

    return out


def autocorrelation(o, max_lag):
    """无偏自相关 r[lag]/r[0]，lag=0..max_lag（FFT 加速）。"""
    n = len(o)
    nfft = 1 << int(np.ceil(np.log2(2 * n)))

    spec = np.fft.rfft(o.astype(np.float64), nfft)
    ac = np.fft.irfft(spec * np.conj(spec), nfft)[:max_lag + 1]

    # 无偏：除以重叠样本数（标准做法，避免短 lag 天然占优）
    counts = n - np.arange(max_lag + 1)
    ac = ac / np.maximum(counts, 1)

    if ac[0] > 1e-12:
        ac = ac / ac[0]

    return ac


def tempo_salience(o, fps):
    """自相关 → BPM 网格上的谐波求和显著度曲线。

    对每个候选 BPM 的周期 lag 取 r[lag], r[2lag], r[3lag], r[4lag]，权重 1/h：
    这是经典的谐波求和，能同时压住「只抓住二倍频」的错判。
    """
    max_lag = int(round(60.0 * fps / BPM_MIN)) + 1
    ac = autocorrelation(o, max_lag)

    lags = 60.0 * fps / BPM_GRID
    salience = np.zeros(len(BPM_GRID), dtype=np.float64)

    for h in range(1, HARMONICS + 1):
        hl = lags * h
        valid = hl <= max_lag

        if not valid.any():
            continue

        # 线性插值到自相关序列上（BPM 网格是 0.5 步长，lag 不是整数）
        salience[valid] += _interp(ac, hl[valid]) / h

    return salience


def _interp(ac, positions):
    """在 ac 上按浮点位置线性插值（对齐 C# 端同样的插值口径）。"""
    lower = np.floor(positions).astype(int)
    frac = positions - lower
    upper = np.minimum(lower + 1, len(ac) - 1)
    return ac[lower] * (1 - frac) + ac[upper] * frac


def find_peaks(salience, min_distance_bins):
    """局部极大值（含平台取中点），按显著度降序返回下标。"""
    n = len(salience)
    peaks = []

    for i in range(1, n - 1):
        if salience[i] >= salience[i - 1] and salience[i] > salience[i + 1]:
            peaks.append(i)

    peaks.sort(key=lambda i: -salience[i])

    kept = []

    for i in peaks:
        if all(abs(i - j) >= min_distance_bins for j in kept):
            kept.append(i)

    return kept


def cache_path(cache_dir, name):
    return os.path.join(cache_dir, name + ".npz")


# ---- 候选评分模型的契约（训练与 C# 推理必须完全一致）----

MAX_CANDIDATES = 20
MIN_CANDIDATE_SEPARATION = 3.0   # BPM：比这更近的候选视为同一个
METRICAL_RATIOS = (0.25, 1 / 3, 0.5, 2 / 3, 1.5, 2.0, 3.0, 4.0)
CANDIDATE_PEAKS = 5              # 取显著度前几名的峰去派生 metrical 亲属
REFINE_SEARCH_BINS = 4           # 候选峰位抛物线细化时向两侧搜索的网格数
USE_REFINE = False               # 扫描结论：细化反而降召回，默认关闭

# 特征顺序（写进 config.json，C# 端按同一顺序拼装）
CANDIDATE_FEATURE_NAMES = (
    "salience_self",       # 候选自身显著度 / 全曲最大
    "salience_r025", "salience_r033", "salience_r05",
    "salience_r067", "salience_r15",
    "salience_r2", "salience_r3", "salience_r4",   # 各 metrical 相对层级的显著度 / 全曲最大
    "metrical_preference",  # self - mean(2x, 0.5x)，正值说明自己比倍频更被强调
    "log2_bpm_prior",       # log2(cand/120)，慢/快先验
    "peak_rank",            # 候选在按显著度排序的候选表中的名次（0 最好）
    "local_contrast",       # self / 邻域均值，峰越尖越大
    "prominence",           # self 在全曲最大中的占比（与 salience_self 同口径，便于模型做线性组合）
)
CANDIDATE_FEATURE_COUNT = len(CANDIDATE_FEATURE_NAMES)


def salience_at(salience, bpm):
    """在 BPM 网格上按线性插值取显著度（超出范围返回 0）。"""
    if bpm < BPM_MIN - 1e-9 or bpm > BPM_MAX + 1e-9:
        return 0.0

    pos = (bpm - BPM_MIN) / BPM_STEP
    return float(_interp(salience, np.array([pos]))[0])


def refine_bpm(salience, bpm):
    """把候选 BPM 移到附近显著度的真实极值上（抛物线细化）。

    BPM 网格是 0.5 步长、评估容差只有 ±2BPM：峰位差 2.5 BPM 就判错。
    自相关的峰在网格上是离散的，抛物线细化能把峰位还原到亚网格精度。
    """
    if bpm < BPM_MIN or bpm > BPM_MAX:
        return bpm

    if not USE_REFINE:
        # 关掉细化时吸附到最近的网格点，保持候选仍是网格值。
        return float(BPM_GRID[int(round((bpm - BPM_MIN) / BPM_STEP))])

    center = int(round((bpm - BPM_MIN) / BPM_STEP))
    lo = max(0, center - REFINE_SEARCH_BINS)
    hi = min(len(salience) - 1, center + REFINE_SEARCH_BINS)
    best = lo

    for i in range(lo, hi + 1):
        if salience[i] > salience[best]:
            best = i

    if best <= 0 or best >= len(salience) - 1:
        return float(BPM_GRID[best])

    y1, y2, y3 = float(salience[best - 1]), float(salience[best]), float(salience[best + 1])
    denom = y1 - 2 * y2 + y3

    if abs(denom) < 1e-12:
        return float(BPM_GRID[best])

    delta = 0.5 * (y1 - y3) / denom
    delta = max(-0.5, min(0.5, delta))   # 只允许落在峰所在网格内，防止跳走
    return float(BPM_GRID[best] + delta * BPM_STEP)


def candidate_bpms(salience):
    """候选 BPM：显著度前若干名峰的 metrical 亲属（含自身），抛物线细化后去重、按显著度降序截断。

    显式补上倍频/分频亲属是关键 —— 基线的错误几乎全是选错层级（48% 的曲目最强峰是
    标注节拍的二倍），若候选集里根本没有正确层级，后续模型再强也救不回来。
    """
    peaks = find_peaks(salience, min_distance_bins=int(round(4.0 / BPM_STEP)))
    pool = []

    if peaks:
        for i in peaks[:CANDIDATE_PEAKS]:
            best = float(BPM_GRID[i])

            for k in (1.0, 2.0, 0.5, 3.0, 1 / 3, 4.0, 0.25, 1.5, 2 / 3):
                pool.append(refine_bpm(salience, best * k))
    else:
        pool.append(float(BPM_GRID[int(np.argmax(salience))]))

    in_range = sorted(
        (b for b in pool if BPM_MIN <= b <= BPM_MAX),
        key=lambda b: -salience_at(salience, b),
    )

    kept = []

    for b in in_range:
        if all(abs(b - c) >= MIN_CANDIDATE_SEPARATION for c in kept):
            kept.append(b)

    return kept[:MAX_CANDIDATES]


def candidate_features(salience, candidates):
    """每个候选一行特征，形状 (len(candidates), CANDIDATE_FEATURE_COUNT)。"""
    s_max = float(salience.max())

    if s_max <= 1e-12:
        s_max = 1.0

    rows = []

    for rank, bpm in enumerate(candidates):
        self_s = salience_at(salience, bpm)
        ratios = [salience_at(salience, bpm * r) for r in METRICAL_RATIOS]
        half = salience_at(salience, bpm * 0.5)
        double = salience_at(salience, bpm * 2.0)

        # 邻域均值（±4%，即同一个峰附近的平均高度）
        lo = (bpm * 0.96 - BPM_MIN) / BPM_STEP
        hi = (bpm * 1.04 - BPM_MIN) / BPM_STEP
        i0, i1 = int(max(0, np.floor(lo))), int(min(len(salience) - 1, np.ceil(hi)))
        local_mean = float(salience[i0:i1 + 1].mean()) if i1 >= i0 else self_s

        rows.append([
            self_s / s_max,
            *[v / s_max for v in ratios],
            (self_s - 0.5 * (half + double)) / s_max,
            float(np.log2(bpm / 120.0)),
            float(rank),
            self_s / local_mean if local_mean > 1e-12 else 1.0,
            self_s / s_max,
        ])

    return np.asarray(rows, dtype=np.float64)

