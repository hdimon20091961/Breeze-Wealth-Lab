#!/usr/bin/env python3
"""Find low-motion screen holds and export representative video frames.

The heuristic is intended for narrated chart recordings: a stationary cursor or
crosshair produces a sustained local minimum in frame-to-frame change.
Candidates remain reviewable through the accompanying CSV/JSON metrics.
"""

from __future__ import annotations

import argparse
import csv
import json
import subprocess
from collections import deque
from pathlib import Path

import imageio_ffmpeg
import numpy as np


def timestamp(seconds: float) -> str:
    total = max(0, round(seconds))
    hours, rem = divmod(total, 3600)
    minutes, secs = divmod(rem, 60)
    return f"{hours:02d}:{minutes:02d}:{secs:02d}"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("video", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--fps", type=float, default=2.0)
    parser.add_argument("--width", type=int, default=480)
    parser.add_argument("--height", type=int, default=270)
    parser.add_argument("--max-frames", type=int, default=120)
    parser.add_argument("--min-spacing", type=float, default=12.0)
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    frame_size = args.width * args.height
    command = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-i",
        str(args.video),
        "-vf",
        f"fps={args.fps},scale={args.width}:{args.height},format=gray",
        "-f",
        "rawvideo",
        "-pix_fmt",
        "gray",
        "pipe:1",
    ]

    process = subprocess.Popen(command, stdout=subprocess.PIPE)
    assert process.stdout is not None
    previous = None
    metrics: list[dict[str, float]] = []
    index = 0
    smooth_window: deque[float] = deque(maxlen=max(2, round(args.fps * 2)))
    while True:
        data = process.stdout.read(frame_size)
        if len(data) != frame_size:
            break
        frame = np.frombuffer(data, dtype=np.uint8).reshape(args.height, args.width)
        std = float(frame.std())
        if previous is None:
            motion = 255.0
        else:
            motion = float(np.abs(frame.astype(np.int16) - previous.astype(np.int16)).mean())
        smooth_window.append(motion)
        smooth = float(np.median(smooth_window))
        metrics.append(
            {
                "time_seconds": index / args.fps,
                "motion": motion,
                "motion_median_2s": smooth,
                "luma_std": std,
            }
        )
        previous = frame.copy()
        index += 1
    return_code = process.wait()
    if return_code:
        raise SystemExit(return_code)

    valid_scores = [m["motion_median_2s"] for m in metrics if m["luma_std"] >= 12 and m["time_seconds"] >= 3]
    threshold = float(np.percentile(valid_scores, 25)) if valid_scores else 0.0
    pool = [
        m for m in metrics
        if m["luma_std"] >= 12
        and m["time_seconds"] >= 3
        and m["motion_median_2s"] <= threshold
    ]
    pool.sort(key=lambda m: (m["motion_median_2s"], m["time_seconds"]))
    selected: list[dict[str, float]] = []
    for item in pool:
        if all(abs(item["time_seconds"] - old["time_seconds"]) >= args.min_spacing for old in selected):
            selected.append(item)
            if len(selected) >= args.max_frames:
                break
    selected.sort(key=lambda m: m["time_seconds"])

    with (args.output_dir / "motion_metrics.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(metrics[0]))
        writer.writeheader()
        writer.writerows(metrics)

    manifest = {
        "source": str(args.video.resolve()),
        "method": "2 fps grayscale mean absolute difference; 2-second rolling median; lowest quartile; 12-second non-maximum suppression",
        "fps": args.fps,
        "analysis_size": [args.width, args.height],
        "low_motion_threshold": threshold,
        "candidate_count": len(selected),
        "candidates": [],
    }
    for number, item in enumerate(selected, start=1):
        seconds = item["time_seconds"]
        name = f"hold_{number:03d}_{timestamp(seconds).replace(':', '-')}.jpg"
        output = args.output_dir / name
        subprocess.run(
            [
                ffmpeg,
                "-y",
                "-hide_banner",
                "-loglevel",
                "error",
                "-ss",
                f"{seconds:.3f}",
                "-i",
                str(args.video),
                "-frames:v",
                "1",
                "-q:v",
                "2",
                str(output),
            ],
            check=True,
        )
        manifest["candidates"].append(
            {
                "id": number,
                "timestamp": timestamp(seconds),
                "time_seconds": seconds,
                "motion_score": item["motion_median_2s"],
                "file": name,
                "status": "heuristic_candidate_pending_semantic_review",
            }
        )

    (args.output_dir / "candidates.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )


if __name__ == "__main__":
    main()
