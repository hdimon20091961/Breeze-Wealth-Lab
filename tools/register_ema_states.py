"""Register finite EMA display states, preserving frozen scoring and old IDs."""
import copy,csv,json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def read(p):return json.loads(p.read_text(encoding="utf-8-sig"))
def write(p,x):p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")

def main():
    path=ROOT/"Breeze/BreezeRules.json";doc=read(path)
    old=read(ROOT/"docs/event-catalog-v6/catalog.json")
    numbers={e["id"]:e["number"] for e in read(ROOT/"research/artifacts/ema_states_release_v1/baseline/Breeze/BreezeEventCatalog.json")["entries"]}
    specs=[
      ("T01B","Медвежий порядок EMA","E20<E50<E100<E200",-1,"EMA20 < EMA50 < EMA100 < EMA200. Только порядок уровней, ежедневно, без цены, наклонов и расстояний.","T01_bear"),
      ("T02B","Все четыре EMA падают","All four one-bar EMA slopes <0",-1,"Каждая EMA20/50/100/200 ниже своего значения предыдущего торгового дня; порядок уровней не используется.","T02_bear"),
      ("T03B","Устойчивое снижение — сила состояния","Bear order + all slopes falling + C<E20 + configured breadth ceilings + ATRP cap; observed age/k capped at 1",-1,"Медвежий порядок, все четыре EMA падают, Close ниже EMA20, четыре широты не выше зеркальных порогов, ATRP не выше границы. Сила min(целый возраст/k,1).","T03_original_bear"),
      ("T03_1","T03/1 — Подтверждённый устойчивый рост","Bull order + all slopes rising + C>E20 + configured breadth floors + ATRP cap; observed age>=k",1,"Бычий порядок, все четыре EMA растут, Close выше EMA20, четыре широты не ниже порогов, ATRP не выше границы. Значение 1 при возрасте не меньше k, иначе 0.","T03_confirmed5_bull"),
      ("T03_1B","T03/1 — Подтверждённое устойчивое снижение","Bear order + all slopes falling + C<E20 + configured breadth ceilings + ATRP cap; observed age>=k",-1,"Медвежий порядок, все четыре EMA падают, Close ниже EMA20, четыре широты не выше зеркальных порогов, ATRP не выше границы. Значение 1 при возрасте не меньше k, иначе 0.","T03_confirmed5_bear")]
    patterns=[[-1,1,1,1],[1,-1,1,1],[-1,-1,1,1],[1,-1,-1,-1],[-1,1,-1,-1],[1,1,-1,-1]]
    for i,pattern in enumerate(patterns,1):
        name=f"T04/{i} — "+" / ".join("↑" if s>0 else "↓" for s in pattern)
        ru="; ".join(f"EMA{p} {'растёт' if s>0 else 'падает'}" for p,s in zip([20,50,100,200],pattern))+". Сравнение со своим предыдущим торговым днём; порядок уровней не используется."
        specs.append((f"T04_{i}",name,"EMA slopes: "+",".join("+" if s>0 else "-" for s in pattern),1 if i<=3 else -1,ru,f"T04_{i}"))
    summary=list(csv.DictReader((ROOT/"research/artifacts/ema_t01_t04_experiment_v1/results/fixed_state_summary.csv").open(encoding="utf-8-sig")))
    extras=[];events=[]
    for number,(key,name,formula,direction,ru,result_name) in enumerate(specs,131):
        r=copy.deepcopy(doc["rules"][0]);r.update(number=number,id=key,names={"ru":name,"code":key},classification={"family":"T","kind":"state"})
        r["detector"]={"implemented_formula":formula,"output":"daily state, 0..1","calculation_status":"implemented","display_direction":direction}
        if key.startswith("T03"):r["detector"]["parameters"]={"atrp_max_percent":2,"breadth_thresholds":[50,50,50,50],"confirmation_bars":5}
        r["breeze"]["enabled"]=True;r["breeze"]["frozen"]=False;r["breeze"]["weights"]={"soft":0,"standard":0,"hard":0}
        r["breeze"]["lifecycle"]="daily cached detector, separate from frozen scoring vector"
        r["torchsharp"].update(value_source=key,enabled=False,weight_status="not_calibrated",note="Детектор включён в Market Setup; ненулевой вклад TorchSharp не откалиброван.")
        r["documentation"]={"source":"EMA T01–T04, 26.10.2026","source_time":{"start":"00:00:32","end":"00:15:31"},"meaning":ru,"proposed_definition":ru,
          "lifecycle":"Целочисленный возраст прошедших дней; нарушение сбрасывает возраст, сила отдельно." if key.startswith("T03") else "Только ежедневное состояние 0/1.",
          "unresolved":"HLC3 используется только для результатов на 3/5/10 баров. Веса не назначаются по малым или неустойчивым выборкам."}
        doc["detector_descriptions_ru"][key]=ru;extras.append(r);numbers[key]=number
        events.append({"id":key,"name":name,"kind":"ema_daily_state","catalogue_status":"implemented_display_enabled","formula":ru,
          "rule_source":"D:/CODEX_PRG/Breadth/Breeze/BreezeRules.json","research_report":"D:/CODEX_PRG/Breadth/research/artifacts/ema_t01_t04_experiment_v1/RESULTS.md",
          "research_protocol":"D:/CODEX_PRG/Breadth/research/artifacts/ema_t01_t04_experiment_v1/protocol.json","test_results":[x for x in summary if x["name"]==result_name],
          "configuration":r["detector"].get("parameters",{}),"outcome_basis":"HLC3 forward outcomes only; Close-based detectors unchanged; 3/5/10 bars",
          "scoring_status":"daily calculation enabled; three profile weights zero pending calibration"})
    doc.update(additional_states=extras,schema_version="1.2.0",catalog_version="1.2.0",updated="2026-10-26")
    doc["detector_descriptions_ru"]["T01"]="EMA20 > EMA50 > EMA100 > EMA200. Только порядок уровней; цена, наклоны и расстояния не входят в условие."
    for r in doc["rules"]:
        if r["id"]=="T01":r["documentation"].update(meaning="Только строгий бычий порядок уровней EMA.",proposed_definition="EMA20>EMA50>EMA100>EMA200.",lifecycle="Только ежедневное состояние 0/1.",unresolved="Наклоны, расстояния, цена и длина серии в это правило не входят.")
        if r["id"]=="T02":r["documentation"].update(lifecycle="Ежедневное состояние; все четыре растут при значении 1.",unresolved="Только сравнение с предыдущим торговым днём; порядок EMA не используется.")
        if r["id"]=="T04":r["documentation"].update(meaning="Объединение T04/1 и T04/3; только однодневные направления, без порядка уровней.",lifecycle="Только ежедневное состояние 0/1.")
    write(path,doc)
    cat=copy.deepcopy(old);cat.update(version="event-catalog-v7",date_moscow="2026-10-26",scope="Unified registry with enabled EMA display states; scoring profiles remain separate.",permanent_numbers=numbers)
    cat["events"]+=events;cat["counts"].update(all_entries=141,new_enabled_ema_states=11)
    write(ROOT/"docs/event-catalog-v7/catalog.json",cat)
    write(ROOT/"docs/event-catalog-current.json",{"version":"event-catalog-v7","path":str(ROOT/"docs/event-catalog-v7/catalog.json"),"human_readable":str(ROOT/"docs/event-catalog-v7/CATALOG.md"),"count":141})
    print("11 enabled daily states; original 75 weights preserved; catalogue 141.")

if __name__=="__main__":main()
