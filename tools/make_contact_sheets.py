#!/usr/bin/env python3
"""Create labeled contact sheets from extracted JPEG frames."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("frames_dir", type=Path)
    parser.add_argument("--per-sheet", type=int, default=20)
    parser.add_argument("--columns", type=int, default=4)
    args = parser.parse_args()

    manifest = json.loads((args.frames_dir / "candidates.json").read_text(encoding="utf-8"))
    candidates = manifest["candidates"]
    font = ImageFont.load_default()
    thumb_w, thumb_h, label_h = 480, 270, 24
    rows = math.ceil(args.per_sheet / args.columns)

    for offset in range(0, len(candidates), args.per_sheet):
        group = candidates[offset : offset + args.per_sheet]
        sheet = Image.new("RGB", (args.columns * thumb_w, rows * (thumb_h + label_h)), "white")
        draw = ImageDraw.Draw(sheet)
        for local_index, item in enumerate(group):
            row, col = divmod(local_index, args.columns)
            x, y = col * thumb_w, row * (thumb_h + label_h)
            with Image.open(args.frames_dir / item["file"]) as source:
                image = source.convert("RGB")
                image.thumbnail((thumb_w, thumb_h))
                sheet.paste(image, (x, y))
            label = f"#{item['id']:03d}  {item['timestamp']}  motion={item['motion_score']:.5f}"
            draw.rectangle((x, y + thumb_h, x + thumb_w, y + thumb_h + label_h), fill="white")
            draw.text((x + 6, y + thumb_h + 5), label, fill="black", font=font)
        number = offset // args.per_sheet + 1
        sheet.save(args.frames_dir / f"contact_sheet_{number:02d}.jpg", quality=90)


if __name__ == "__main__":
    main()
