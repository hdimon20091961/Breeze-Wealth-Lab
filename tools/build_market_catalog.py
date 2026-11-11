#!/usr/bin/env python3
"""Build the full UI catalogue without changing the frozen runtime scoring rules."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    pointer = read(root / "docs/event-catalog-current.json")
    source = root / "docs" / pointer["version"] / "catalog.json"
    model_pointer = read(root / "research/models/current_model.json")
    model_dir = root / "research/models" / model_pointer["id"]
    membership = read(model_dir / "model_members.json")
    members = {item["id"]: item for item in membership["members"]}
    raw = read(source)
    runtime_path = root / "Breeze/BreezeRules.json"
    rule_document = read(runtime_path)
    runtime = rule_document["rules"] + rule_document.get("additional_states", [])
    runtime_by_id = {entry["id"]: entry for entry in runtime}
    raw_by_id = {entry["id"]: entry for entry in raw["events"]}
    assert len(raw_by_id) == len(raw["events"]) == pointer["count"], "Catalogue IDs/count mismatch"
    assert set(runtime_by_id) <= raw_by_id.keys(), "Runtime rule missing from general catalogue"
    previous = set()
    for path in sorted((root / "docs").glob("event-catalog-v*/catalog.json"),
                       key=lambda item: int(re.search(r"v(\d+)$", item.parent.name).group(1))):
        data = read(path)
        ids = {entry["id"] for entry in data["events"]}
        assert previous <= ids, f"Prior definitions lost in {path}"
        previous = ids
    entries = []
    source_files = {source, runtime_path, model_dir / "model_members.json", model_dir / "accepted_model.json"}

    def resolve(value):
        path = Path(value)
        if path.is_absolute():
            try:
                return path.relative_to(root).as_posix()
            except ValueError:
                return str(path)
        return value.replace("\\", "/")

    def paths_in(value):
        if isinstance(value, dict):
            for child in value.values():
                yield from paths_in(child)
        elif isinstance(value, list):
            for child in value:
                yield from paths_in(child)
        elif isinstance(value, str) and re.match(r"^[A-Za-z]:[\\/]", value):
            if "\n" not in value and len(value) < 500:
                yield value

    def definition(item):
        if item["id"] in runtime_by_id:
            return "Рабочий детектор описан в BreezeRules.json."
        for key in ("specification_markdown", "full_specification_markdown", "short_definition", "formula", "proposed_definition", "source_meaning"):
            if item.get(key):
                return item[key]
        if item.get("kind") == "sentiment_lifecycle_event":
            condition = {
                "A": "Сглаженный ряд выше 0,45, а предыдущие 21 конечное значение не превышали 0,45.",
                "B": "Сглаженный ряд сегодня выше 0,25, вчера был не выше 0,25.",
                "C": "Счётчик непрерывного пребывания сглаженного ряда строго ниже 0,25 пересёк 225: наступил 226-й бар.",
                "D": "Сглаженный ряд сегодня выше 0,25, а предыдущие 225 конечных значений не превышали 0,25."
            }.get(item.get("template"), "См. сохранённый протокол.")
            return (f"Источник: {item.get('source_name', item.get('source', ''))}. "
                    f"Исходный ряд: {item.get('source_formula', '')}. Шаблон {item.get('template', '')}; "
                    f"{condition} "
                    f"срок {item.get('hold', '')} торговых баров. "
                    f"Основное сглаживание: {json.dumps(item.get('primary_smoothing', {}), ensure_ascii=False)}. "
                    "Пропуски не засчитываются; новый старт внутри активного срока подавляется, срок не продлевается. "
                    "Детектор известен после закрытия, исполнение — не ранее следующего доступного открытия.")
        path = root / resolve(item.get("definition", ""))
        if path.is_file() and path.suffix == ".md":
            source_files.add(path)
            text = path.read_text(encoding="utf-8-sig")
            section = item.get("section", item["id"])
            headings = list(re.finditer(r"(?m)^#{1,6}\s+.*$", text))
            for index, heading in enumerate(headings):
                if re.search(rf"\b{re.escape(section)}\b", heading.group()):
                    end = headings[index + 1].start() if index + 1 < len(headings) else len(text)
                    return text[heading.start():end].strip()
        return "Исследовательская конструкция; полное определение и ограничения сохранены в связанных материалах."

    permanent = raw.get("permanent_numbers", {})
    if permanent:
        ordered = sorted(raw["events"], key=lambda item: permanent[item["id"]])
    else:
        ordered = [raw_by_id[entry["id"]] for entry in runtime]
        ordered.extend(entry for entry in raw["events"] if entry["id"] not in runtime_by_id)
    for number, item in enumerate(ordered, 1):
        key = item["id"]
        base = runtime_by_id.get(key)
        family = (base["classification"]["family"] if base else
                  item.get("family", key.split("-")[0] if "-" in key else re.sub(r"\d+$", "", key)))
        member = members.get(key, item.get("accepted_model_membership", {}))
        links = []
        for value in sorted(set(paths_in(item))):
            relative = resolve(value)
            path = root / relative
            links.append({"path": relative, "exists_at_build": path.is_file()})
        entries.append({"number": permanent.get(key, number), "id": key, "name": item.get("name", item.get("title", key)),
                        "family": family, "kind": item.get("kind", "research"),
                        "runtime_available": base is not None,
                        "accepted_selected": bool(member.get("selected_for_scoring", False)),
                        "accepted_nonzero": bool(member.get("configured_weight_nonzero", False)),
                        "status": item.get("catalogue_status", item.get("status", "research")),
                        "definition": definition(item), "references": links,
                        "accepted_membership": member, "research_record": item})
    output = {"schema_version": 1, "catalog_version": raw["version"], "source_path": source.relative_to(root).as_posix(),
              "counts": {"catalogue": len(entries), "runtime": len(runtime),
                         "runtime_active": sum(any(entry["breeze"]["weights"][mode] != 0 for mode in ("soft", "standard", "hard")) for entry in runtime),
                         "accepted_selected": sum(entry["accepted_selected"] for entry in entries),
                         "accepted_nonzero": sum(entry["accepted_nonzero"] for entry in entries)},
              "accepted_model": model_pointer["id"],
              "sources": [{"path": path.relative_to(root).as_posix(), "sha256": sha(path)} for path in sorted(source_files)],
              "entries": entries}
    target = root / "Breeze/BreezeEventCatalog.json"
    serialized = json.dumps(output, ensure_ascii=False, indent=2) + "\n"
    if args.check:
        assert target.read_text(encoding="utf-8") == serialized, "UI catalogue is stale: run tools/build_market_catalog.py"
    else:
        target.write_text(serialized, encoding="utf-8")
    print(json.dumps(output["counts"], ensure_ascii=False))


if __name__ == "__main__":
    main()
