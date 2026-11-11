#!/usr/bin/env python3
"""Create event-catalog-v8 with the Contango 2 definitions."""
from __future__ import annotations

import copy
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "docs/event-catalog-v7/catalog.json"
TARGET_DIR = ROOT / "docs/event-catalog-v8"


def read(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def event(key: str, name: str, status: str, definition: str, lifecycle: str, unresolved: str):
    return {
        "id": key,
        "name": name,
        "family": "VIX и форма срочной кривой",
        "kind": "contango_state_or_event",
        "catalogue_status": status,
        "source_meaning": definition,
        "proposed_definition": definition,
        "lifecycle": lifecycle,
        "unresolved": unresolved,
        "analysis": "D:/CODEX_PRG/Breadth/research/artifacts/contango2_research_v1/analysis.md",
        "implementation_note": "D:/CODEX_PRG/Breadth/research/artifacts/contango2_research_v1/transition-stabilization.md",
    }


def main() -> None:
    catalog = copy.deepcopy(read(SOURCE))
    additions = [
        event("V09", "Незначительный локальный излом кривой VIX", "implemented_contango_classifier",
              "Соседнее снижение в пределах 0,5% для первых трёх контрактов или 1% для дальней части считается почти плоским участком, а не полноценной инверсией.",
              "Ежедневная поправка геометрии до определения итогового состояния New.",
              "Пороги являются согласованной первой реализацией и подлежат последующей проверке на устойчивость."),
        event("V10", "Накопленная глубина и ширина инверсии VIX", "implemented_contango_classifier",
              "Несколько малых снижений не игнорируются, если накопленное падение хвоста превышает 1,5%; F2 и F3 ниже F1 больше front-допуска запрещают выраженное контанго.",
              "Состояние сохраняется, пока значимая широкая или накопленная инверсия присутствует.",
              "Граница хвоста начинается с 90 дней; альтернативы 90/120 дней остаются исследовательской проверкой."),
        event("V11", "Подтверждение перехода между двумя зелёными состояниями", "implemented_contango_classifier",
              "Слабый переход Contango↔StrongContango требует двух последовательных торговых снимков; spot-стресс и выраженный ценовой стресс понижают состояние немедленно.",
              "Первое слабое изменение удерживает прежний оттенок; второе подтверждает переход. Пропуск данных не считается подтверждением.",
              "Память 1/2/3 дня должна сравниваться после пользовательской проверки первой реализации."),
        event("V12", "Геометрия контанго восстановилась, но стресс сохраняется", "implemented_contango_classifier",
              "Кривая геометрически соответствует контанго, но spot VIX выше ближайшего фьючерса более чем на 0,5%. Выраженное контанго ограничивается обычным Contango и сопровождается явным объяснением.",
              "Состояние действует, пока превышение spot над F1 остаётся значимым; исчезает без ретроспективной перекраски.",
              "Само превышение spot не объявляется бэквордацией и не заменяет оценку всей кривой."),
        event("V13", "Выраженный ценовой стресс при нарушенном front VIX", "implemented_contango_classifier",
              "SPY снижается, ATRP(1) не ниже 2%, spot VIX выше F1/F2/F3 более чем на 0,5%, а один из двух front-спредов ниже −0,5%; зелёное состояние ограничивается Mixed.",
              "Понижение применяется немедленно по доступному после закрытия снимку.",
              "Рыжий и красный уровни требуют отдельной калибровки тяжести; текущая реализация использует Mixed."),
        event("V14", "Контекст экспирации и смены ближайшего контракта", "research",
              "DTE ближайшего контракта, смена набора узлов и ролловер рассматриваются отдельно; близость экспирации не отменяет реальный spot- или ценовой стресс.",
              "Календарный контекст вычисляется на каждом снимке, а день смены контракта не образует ложный переход.",
              "Нужны надёжные идентификаторы контрактов и окончательная граница окна 0/1/3/5 дней."),
        event("V15", "Локальный дальний горб кривой VIX", "research",
              "Отдельно измеряются наклон front, дальний уровень и выпуклость узла относительно интерполяции соседних сроков; устойчивый дальний горб не приравнивается к фронтальной инверсии.",
              "Состояние горба действует, пока локальная выпуклость превышает выбранный допуск.",
              "Сезонная причина не установлена; требуются символы контрактов и отдельная проверка."),
        event("V16", "Продолжительность spot/front-стресса", "research",
              "Считается серия последовательных снимков, где spot VIX выше front и/или сохраняется значимая фронтальная инверсия, вместе с изменением глубины за 1–3 торговых бара.",
              "Пропуск данных разрывает подтверждённую серию; повтор после пропуска начинается заново.",
              "Нельзя суммировать длительность как независимый полный штраф поверх V13 без проверки добавочной пользы."),
    ]
    existing = {item["id"] for item in catalog["events"]}
    if existing.intersection(item["id"] for item in additions):
        raise RuntimeError("Contango 2 IDs already exist")
    catalog["events"].extend(additions)
    numbers = catalog.setdefault("permanent_numbers", {})
    next_number = max(numbers.values()) + 1
    for item in additions:
        numbers[item["id"]] = next_number
        next_number += 1
    catalog.update(
        version="event-catalog-v8",
        date_moscow="2026-10-29",
        scope="Unified registry extended with Contango 2 corrected curve states and research candidates.",
    )
    catalog.setdefault("counts", {})["all_entries"] = len(catalog["events"])
    write(TARGET_DIR / "catalog.json", catalog)
    (TARGET_DIR / "CATALOG.md").write_text(
        "# Каталог событий v8\n\n"
        "Версия v7 сохранена без изменений. Добавлены восемь записей V09–V16 семейства "
        "«VIX и форма срочной кривой» из анализа Contango 2. V09–V13 реализованы внутри "
        "единого расчёта Contango New; V14–V16 сохранены как исследовательские определения.\n",
        encoding="utf-8",
    )
    write(ROOT / "docs/event-catalog-current.json", {
        "version": "event-catalog-v8",
        "path": str(TARGET_DIR / "catalog.json"),
        "human_readable": str(TARGET_DIR / "CATALOG.md"),
        "count": len(catalog["events"]),
    })
    print(f"Contango 2 events registered: {len(additions)}; total={len(catalog['events'])}")


if __name__ == "__main__":
    main()
