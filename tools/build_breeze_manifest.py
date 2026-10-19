#!/usr/bin/env python3
"""Write a SHA-256 manifest and compact validation summary for Breeze_AI."""

from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("original", type=Path)
    args = parser.parse_args()

    manifest_path = args.root / "validation" / "manifest.json"
    files = []
    for path in sorted(p for p in args.root.rglob("*") if p.is_file() and p != manifest_path):
        files.append(
            {
                "path": path.relative_to(args.root).as_posix(),
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
        )
    original_hash = sha256(args.original)
    copied_video = args.root / "source" / args.original.name
    copied_hash = sha256(copied_video)
    payload = {
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "root": str(args.root.resolve()),
        "source_original": str(args.original.resolve()),
        "source_original_sha256": original_hash,
        "copied_video_sha256": copied_hash,
        "source_copy_matches_original": original_hash == copied_hash,
        "model_trained": False,
        "methodology_status": "pending_author_review",
        "file_count_excluding_manifest": len(files),
        "total_bytes_excluding_manifest": sum(item["bytes"] for item in files),
        "files": files,
    }
    manifest_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
