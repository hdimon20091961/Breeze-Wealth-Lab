from pathlib import Path
import hashlib,json

out=Path(__file__).resolve().parent
root=out.parents[1]
baseline=root/'research/ensemble_parity_20261102'
manifest={}
for name,src in {
    'Strategy.cs':root/'ген_на_TorchSharp_oll_indi_ансамбльGPT_mult_(что если).cs',
    'Inference.cs':root/'Breeze/TradingEnsembleInference.cs',
    'GpuBaseline.cs':root/'preservation/before-0.11.17-20261102/Breeze/TradingEnsembleInference.cs',
}.items():
    data=src.read_bytes()
    text=data.decode('utf-8-sig').replace('\r\n','\n').replace('GetHistory(bars, ','AuditHistory.Get(bars, ')
    if name=='Strategy.cs': text=text.replace('WriteToDebugLog(','AuditHistory.Log(')
    if name=='GpuBaseline.cs': text=text.replace('TradingEnsembleInference','GpuBaselineInference')
    (out/name).write_text(text,encoding='utf-8')
    manifest[str(src)]=hashlib.sha256(data).hexdigest()
for name in ('Audit.csproj','AuditHistory.cs'):
    (out/name).write_text((baseline/name).read_text(encoding='utf-8-sig'),encoding='utf-8')
program=(baseline/'Program.cs').read_text(encoding='utf-8-sig')
program=program.replace('internal static class Program','internal static partial class Program')
program=program.replace('object dp = Call(dllType,"LoadParameters",ini,Path.Combine(spec.Root,"system_param.ini"));',
    'object dp = Call(dllType,"LoadLegacyParameters",ini,Path.Combine(spec.Root,"system_param.ini"),spec.Ext=="eg");')
program=program.replace('var source = new WealthScript1.MyStrategy2779();',
    'if (args.Contains("gpu")) { CheckGpu(bars); return; }\n        var source = new WealthScript1.MyStrategy2779();')
program=program.replace('int differing125=rows.Sum',
    'ValidatePackageRoute(engine,bars,spec.Key,spec.Root,spec.Dir,spec.Ext,rows);\n            int differing125=rows.Sum')
program=program.replace('if(dll) sorted=sorted.OrderBy(r=>r.Dbench).ThenBy(r=>r.Name,StringComparer.OrdinalIgnoreCase).ToList();\n        else sorted.Sort((a,c)=>a.Bench.CompareTo(c.Bench));',
    'sorted.Sort((a,c)=>(dll?a.Dbench:a.Bench).CompareTo(dll?c.Dbench:c.Bench));')
(out/'Program.cs').write_text(program,encoding='utf-8')
(out/'source_hashes.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
