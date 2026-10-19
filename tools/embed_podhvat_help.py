from __future__ import annotations

import csv
import html
from pathlib import Path


ROOT = Path(r"D:\CODEX_PRG\Breadth")
RESEARCH = ROOT / "artifacts" / "Podhvat_1" / "research"
HELP = ROOT / "Breeze" / "BreezeHelp.html"
START = "<!-- PODHVAT-RESEARCH-START -->"
END = "<!-- PODHVAT-RESEARCH-END -->"


def read_csv(name: str) -> list[dict[str, str]]:
    with (RESEARCH / name).open("r", encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def fmt(value: str, digits: int = 2) -> str:
    if value is None or value == "":
        return "—"
    try:
        return f"{float(value):.{digits}f}".replace(".", ",")
    except ValueError:
        return html.escape(value)


def table(headers: list[tuple[str, str]], rows: list[dict[str, str]], css: str = "research-table") -> str:
    head = "".join(f"<th>{html.escape(label)}</th>" for _, label in headers)
    body = []
    for row in rows:
        cells = []
        for key, _ in headers:
            value = row.get(key, "")
            if key == "Date" and value:
                rendered = html.escape(value[:10])
            elif key in {"EventType", "Subtype", "Phase", "Notes", "VIXCurveState", "SVXYRecommendation"}:
                rendered = html.escape(value) if value else "—"
            elif key in {"N", "TrainThrough", "TestYear", "CompressedN", "StretchedN", "HighVolN"}:
                rendered = fmt(value, 0)
            else:
                rendered = fmt(value, 2)
            cells.append(f"<td>{rendered}</td>")
        body.append("<tr>" + "".join(cells) + "</tr>")
    return f'<div class="table-scroll"><table class="{css}"><thead><tr>{head}</tr></thead><tbody>{"".join(body)}</tbody></table></div>'


summary = read_csv("event_summary.csv")
walk_forward = read_csv("walk_forward.csv")
events = read_csv("events.csv")
daily = read_csv("daily_features.csv")

summary_headers = [
    ("EventType", "Событие"), ("Subtype", "Вариант"), ("N", "N"),
    ("AvgFwd5Pct", "Среднее 5д, %"), ("WinRate5Pct", "+ 5д, %"),
    ("AvgFwd10Pct", "Среднее 10д, %"), ("WinRate10Pct", "+ 10д, %"),
    ("AvgFwd20Pct", "Среднее 20д, %"), ("WinRate20Pct", "+ 20д, %"),
    ("NextDayBodyMidTouchPct", "50% тела на след. день, %"),
    ("NextDayRangeMidTouchPct", "50% диапазона, %"),
]

wf_headers = [
    ("TrainThrough", "Обучение до"), ("TestYear", "Тест"), ("EventType", "Событие"), ("N", "N"),
    ("TrainFanQ33Pct", "Fan Q33"), ("TrainFanQ67Pct", "Fan Q67"),
    ("TrainATRPMedianPct", "Медиана ATRP"), ("AvgFwd5Pct", "Среднее 5д, %"),
    ("WinRate5Pct", "+ 5д, %"), ("AvgFwd10Pct", "Среднее 10д, %"),
    ("WinRate10Pct", "+ 10д, %"), ("AvgFwd20Pct", "Среднее 20д, %"),
    ("WinRate20Pct", "+ 20д, %"),
]

event_headers = [
    ("Date", "Дата"), ("EventType", "Событие"), ("Subtype", "Вариант"),
    ("Close", "SPY"), ("ATRP_1", "ATRP, %"), ("Phase", "Phase"),
    ("Contango30To60Pct", "Contango 30–60, %"), ("Fwd5Pct", "5д, %"),
    ("Fwd10Pct", "10д, %"), ("Fwd20Pct", "20д, %"), ("Notes", "Примечание"),
]

daily_headers = [
    ("Date", "Дата"), ("Open", "Open"), ("High", "High"), ("Low", "Low"), ("Close", "Close"),
    ("ATRP_1", "ATRP, %"), ("EMA20", "EMA20"), ("EMA50", "EMA50"),
    ("EMA100", "EMA100"), ("EMA200", "EMA200"), ("SP500_SMA50", "S&P > SMA50, %"),
    ("SP500_SMA100", "S&P > SMA100, %"), ("SP500_SMA200", "S&P > SMA200, %"),
    ("NASDAQ100_SMA100", "NDX > SMA100, %"), ("Phase", "Phase"),
    ("Contango30To60Pct", "Contango, %"), ("Fwd5Pct", "5д, %"),
    ("Fwd10Pct", "10д, %"), ("Fwd20Pct", "20д, %"),
]

block = f"""
{START}
<style>
  .research-nav{{position:sticky;top:0;z-index:3;margin:8px 0;padding:9px;background:#17365d;border-radius:6px}}
  .research-nav a{{display:inline-block;margin:2px 8px 2px 0;color:#fff;text-decoration:none;font-weight:600}}
  .research-kpi{{display:table;width:100%;table-layout:fixed;margin:8px 0 12px}}
  .research-kpi span{{display:table-cell;padding:10px;border-right:6px solid #fff;background:#e9f2fb;text-align:center}}
  .research-kpi b{{display:block;color:#17365d;font-size:20px}}
  .table-scroll{{max-height:520px;overflow:auto;border:1px solid #cfd8e3;margin:8px 0 16px}}
  .research-table{{margin:0;white-space:nowrap;font-size:11px;background:#fff}}
  .research-table th{{position:sticky;top:0;z-index:2;background:#4472c4;color:#fff}}
  .research-table td:last-child{{white-space:normal;min-width:260px}}
  .method-card{{margin:7px 0;padding:9px 11px;background:#f7f9fc;border-left:4px solid #4472c4}}
  .warning-card{{margin:8px 0;padding:10px 12px;background:#fce4d6;border-left:4px solid #ed7d31}}
</style>
<h2 id="research">Исследование «Подхват 1»: дневные данные</h2>
<nav class="research-nav">
  <a href="#research-overview">Overview</a><a href="#research-summary">Summary</a>
  <a href="#research-walk">Walk Forward</a><a href="#research-events">Events</a>
  <a href="#research-daily">Daily Data</a><a href="#research-method">Methodology</a>
</nav>

<h3 id="research-overview">Overview</h3>
<div class="research-kpi"><span><b>116</b>кадров Podhvat_1</span><span><b>439</b>кадров всего</span><span><b>3 016</b>дневных баров</span><span><b>303</b>события</span></div>
<p>Период: <b>13.10.2014–09.10.2026</b>. В новом видео 36 речевых опорных и 80 устойчивых кадров. Картинки используются для формулировки гипотез; статистика считается по полной дневной истории, поэтому соседние кадры одного эпизода не считаются разными сделками.</p>
<p>Свежий <code>spy_atrp1.csv</code> включён. Максимальное абсолютное расхождение между экспортом и объединённой колонкой <code>ATRP_1</code> равно <b>0,0</b>.</p>

<h3 id="research-summary">Summary</h3>
<p>Доходность измеряется от закрытия сигнального дня. «+» — доля положительных исходов на соответствующем горизонте.</p>
{table(summary_headers, summary)}
<div class="note"><b>Краткий вывод:</b> стабильный рост и неглубокий подхват EMA20 дают наиболее последовательное продолжение. После сильной красной свечи выражен отскок, однако возврат ровно к 50% на следующий день происходит лишь примерно в половине случаев.</div>

<h3 id="research-walk">Walk Forward</h3>
<p>Для каждого тестового года используются только более ранние данные. На прошлом отрезке вычисляются Q33/Q67 ширины веера EMA и медиана ATRP, затем границы замораживаются на весь следующий календарный год.</p>
{table(wf_headers, walk_forward)}
<div class="warning-card"><b>Ограничение:</b> это историческая временная прокрутка, а не полностью независимый out-of-sample. Авторские идеи были сформулированы после просмотра исторических графиков. Настоящий форвард начинается после фиксации правил и на будущих невиденных барах.</div>

<h3 id="research-events">Events</h3>
<p>303 разнесённых по времени события. Интервалы дедупликации: 10 баров для начала стабильного режима, 5 для EMA-подхвата и 3 для сильных свечей.</p>
{table(event_headers, events)}

<h3 id="research-daily">Daily Data</h3>
<p>Полная дневная панель из 3 016 строк. Последние 5/10/20 строк не имеют форвардного результата там, где соответствующий будущий горизонт ещё не наступил.</p>
{table(daily_headers, daily)}

<h3 id="research-method">Methodology</h3>
<div class="method-card"><b>Стабильный рост.</b> Close &gt; EMA20 &gt; EMA50 &gt; EMA100 &gt; EMA200; пятидневные наклоны всех EMA положительны; четыре ряда широты выше 50%.</div>
<div class="method-card"><b>Подхват EMA.</b> Зелёное закрытие выше EMA20 после хотя бы одного закрытия ниже EMA20 за предыдущие пять баров; наклоны EMA положительны. Глубина определяется по самой глубокой достигнутой зоне EMA.</div>
<div class="method-card"><b>Медвежья опорная свеча.</b> Красный бар с телом или изменением закрытие-к-закрытию не выше −2,0%.</div>
<div class="method-card"><b>Бычья опорная свеча.</b> Зелёный бар с телом или изменением закрытие-к-закрытию не ниже +2,5%.</div>
<div class="method-card"><b>Половина свечи.</b> Отдельно проверяются середина тела, середина полного High–Low и середина движения от предыдущего Close до текущего Close.</div>
<p class="small">Таблицы встроены в Breeze.dll и не запускают получение истории, расчёт индикаторов или экспорт при открытии Help.</p>
{END}
"""

source = HELP.read_text(encoding="utf-8")
if START in source and END in source:
    before, tail = source.split(START, 1)
    _, after = tail.split(END, 1)
    result = before + block + after
else:
    anchor = "  <h2>Экспорт</h2>"
    if anchor not in source:
        raise RuntimeError("Help insertion anchor not found")
    result = source.replace(anchor, block + "\n" + anchor, 1)

HELP.write_text(result, encoding="utf-8")
print(f"Embedded {len(summary)} summary rows, {len(walk_forward)} walk-forward rows, {len(events)} events, {len(daily)} daily rows")
print(f"Help size: {HELP.stat().st_size} bytes")
