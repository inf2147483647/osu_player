#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""删除训练音频（版权数据只保留 24 小时）。

默认只删除 `downloaded_at` 超过 24 小时的下载音频（本地导入的曲目永远不删）。
删完后把清单里的状态改回 `pending`，下次 download.py 会重新拉取。

用法：
  py training/cleanup.py            # 删除超过 24 小时的音频
  py training/cleanup.py --all      # 立即删除全部下载音频
  py training/cleanup.py --dry-run  # 只列出将删除的文件
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
MANIFEST = os.path.join(ROOT, "dataset", "manifest.json")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", default=MANIFEST)
    parser.add_argument("--hours", type=float, default=24.0)
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    if not os.path.exists(args.manifest):
        print(f"清单不存在：{args.manifest}")
        return 1

    with open(args.manifest, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    now = dt.datetime.now()
    removed = 0
    freed = 0

    for entry in manifest["songs"]:
        audio = entry.get("audio") or {}
        path = audio.get("path")

        if audio.get("status") != "downloaded" or not path:
            continue

        age_hours = args.hours + 1

        if not args.all and audio.get("downloaded_at"):
            age_hours = (now - dt.datetime.fromisoformat(audio["downloaded_at"])).total_seconds() / 3600

        if not args.all and age_hours < args.hours:
            continue

        if os.path.exists(path):
            size = os.path.getsize(path)
            print(f"{'[dry-run] ' if args.dry_run else ''}删除 {path}（{size / 1024 / 1024:.1f}MB，{age_hours:.1f}h）")
            freed += size

            if not args.dry_run:
                os.remove(path)
        elif not args.dry_run:
            print(f"文件已不存在：{path}")

        if not args.dry_run:
            audio.update(status="pending", path=None, downloaded_at=None, note="已按 24h 规则清理")

        removed += 1

    if not args.dry_run and removed:
        temp = args.manifest + ".tmp"

        with open(temp, "w", encoding="utf-8") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=2)

        os.replace(temp, args.manifest)

    print(f"\n{'将' if args.dry_run else '已'}清理 {removed} 首，释放 {freed / 1024 / 1024:.1f}MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
