#!/usr/bin/env python3
"""Transcribe a local video/audio file with timestamps using faster-whisper."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path


def stamp(seconds: float, srt: bool = False) -> str:
    millis = max(0, round(seconds * 1000))
    hours, millis = divmod(millis, 3_600_000)
    minutes, millis = divmod(millis, 60_000)
    secs, millis = divmod(millis, 1000)
    separator = "," if srt else "."
    return f"{hours:02d}:{minutes:02d}:{secs:02d}{separator}{millis:03d}"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--model", default="small")
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--compute-type", default="int8")
    parser.add_argument("--language", default="ru")
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

    from faster_whisper import WhisperModel

    model = WhisperModel(
        args.model,
        device=args.device,
        compute_type=args.compute_type,
    )
    segments_iter, info = model.transcribe(
        str(args.input),
        language=args.language,
        beam_size=5,
        vad_filter=True,
        word_timestamps=True,
        condition_on_previous_text=True,
    )

    segments = []
    for index, segment in enumerate(segments_iter, start=1):
        words = [
            {
                "start": word.start,
                "end": word.end,
                "word": word.word,
                "probability": word.probability,
            }
            for word in (segment.words or [])
        ]
        row = {
            "id": index,
            "start": segment.start,
            "end": segment.end,
            "text": segment.text.strip(),
            "avg_logprob": segment.avg_logprob,
            "no_speech_prob": segment.no_speech_prob,
            "words": words,
        }
        segments.append(row)
        print(f"[{stamp(segment.start)} --> {stamp(segment.end)}] {row['text']}", flush=True)

    metadata = {
        "source": str(args.input.resolve()),
        "model": args.model,
        "device": args.device,
        "compute_type": args.compute_type,
        "requested_language": args.language,
        "detected_language": info.language,
        "language_probability": info.language_probability,
        "duration_seconds": info.duration,
        "duration_after_vad_seconds": info.duration_after_vad,
        "segment_count": len(segments),
    }

    (args.output_dir / "metadata.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    with (args.output_dir / "transcript.jsonl").open("w", encoding="utf-8") as handle:
        for segment in segments:
            handle.write(json.dumps(segment, ensure_ascii=False) + "\n")

    with (args.output_dir / "transcript.txt").open("w", encoding="utf-8") as handle:
        for segment in segments:
            handle.write(
                f"[{stamp(segment['start'])} --> {stamp(segment['end'])}] "
                f"{segment['text']}\n"
            )

    with (args.output_dir / "transcript.srt").open("w", encoding="utf-8") as handle:
        for segment in segments:
            handle.write(
                f"{segment['id']}\n"
                f"{stamp(segment['start'], srt=True)} --> {stamp(segment['end'], srt=True)}\n"
                f"{segment['text']}\n\n"
            )


if __name__ == "__main__":
    main()
