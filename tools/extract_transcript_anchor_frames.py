#!/usr/bin/env python3
"""Extract reviewable video frames at spoken visual-attention anchors."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from pathlib import Path

import imageio_ffmpeg


ANCHOR_PATTERN = re.compile(
    r"посмотр|смотрит|обратит.{0,20}вниман|на экран|на график|на картин|видите|видно",
    re.IGNORECASE,
)


def stamp(seconds: float) -> str:
    total = max(0, round(seconds))
    hours, rem = divmod(total, 3600)
    minutes, secs = divmod(rem, 60)
    return f"{hours:02d}:{minutes:02d}:{secs:02d}"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("video", type=Path)
    parser.add_argument("transcript_jsonl", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--offset", type=float, default=0.35)
    parser.add_argument("--min-spacing", type=float, default=2.0)
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    rows = [
        json.loads(line)
        for line in args.transcript_jsonl.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    matches = [row for row in rows if ANCHOR_PATTERN.search(row["text"])]

    selected: list[dict] = []
    for row in matches:
        seconds = min(float(row["end"]) + args.offset, float(row["start"]) + 2.5)
        if selected and seconds - selected[-1]["time_seconds"] < args.min_spacing:
            selected[-1]["text"] += " " + row["text"]
            continue
        selected.append(
            {
                "time_seconds": seconds,
                "timestamp": stamp(seconds),
                "transcript_start": row["start"],
                "transcript_end": row["end"],
                "text": row["text"],
            }
        )

    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    for number, item in enumerate(selected, start=1):
        name = f"anchor_{number:03d}_{item['timestamp'].replace(':', '-')}.jpg"
        subprocess.run(
            [
                ffmpeg,
                "-y",
                "-hide_banner",
                "-loglevel",
                "error",
                "-ss",
                f"{item['time_seconds']:.3f}",
                "-i",
                str(args.video),
                "-frames:v",
                "1",
                "-q:v",
                "2",
                str(args.output_dir / name),
            ],
            check=True,
        )
        item.update(
            {
                "id": number,
                "file": name,
                "motion_score": 0.0,
                "status": "spoken_visual_anchor_pending_semantic_review",
            }
        )

    manifest = {
        "source": str(args.video.resolve()),
        "transcript": str(args.transcript_jsonl.resolve()),
        "method": "frame immediately after a spoken visual-attention anchor",
        "anchor_pattern": ANCHOR_PATTERN.pattern,
        "candidate_count": len(selected),
        "candidates": selected,
    }
    (args.output_dir / "candidates.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )


if __name__ == "__main__":
    main()
