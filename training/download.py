#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""顺序下载训练音频（不并行）。

为什么要慢：酷狗对短时间密集请求会触发人机验证（errcode 20028）。
本脚本一次只处理一首，歌曲之间默认等待 2.5–4.5 秒；一旦识别到安全验证，
立即停止整个队列并写出 `dataset/RISK_CONTROL.txt`，等待人工在播放器里完成验证。

下载位置：`training/dataset/audio/<歌曲hash>.<ext>`
版权数据保留不超过 24 小时：本脚本只记录 `downloaded_at`，由 `cleanup.py` 负责清理。
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import time

import kugou

ROOT = os.path.dirname(os.path.abspath(__file__))
DATASET = os.path.join(ROOT, "dataset")
MANIFEST = os.path.join(DATASET, "manifest.json")
AUDIO_DIR = os.path.join(DATASET, "audio")
RISK_FILE = os.path.join(DATASET, "RISK_CONTROL.txt")


def save_manifest(manifest: dict) -> None:
    temp = MANIFEST + ".tmp"

    with open(temp, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

    os.replace(temp, MANIFEST)


def risk_control_notice(entry: dict) -> None:
    os.makedirs(DATASET, exist_ok=True)

    with open(RISK_FILE, "w", encoding="utf-8") as f:
        f.write(
            "酷狗触发了安全验证（20028），下载已停止。\n"
            f"时间：{dt.datetime.now().isoformat(timespec='seconds')}\n"
            f"最后请求的歌曲：{entry.get('name') or entry['id']} / {entry['hash']}\n\n"
            "请先在播放器或酷狗 App 里完成人机验证，然后重新运行：\n"
            "  py training/download.py\n"
        )

    print()
    print("!! 触发酷狗安全验证（20028），已停止下载。")
    print("!! 请先在播放器/酷狗 App 内完成人机验证，然后重新运行本脚本继续。")
    print(f"!! 标记文件：{RISK_FILE}")


def download_one(entry: dict, session: dict, quality: str) -> tuple[bool, str]:
    payload = kugou.resolve_song_url(
        session,
        entry["hash"],
        album_audio_id=entry.get("album_audio_id") or 0,
        quality=quality,
    )

    url, ext, reason = kugou.pick_url(payload)
    note = ""

    if url is None:
        # 会员/付费曲目：退回到 60 秒试听片段；BPM 检测不依赖整首长度。
        payload = kugou.resolve_song_url(
            session,
            entry["hash"],
            album_audio_id=entry.get("album_audio_id") or 0,
            quality=quality,
            free_part=True,
        )
        url, ext, reason = kugou.pick_url(payload)

        if url is None:
            entry["audio"].update(status="unavailable", note=reason)
            return False, reason

        note = "60 秒试听片段"

    target = os.path.join(AUDIO_DIR, f"{entry['id']}.{ext}")
    kugou.download_file(url, target)

    entry["audio"] = {
        "status": "downloaded",
        "path": target,
        "source": "kugou",
        "downloaded_at": dt.datetime.now().isoformat(timespec="seconds"),
        "note": note,
    }

    return True, f"{os.path.getsize(target) / 1024 / 1024:.1f}MB {note}".strip()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", default=MANIFEST)
    parser.add_argument("--state", default=kugou.default_state_path())
    parser.add_argument("--quality", default="128", help="128 / 320 / flac")
    parser.add_argument("--limit", type=int, default=0, help="本次最多下载几首（0 = 全部）")
    parser.add_argument("--delay-min", type=float, default=2.5)
    parser.add_argument("--delay-max", type=float, default=4.5)
    parser.add_argument("--retry-failed", action="store_true")
    args = parser.parse_args()

    if not os.path.exists(args.manifest):
        print(f"清单不存在：{args.manifest}\n请先运行 py training/collect.py")
        return 1

    with open(args.manifest, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    state = kugou.load_state(args.state)
    session = {
        "token": (state.get("session") or {}).get("token") or "",
        "userid": (state.get("session") or {}).get("userid") or 0,
        "device": state.get("device") or {},
    }

    if not session["token"]:
        print("播放器状态里没有登录凭证，无法解析播放地址。请先在播放器登录。")
        return 1

    wanted = {"pending"}

    if args.retry_failed:
        wanted |= {"failed", "unavailable", "risk_control"}

    queue = [item for item in manifest["songs"] if item["audio"]["status"] in wanted]

    if args.limit > 0:
        queue = queue[: args.limit]

    print(f"待下载 {len(queue)} 首（顺序执行，间隔 {args.delay_min:g}–{args.delay_max:g}s）")

    done = 0
    failed = 0

    for index, entry in enumerate(queue, 1):
        label = entry["name"] or entry["id"]
        print(f"[{index}/{len(queue)}] {label} ({entry['hash']}) ...", end=" ", flush=True)

        success = False
        last_error = ""

        for attempt in range(1, 4):
            try:
                success, detail = download_one(entry, session, args.quality)
                print(("OK " if success else "跳过 ") + detail)

                if not success:
                    last_error = detail

                break
            except kugou.RiskControlError:
                entry["audio"].update(status="risk_control", note="等待人工完成安全验证")
                save_manifest(manifest)
                risk_control_notice(entry)
                return 42
            except kugou.KuGouError as e:
                last_error = str(e)
                print(f"失败（{attempt}/3）：{e}")

                if attempt < 3:
                    time.sleep(6 + attempt * 4)
            except OSError as e:
                last_error = str(e)
                print(f"写文件失败：{e}")
                break

        if not success:
            entry["audio"].update(status="failed", note=last_error[:500])
            failed += 1
        else:
            done += 1

        save_manifest(manifest)

        if index < len(queue):
            kugou.polite_delay(args.delay_min, args.delay_max)

    remaining = sum(1 for item in manifest["songs"] if item["audio"]["status"] == "pending")
    print()
    print(f"完成：成功 {done}，失败 {failed}，剩余待下载 {remaining}")
    print(f"音频目录：{AUDIO_DIR}")

    if os.path.exists(RISK_FILE):
        os.remove(RISK_FILE)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
