from pathlib import Path
import hashlib, json

root = Path(__file__).resolve().parents[2]
out = Path(__file__).resolve().parent
files = {'Strategy.cs': root / 'ген_на_TorchSharp_oll_indi_ансамбльGPT_mult_(что если).cs',
         'Inference.cs': root / 'Breeze/TradingEnsembleInference.cs'}
manifest = {}
for target, source in files.items():
    data = source.read_bytes()
    text = data.decode('utf-8-sig').replace('\r\n','\n').replace('GetHistory(bars, ', 'AuditHistory.Get(bars, ')
    if target == 'Strategy.cs':
        text = text.replace('WriteToDebugLog(', 'AuditHistory.Log(')
    (out / target).write_text(text, encoding='utf-8')
    manifest[str(source)] = hashlib.sha256(data).hexdigest()
(out / 'source_hashes.json').write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding='utf-8')
# Only the history read boundary and debug logging are redirected. No formulas
# or model loaders are changed in the audit copies. Initialize is never called.
project = (root / 'tests/EnsembleStrategyCompile/EnsembleStrategyCompile.csproj').read_text(encoding='utf-8-sig')
project = project.replace('<OutputType>Library</OutputType>', '<OutputType>Exe</OutputType><UseWPF>true</UseWPF>')
project = project.replace('<ImplicitUsings>disable</ImplicitUsings>', '<ImplicitUsings>enable</ImplicitUsings>')
project = project.replace('<Nullable>disable</Nullable>', '<Nullable>disable</Nullable><NoWarn>CS8632</NoWarn>')
start = project.index('    <Compile Include=')
end = project.index('\n', start)
project = project[:start] + '''    <Compile Include="*.cs" />
    <Compile Include="../../Breeze/TradingEnsembleData.cs" Link="Data.cs" />''' + project[end:]
(out / 'Audit.csproj').write_text(project, encoding='utf-8')
