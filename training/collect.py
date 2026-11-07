#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""收集播放器里「用户手改过的 timing」→ 训练清单。

数据来源：播放器的持久化状态 `%APPDATA%/OsuPlayer/player-state.json`：
- `timings`：按歌曲 hash 保存的手改时间轴（timing 编辑器导出的对象）；
- `local` / `history` / `liked`：歌曲元数据（歌名、歌手、时长、album_audio_id、本地路径）。

真值口径：
- 全局 BPM = 第一条红线的 60000 / beatLength；
- 首拍 = 第一条红线的时间（毫秒）；
- 同一首歌存在多处不同 BPM（max/min > 1.04）时标记 `variable_tempo`，默认不参与全局 BPM 训练。

输出：`training/dataset/manifest.json`。重复运行会保留已有音频下载状态。
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os

import kugou

ROOT = os.path.dirname(os.path.abspath(__file__))
DATASET = os.path.join(ROOT, "dataset")
MANIFEST = os.path.join(DATASET, "manifest.json")


def load_song_index(state: dict) -> dict[str, dict]:
    """把 local / history / liked 合并成 hash → 元数据。"""
    songs: dict[str, dict] = {}

    for source in ("local", "history", "liked"):
        for song in state.get(source) or []:
            key = (song.get("hash") or "").lower()

            if key and key not in songs:
                songs[key] = {
                    "name": song.get("name") or "",
                    "artist": song.get("artist") or "",
                    "album": song.get("album") or "",
                    "duration_ms": song.get("duration") or 0,
                    "album_audio_id": song.get("audioId") or 0,
                    "local_path": song.get("localPath") or "",
                    "source_list": source,
                }

    return songs


def merge_timings(timings: dict) -> dict[str, list]:
    """state 里同一个 hash 可能同时存在大小写两份；取红线更多（信息更完整）的一份。"""
    merged: dict[str, list] = {}

    for key, lines in timings.items():
        lower = key.lower()

        if not isinstance(lines, list) or not lines:
            continue

        old = merged.get(lower)

        if old is None or red_count(lines) > red_count(old):
            merged[lower] = lines

    return merged


def red_count(lines: list) -> int:
    return sum(1 for line in lines if line.get("uninherited", True) and (line.get("beatLength") or 0) > 0)


def timing_summary(lines: list) -> dict:
    reds = sorted(
        (line for line in lines if line.get("uninherited", True) and (line.get("beatLength") or 0) > 0),
        key=lambda line: line.get("time") or 0,
    )

    if not reds:
        return {}

    bpms = [60000.0 / line["beatLength"] for line in reds]

    return {
        "bpm": round(bpms[0], 3),
        "first_beat_ms": round(reds[0].get("time") or 0, 3),
        "meter": reds[0].get("meter") or 4,
        "tempo_bpms": [round(bpm, 3) for bpm in bpms],
        "variable_tempo": max(bpms) / min(bpms) > 1.04,
        "timing_lines": lines,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--state", default=kugou.default_state_path())
    parser.add_argument("--out", default=MANIFEST)
    args = parser.parse_args()

    if not os.path.exists(args.state):
        print(f"找不到播放器状态文件：{args.state}")
        print("请先启动播放器并至少手动保存过一次 timing。")
        return 1

    state = kugou.load_state(args.state)
    songs = load_song_index(state)
    timings = merge_timings(state.get("timings") or {})

    previous = {}

    if os.path.exists(args.out):
        try:
            with open(args.out, "r", encoding="utf-8") as f:
                previous = {item["id"]: item for item in json.load(f).get("songs", [])}
        except (OSError, ValueError, KeyError):
            previous = {}

    entries = []
    skipped = []

    for song_id, lines in timings.items():
        summary = timing_summary(lines)

        if not summary:
            skipped.append((song_id, "没有红线（BPM 点）"))
            continue

        meta = songs.get(song_id) or {}
        local_path = meta.get("local_path") or ""
        old_audio = (previous.get(song_id) or {}).get("audio") or {}

        if local_path and os.path.exists(local_path):
            audio = {
                "status": "local",
                "path": local_path,
                "source": "local",
                "downloaded_at": None,
                "note": "",
            }
        elif old_audio.get("status") in ("downloaded", "failed", "risk_control", "unavailable"):
            audio = old_audio
            # 已下载但文件被 24h 清理脚本删除时，重新标为待下载。
            if audio.get("status") == "downloaded" and not os.path.exists(audio.get("path") or ""):
                audio = {"status": "pending", "path": None, "source": "kugou", "downloaded_at": None, "note": ""}
        else:
            audio = {"status": "pending", "path": None, "source": "kugou", "downloaded_at": None, "note": ""}

        entries.append(
            {
                "id": song_id,
                "hash": song_id.upper(),
                "name": meta.get("name") or "",
                "artist": meta.get("artist") or "",
                "album": meta.get("album") or "",
                "duration_ms": meta.get("duration_ms") or 0,
                "album_audio_id": meta.get("album_audio_id") or 0,
                **summary,
                "audio": audio,
            }
        )

    entries.sort(key=lambda item: item["id"])
    os.makedirs(os.path.dirname(args.out), exist_ok=True)

    manifest = {
        "generated_at": dt.datetime.now().isoformat(timespec="seconds"),
        "state_path": os.path.abspath(args.state),
        "count": len(entries),
        "songs": entries,
    }

    temp = args.out + ".tmp"

    with open(temp, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

    os.replace(temp, args.out)

    trainable = [item for item in entries if not item["variable_tempo"]]
    local_ready = sum(1 for item in entries if item["audio"]["status"] == "local")
    downloaded = sum(1 for item in entries if item["audio"]["status"] == "downloaded")

    print(f"清单：{args.out}")
    print(f"手改 timing：{len(entries)} 首（可训练 {len(trainable)} 首，多 BPM 跳过 {len(entries) - len(trainable)} 首）")
    print(f"音频：本地 {local_ready} 首，已下载 {downloaded} 首，待下载 {sum(1 for i in entries if i['audio']['status'] == 'pending')} 首")
    print(f"元数据缺失（只能按 hash 下载）：{sum(1 for item in entries if not item['name'])} 首")

    if skipped:
        print(f"跳过无红线条目：{len(skipped)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
