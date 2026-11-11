"""Materialize the user-accepted additive research baseline, preserving v1."""
import csv, hashlib, json, sys
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT.parents[2]
EXPERIMENT = PROJECT/'research/artifacts/sentiment_detector_experiment_v2'
sys.path.insert(0,str(EXPERIMENT))
from detectors import load_data,features,detect
from calibration import apply_model,simulate,scalar_metrics

def main():
    protocol=json.loads((EXPERIMENT/'protocol.json').read_text('utf-8'))
    saved=json.loads((EXPERIMENT/'selected_models.json').read_text('utf-8'))
    schedule=[s for s in saved if s['family']=='G01' and s['mode']=='decibel']
    base=PROJECT/'research/artifacts/event_hypotheses_xhigh_v1/baseline'
    d=load_data(base/'data/Breeze');f=features(d)
    signals,starts,events=detect(d,f,protocol['primary'],protocol)
    addition=np.zeros(len(d['date']))
    for model in schedule:
        mask=np.char.startswith(d['date'],str(model['year']))
        addition[mask]=apply_model(model,signals['G01'],f)[mask]
    score=np.clip(d['base']+addition,0,100)
    ix=np.flatnonzero(np.isfinite(d['base']))
    with (ROOT/'accepted_score.csv').open('w',encoding='utf-8-sig',newline='') as h:
        writer=csv.writer(h);writer.writerow(['Date','BaseScore','G01','DecibelState','Addition','Score','BaseTarget','Target'])
        for i in ix:writer.writerow([d['date'][i],d['base'][i],signals['G01'][i],f['regimes']['D'][i],addition[i],score[i],10 if d['base'][i]>=50 else 0,10 if score[i]>=50 else 0])
    with (EXPERIMENT/'walkforward_paths.csv').open(encoding='utf-8-sig') as h:
        expected={r['Date']:float(r['G01__decibel_score']) for r in csv.DictReader(h)}
    assert all(np.isclose(score[i],expected[d['date'][i]],atol=1e-10) for i in ix if d['date'][i] in expected)
    sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
    metrics={}
    for name,mask in [('full',np.isfinite(d['base'])),('2020_2026',np.isfinite(d['base'])&(d['date']>='2020-01-01'))]:
        ids=np.flatnonzero(mask)
        metrics[name]={key:scalar_metrics(simulate(s[ids],d['Open'][ids],d['Close'][ids],2)) for key,s in [('base',d['base']),('accepted',score)]}
    manifest={'id':'standard_plus_g01_decibel_v1','accepted_by_user_date_moscow':'2026-10-25',
              'scope':'accepted baseline for subsequent research; runtime DLL deployment not performed',
              'base':{'id':'frozen_standard_75','snapshot':str(base),'rules_sha256':sha(base/'repo/Breeze/BreezeRules.json')},
              'composition':'clip(clip(50 + sum(original 75 Standard weighted events),0,100) + G01 * weight(Decibel state, schedule),0,100)',
              'event':'G01 variant A from sentiment_detector_experiment_v2; unchanged detector and thresholds',
              'regime':'Decibel vs median of preceding 252 bars, equality low',
              'schedule':[{'year':m['year'],'weights_low_high':m['weights'],'trained_through':m['train_end']} for m in schedule],
              'before_2020':'zero supplement; original base preserved',
              'continuation_from_2026':'retain last accepted weights [-20,+10]; no automatic refit or retrospective replacement',
              'score_series_sha256':sha(ROOT/'accepted_score.csv'),
              'reproduces_tested_G01_decibel_path':True,'metrics_2bps':metrics,
              'priority_next_sources':['широтыS&P.mp4','широтыS&P2.mp4']}
    (ROOT/'accepted_model.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    (PROJECT/'research/models/current_model.json').write_text(json.dumps({'id':manifest['id'],'manifest':str(ROOT/'accepted_model.json'),'scope':manifest['scope']},ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'accepted_model':str(ROOT/'accepted_model.json'),'matches_tested_path':True,'metrics':metrics},ensure_ascii=False,indent=2))

if __name__=='__main__':main()
