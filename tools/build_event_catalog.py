#!/usr/bin/env python3
"""Build a traceable research catalogue; never edits production code or trains a model.

Inputs are authored specification + existing transcripts/frame manifests.
Outputs are documentation, evidence navigation and structural validation only.
"""
from __future__ import annotations

import hashlib
import json
import re
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "docs/event-catalog-v1"
REPRO = ROOT / "artifacts/breeze_ai_reprocessed_20261012"
FAMILIES = {
    "T": "Тренд и контекст снижения",
    "E": "Цена и пересечения EMA",
    "R": "MyRSI: состояния и переходы",
    "B": "Широта и её последовательности",
    "P": "Свечи, волатильность и гэпы",
    "V": "VIX и форма срочной кривой",
    "Z": "Цвейг",
    "I": "Titanic",
    "C": "Контекстные комплексы",
    "X": "Исполнение — вне рыночного скоринга",
    "U": "Дополнительные конструкции из текущего чата",
}
KINDS = {
    "state": "состояние", "event": "событие", "sequence": "последовательность",
    "composite": "комплекс", "execution": "исполнение",
}
# Only these images were opened separately during this catalogue task.
# A cursor date is NOT automatically the date of the event being narrated.
VISUAL_REVIEWS = {
    "artifacts/breeze_ai_reprocessed_20261012/hold_frames/hold_099_00-41-30.jpg": {
        "cursor_date": "2019-12-04", "events": ["B07"],
        "note": "Открыт полный кадр Nasdaq SMA100 и SPY; виден широкий участок до кризиса 2020. Дата курсора не подтверждена как дата дивергенции; событие требует разметки по данным.",
    },
    "artifacts/Contango_1/anchor_frames/anchor_073_01-03-01.jpg": {
        "cursor_date": "2025-02-21", "events": ["C08", "R08"],
        "note": "На курсоре красная свеча ниже EMA20; MyRSI 14/8=0.688, 10/8=0.639; ZW Density=30, Titanic ACTIVE d2. Относительная сила тела требует числового сравнения с прошлым.",
    },
    "artifacts/Podhvat_1/anchor_frames/anchor_008_00-08-08.jpg": {
        "cursor_date": "2024-09-11", "events": ["C06", "P03"],
        "note": "На курсоре возвратный зелёный бар в эпизоде подхвата; EMA20/50/100/200=551.1/547.2/537.5/515.9, ATRP1=2.78%. Проверка всей последовательности по числовым данным ещё не выполнена.",
    },
}
CONTACT_SHEETS_REVIEWED = [
    "artifacts/Breeze_AI_2/frames/contact_sheet_02.jpg",
    "artifacts/Contango_1/hold_frames/contact_sheet_04.jpg",
    "artifacts/Contango_1/anchor_frames/contact_sheet_04.jpg",
    "artifacts/Podhvat_1/hold_frames/contact_sheet_01.jpg",
    "artifacts/Podhvat_1/anchor_frames/contact_sheet_01.jpg",
    "artifacts/Podhvat_1/anchor_frames/contact_sheet_02.jpg",
    "artifacts/breeze_ai_reprocessed_20261012/hold_frames/contact_sheet_04.jpg",
    "artifacts/breeze_ai_reprocessed_20261012/hold_frames/contact_sheet_05.jpg",
    "artifacts/breeze_ai_reprocessed_20261012/hold_frames/contact_sheet_06.jpg",
]


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save_json(path: Path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def seconds(stamp: str) -> int:
    parts = stamp.split(":")
    if len(parts) != 3:
        raise ValueError(stamp)
    h, m, s = map(int, parts)
    assert h >= 0 and 0 <= m < 60 and 0 <= s < 60
    return h * 3600 + m * 60 + s


def digest(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def link(path: str | Path, label: str, line: int | None = None) -> str:
    full = (ROOT / path).resolve().as_posix()
    if line:
        full += f":{line}"
    return f"[{label}](<{full}>)"


def main():
    spec = read_json(OUT / "catalog_source.json")
    records = []
    ids = set()
    for row in spec["events"]:
        assert len(row) == len(spec["columns"]), row[0]
        item = dict(zip(spec["columns"], row))
        assert re.fullmatch(r"[TERBPVZICXU]\d{2}", item["id"]), item["id"]
        assert item["id"] not in ids, item["id"]
        assert item["kind"] in KINDS and item["source"] in spec["sources"]
        ids.add(item["id"])
        item.update({
            "family": FAMILIES[item["id"][0]],
            "origin": "user_chat_example" if item["source"] == "U" else "video_narrative_with_proposed_formalization",
            "weight": None,
            "detector_implemented": False,
            "numerical_event_verified": False,
            "definition_status": "proposal_with_explicit_unresolved_parameters",
            "role": "execution_only" if item["kind"] == "execution" else "candidate_market_feature",
        })
        records.append(item)
    records.sort(key=lambda r: (list(FAMILIES).index(r["id"][0]), r["id"]))

    frames = []
    sources = {}
    transcripts = {}
    for source_id, source_spec in spec["sources"].items():
        src = dict(source_spec)
        if source_id != "U":
            video = ROOT / src["video"]
            transcript = ROOT / src["transcript"]
            assert video.is_file() and transcript.is_file(), source_id
            segments = [json.loads(line) for line in transcript.with_suffix(".jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
            assert segments and all(x["start"] <= x["end"] for x in segments)
            assert all(x["start"] <= y["start"] for x, y in zip(segments, segments[1:]))
            assert "\ufffd" not in transcript.read_text(encoding="utf-8"), "Invalid transcript encoding"
            meta = read_json(transcript.parent / "metadata.json")
            src.update({
                "video_sha256": digest(video), "transcript_sha256": digest(transcript),
                "transcript_segments": len(segments), "duration_seconds": meta["duration_seconds"],
                "transcript_read_in_this_task": True,
            })
            transcripts[source_id] = segments
            for directory in src["frame_dirs"]:
                manifest = read_json(ROOT / directory / "candidates.json")
                assert manifest["candidate_count"] == len(manifest["candidates"])
                for frame in manifest["candidates"]:
                    relative = f"{directory}/{frame['file']}"
                    assert (ROOT / relative).is_file(), relative
                    frames.append({
                        "source": source_id, "path": relative,
                        "timestamp": frame["timestamp"], "time_seconds": frame["time_seconds"],
                        "method": manifest["method"], "sha256": digest(ROOT / relative),
                        "visual_review": VISUAL_REVIEWS.get(relative),
                        "matching_event_ranges": [],
                    })
            src["frame_candidates"] = sum(f["source"] == source_id for f in frames)
        sources[source_id] = src

    # Duplicate bytes, copied selections and contact sheets do not create new observations.
    byte_groups = {}
    for frame in frames:
        byte_groups.setdefault(frame["sha256"], []).append(frame["path"])
    for frame in frames:
        frame["same_bytes_other_paths"] = [p for p in byte_groups[frame["sha256"]] if p != frame["path"]]

    for item in records:
        source_id = item["source"]
        if source_id == "U":
            assert item["start"] is None and item["end"] is None
            item["navigation_frames"] = []
            item["source_segment_ids"] = []
            continue
        start, end = seconds(item["start"]), seconds(item["end"])
        assert 0 <= start < end <= sources[source_id]["duration_seconds"] + 1, item["id"]
        matched_segments = [s for s in transcripts[source_id] if s["end"] >= start and s["start"] <= end]
        assert matched_segments, item["id"]
        item["source_segment_ids"] = [s["id"] for s in matched_segments]
        item["transcript_line_start"] = next(i for i, s in enumerate(transcripts[source_id], 1) if s["id"] == matched_segments[0]["id"])
        candidates = [f for f in frames if f["source"] == source_id and start <= f["time_seconds"] <= end]
        for frame in candidates:
            frame["matching_event_ranges"].append(item["id"])
        # Full-frame inspected examples first, then temporal navigation only.
        preferred = sorted(candidates, key=lambda f: (
            not (f["visual_review"] and item["id"] in f["visual_review"]["events"]),
            abs(f["time_seconds"] - (start + end) / 2),
        ))
        item["navigation_frames"] = [
            {"path": f["path"], "timestamp": f["timestamp"], "status": "full_frame_inspected_not_detector_validated" if f["visual_review"] else "temporal_navigation_not_semantically_verified"}
            for f in preferred[:3]
        ]
        item["range_frame_count"] = len(candidates)

    for rel in [*VISUAL_REVIEWS, *CONTACT_SHEETS_REVIEWED]:
        assert (ROOT / rel).is_file(), rel
    for item in records:
        assert item["weight"] is None and not item["detector_implemented"]

    catalog = {
        "version": spec["version"], "date": spec["date"], "status": spec["status"],
        "target": spec["target"], "sources": sources,
        "information_contract": "See README.md; no future data or actual holdings in market feature definitions.",
        "scope": "Catalogue of event TYPES; no daily detector run, optimized weights or claimed predictive performance.",
        "events": records,
    }
    save_json(OUT / "catalog.json", catalog)
    save_json(OUT / "evidence_index.json", {
        "association_method": "same source + candidate timestamp within narrative range; NOT semantic validation",
        "full_frame_reviews": VISUAL_REVIEWS,
        "contact_sheets_reviewed": CONTACT_SHEETS_REVIEWED,
        "frames": frames,
    })

    doc = [
        "# Каталог событий Breeze — карточки", "",
        f"Версия {spec['version']}, {spec['date']}. Всего {len(records)} типов конструкций; не число сделок или независимых наблюдений.", "",
        link(OUT / "README.md", "Постановка, обозначения, правила причинности и ограничения"), "",
        "Цель модели: целевая доля позиции 0–100% по доступным данным завершившегося дня. Горизонт прогноза не задан. Фактическая позиция не входит в определение рыночного события.", "",
        "Все определения ниже — спецификация для будущих детекторов. Веса не подобраны. Указаны авторский смысл и отдельно предлагаемая формализация. Таймкод относится к записи, а не к торговой дате.", "",
        "Кадры подобраны внутри временного отрезка как навигационные опоры: они не доказывают выполнение всей конструкции. Проверка каждого условия по числовой истории ещё впереди.", "",
        "## Состав", "", "| Семейство | Карточек |", "|---|---:|",
    ]
    for prefix, family in FAMILIES.items():
        doc.append(f"| {family} | {sum(r['id'].startswith(prefix) for r in records)} |")
    doc += ["", "## Источники", ""]
    for sid, src in sources.items():
        if sid == "U":
            doc += [f"- **U** — {src['title']}. Примеры отдельно помечены и не приписаны видеороликам."]
        else:
            doc += [f"- **{sid}** — {src['title']}: {link(src['video'], 'видео')}; {link(src['transcript'], 'расшифровка')}. Кадров-кандидатов: {src['frame_candidates']}."]
    doc += ["", "## Указатель", ""]
    for item in records:
        doc.append(f"- [{item['id']} — {item['name']}](#{item['id'].lower()})")
    current_family = None
    for item in records:
        if item["family"] != current_family:
            current_family = item["family"]
            doc += ["", f"## {current_family}", ""]
        doc += [f"<a id=\"{item['id'].lower()}\"></a>", "", f"### {item['id']} — {item['name']}", "", f"Тип: {KINDS[item['kind']]}. Вес: не задан. Числовой детектор: ещё не реализован.", ""]
        if item["source"] == "U":
            doc.append("Источник: пример пользователя в текущем обсуждении архитектуры.")
        else:
            src = sources[item["source"]]
            doc.append(f"Источник: {src['title']}, {item['start']}–{item['end']}; {link(src['transcript'], 'фрагмент расшифровки', item['transcript_line_start'])}.")
        for field, label in [("source_meaning", "Смысл источника"), ("proposed_definition", "Предлагаемое определение"), ("lifecycle", "Появление, действие и отмена"), ("unresolved", "Что уточнить / не подменять")]:
            doc += ["", f"**{label}:** {item[field]}"]
        if item["navigation_frames"]:
            doc += ["", "Навигационные кадры: " + "; ".join(link(f["path"], f["timestamp"]) for f in item["navigation_frames"]) + "."]
            for frame in item["navigation_frames"]:
                review = VISUAL_REVIEWS.get(frame["path"])
                if review:
                    doc += ["", f"Отдельно просмотренный кадр: {review['note']}"]
    doc += ["", "## Проверка покрытия", "", f"В реестре {len(frames)} кадров-кандидатов из четырёх роликов; {len(byte_groups)} уникальных SHA-256. Совпадающие изображения и близкие моменты записи не равны независимым рыночным эпизодам.", "",
            "Три исходных пакета содержат 428 кадров по манифестам. Ранее сообщавшиеся 439 файлов включали ещё 11 копий выбранных кадров Breeze_AI_2/frames/selected. Контактные листы не являются новыми примерами.", "",
            "В этой работе полностью прочитаны четыре расшифровки, просмотрены перечисленные в evidence_index.json обзорные листы и три отдельно открытых кадра. Не заявляется сплошная визуальная проверка каждого кадра или проверка всех событий по биржевым датам.", ""]
    (OUT / "CATALOG.md").write_text("\n".join(doc), encoding="utf-8")

    episodes = read_json(REPRO / "episodes/episodes.json")
    previous_end = 0
    episode_doc = ["# Первый ролик — повторная смысловая разбивка", "", episodes["basis"], "", "| ID | Время | Раздел | События |", "|---|---|---|---|"]
    for episode in episodes["episodes"]:
        start, end = seconds(episode["start"]), seconds(episode["end"])
        assert start == previous_end and end > start
        previous_end = end
        assert set(episode["event_ids"]) <= ids
        episode_frames = [f for f in frames if f["source"] == "B1" and start <= f["time_seconds"] < end]
        episode["frame_paths"] = [f["path"] for f in episode_frames]
        episode["transcript_segment_ids"] = [s["id"] for s in transcripts["B1"] if s["end"] >= start and s["start"] < end]
        episode_doc.append(f"| {episode['id']} | {episode['start']}–{episode['end']} | {episode['title']} | {', '.join(episode['event_ids']) or 'Контекст / качество данных'} |")
    assert abs(previous_end - sources["B1"]["duration_seconds"]) < 1
    for episode in episodes["episodes"]:
        episode_doc += ["", f"## {episode['id']} — {episode['title']}", "", episode["note"], "", f"Сегментов расшифровки: {len(episode['transcript_segment_ids'])}; кадров-кандидатов: {len(episode['frame_paths'])}."]
        if episode["frame_paths"]:
            chosen = episode["frame_paths"][len(episode["frame_paths"]) // 2]
            episode_doc += ["", link(chosen, "Опорный кадр внутри отрезка — навигация")]
    save_json(REPRO / "episodes/episodes_index.json", episodes)
    (REPRO / "episodes/episodes.md").write_text("\n".join(episode_doc) + "\n", encoding="utf-8")
    b1_metadata = read_json(REPRO / "transcript/metadata.json")
    packet = {
        "review_date": spec["date"], "source": sources["B1"]["video"],
        "source_sha256": sources["B1"]["video_sha256"],
        "transcription": b1_metadata, "episodes": len(episodes["episodes"]),
        "frame_candidates": sources["B1"]["frame_candidates"],
        "files": [{"path": p.relative_to(REPRO).as_posix(), "bytes": p.stat().st_size, "sha256": digest(p)} for p in sorted(REPRO.rglob("*")) if p.is_file() and p.name not in {"manifest.json", "README.md"}],
    }
    save_json(REPRO / "manifest.json", packet)
    validation = {
        "date": spec["date"], "structural_checks_passed": True,
        "event_count": len(records), "by_kind": dict(Counter(r["kind"] for r in records)),
        "by_source": dict(Counter(r["source"] for r in records)),
        "frame_candidates": len(frames), "unique_frame_hashes": len(byte_groups),
        "candidate_counts_by_source": {sid: s.get("frame_candidates", 0) for sid, s in sources.items()},
        "full_frames_opened": len(VISUAL_REVIEWS), "contact_sheets_reviewed": len(CONTACT_SHEETS_REVIEWED),
        "events_without_frame_in_narrative_range": [r["id"] for r in records if r["source"] != "U" and not r["navigation_frames"]],
        "checks": ["unique event IDs", "record schema", "all video/transcript/frame references exist", "time ranges within source duration", "matching transcript segments", "UTF-8 transcripts have no replacement characters", "all learned weights null", "first video episode coverage contiguous to end"],
        "not_claimed": ["each frame semantically reviewed", "all trading dates aligned", "detector implementation", "numerical event validation", "model training", "backtest", "production DLL changes"],
        "source_spec_sha256": digest(OUT / "catalog_source.json"),
    }
    save_json(OUT / "validation.json", validation)
    print(json.dumps(validation, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
