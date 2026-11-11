from pathlib import Path
import json, collections, re

def ini(path):
    if not path.exists(): return {}
    return {k.strip().lower():v.strip() for line in path.read_text(encoding='utf-8-sig').splitlines()
            if '=' in line for k,v in [line.split('=',1)]}

specs=[('Encog',Path('D:/model_ensemble_Encog'),['trading_package','trading_package_zz2']),
       ('Torch',Path('D:/TorchSharp'),['trading_package','trading_package_zz2','trading_package_J','trading_package_zz2_J'])]
defaults={'param_x6':40,'param_x6/1':40,'param_x8':28,'param_x10':120,'param_x13':3,'param_x14/1':64}
rows=[]
for family,root,dirs in specs:
    for folder in dirs:
        params=collections.defaultdict(collections.Counter)
        active=collections.Counter(); missingbench=0; count=0
        for p in sorted((root/folder).glob('*.ini')):
            d=ini(root/'system_param.ini') | ini(p)
            if not any(p.with_suffix(ext).exists() for ext in ('.eg','.pt','.jbin')): continue
            count+=1
            for key,value in defaults.items():
                fallback=d.get('param_x6',40) if key=='param_x6/1' else value
                params[key][str(d.get(key,fallback))]+=1
            features=d.get('activefeaturenames',d.get('allall_header','')).split(',')
            active.update(x.strip() for x in features)
            if not re.search(r'(?:^|_)Bench\s*=?\s*(-?\d+(?:\.\d+)?)',p.name,re.I): missingbench+=1
        rows.append(dict(Family=family,Folder=folder,Count=count,Parameters=params,ActiveFeatures=active,MissingBench=missingbench))
Path(__file__).with_name('inventory.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
for r in rows: print(r['Family'],r['Folder'],r['Count'],dict(r['Parameters']))
