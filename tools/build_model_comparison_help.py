"""Render the readable comparison into both Help variants; no scoring changes."""
from pathlib import Path
import html
import json
import re
import argparse

ROOT=Path(__file__).resolve().parents[1]
PACKAGE=ROOT/'research/model_comparison_20261028'
NAMES={r['id']:r['names']['ru'] for r in json.loads((PACKAGE/'inputs/BreezeRules.json').read_text(encoding='utf-8-sig'))['rules']}
NAMES.update({
 'B01':'средний уровень четырёх широт',
 'T05':'ширина веера EMA с учётом положения цены',
 'T07':'цена ниже всех четырёх EMA',
 'P03':'всплеск истинного диапазона цены (ATRP)',
 'V01':'знак и величина ближайшего контанго VIX',
 'Z02':'плотность сигналов Цвейга',
 'Z03':'совпадение двух режимов Цвейга',
 'G01':'устойчивость цены при новом минимуме индекса паники с учётом Decibel',
 'PH01':'подхват после коррекции с учётом Hurst и денежного потока CMF',
 'H01':'ранний разворот цены при нижнем MyRSI и росте широт',
 'H02':'подтверждённый срыв раннего восстановления',
 'H03':'возврат цены над EMA20 при удержанной EMA50',
 'H04':'ослабление быстрой широты при устойчивой долгой широте',
 'H05':'ценовой стресс при сохраняющемся контанго VIX',
 'H06':'новый минимум цены без ухудшения низкой широты',
 'F01':'согласованная скорость изменения четырёх широт',
 'F02':'согласованные расстояния и наклоны четырёх EMA',
 'F03':'соотношение скорости падения и восстановления цены',
 'F04':'относительный объём SPY с учётом направления движения цены'})
PATTERN=re.compile(r'(?<![A-Za-z0-9_])('+'|'.join(sorted(NAMES,key=len,reverse=True))+r')(?![A-Za-z0-9_])')

def humanize(text):
    saved=[]
    def keep(value):
        saved.append(value);return f'\x01{len(saved)-1}\x02'
    # Keep destinations, paths and literal source examples exact.
    text=re.sub(r'\]\(([^)]+)\)',lambda m:']('+keep(m[1])+')',text)
    text=re.sub(r'`[^`]+`',lambda m:keep(m[0]),text)
    # Existing explanatory table cells already carry meaning: put the code last.
    text=re.sub(r'(?m)(\|\s*)([A-Z]+\d{2})\s*[—:]\s*([^|\n]+)(?=\|)',
                lambda m:m[1]+keep(m[3].strip()+f' ({m[2]})')+' ',text)
    text=text.replace('H01–H06/F01–F04','шесть событий и четыре непрерывных признака')
    text=PATTERN.sub(lambda m:'«'+NAMES[m[1]]+'»'+f' ({m[1]})',text)
    text=re.sub(r'\bUnknown\b','«нет достоверного значения»',text)
    text=re.sub(r'\bLong\b','длинная позиция',text)
    text=re.sub(r'\bFlat\b','вне рынка',text)
    text=re.sub(r'\bMaxDD\b','максимальная просадка',text)
    text=text.replace('доступность score','доступность оценки').replace('известного score','известной оценки')
    text=re.sub(r'\bscore\b','оценка',text)
    text=re.sub(r'\bScore\b','Оценка',text)
    text=re.sub(r'\bequity\b','стоимость счёта',text)
    text=text.replace('checkpoint GPT','версия GPT').replace('парная блочная повторная выборка дневных P&L','парная блочная повторная выборка дневных прибылей и убытков')
    text=re.sub(r'\x01(\d+)\x02',lambda m:saved[int(m[1])],text)
    return text

def inline(text,base):
    saved=[]
    def keep(value):saved.append(value);return f'\x01{len(saved)-1}\x02'
    text=re.sub(r'`([^`]+)`',lambda m:keep('<code>'+html.escape(m[1])+'</code>'),text)
    def link(m):
        dest=m[2]
        if dest=='FACTORS.md':dest='#model-comparison-factors'
        elif dest=='REPORT.md':dest='#model-comparison'
        elif dest=='../model_comparison_20261028/REPORT.md':dest='#model-comparison' if base==PACKAGE else (base/dest).resolve().as_uri()
        elif dest=='../combined_model_20261029/REPORT.md':dest='#combined-model'
        elif not re.match(r'^(?:https?:|#)',dest):dest=(base/dest).resolve().as_uri()
        return keep('<a href="'+html.escape(dest,quote=True)+'">'+html.escape(m[1])+'</a>')
    text=re.sub(r'\[([^\]]+)\]\(([^)]+)\)',link,text)
    text=html.escape(text)
    text=re.sub(r'\*\*(.+?)\*\*',r'<strong>\1</strong>',text)
    text=re.sub(r'\x01(\d+)\x02',lambda m:saved[int(m[1])],text)
    return text

def render(markdown,prefix,base):
    lines=markdown.splitlines();out=[];nav=[];i=0;heading=0
    while i<len(lines):
        line=lines[i].strip()
        if not line:i+=1;continue
        m=re.match(r'^(#{1,3}) (.+)',line)
        if m:
            level=min(4,len(m[1])+1);anchor=prefix if heading==0 else f'{prefix}-{heading}'
            out.append(f'<h{level} id="{anchor}">'+inline(m[2],base)+f'</h{level}>')
            if len(m[1])==2:nav.append((anchor,m[2]))
            heading+=1;i+=1;continue
        if line.startswith('|'):
            rows=[]
            while i<len(lines) and lines[i].strip().startswith('|'):
                cells=[v.strip() for v in lines[i].strip().strip('|').split('|')]
                if not all(re.fullmatch(r':?-+:?',v) for v in cells):rows.append(cells)
                i+=1
            assert len({len(r) for r in rows})==1,'Unequal table columns'
            out.append('<div class="comparison-table-wrap"><table><thead><tr>'+''.join('<th>'+inline(v,base)+'</th>' for v in rows[0])+'</tr></thead><tbody>')
            out.extend('<tr>'+''.join('<td>'+inline(v,base)+'</td>' for v in row)+'</tr>' for row in rows[1:])
            out.append('</tbody></table></div>');continue
        if line.startswith('- '):
            out.append('<ul>')
            while i<len(lines) and lines[i].strip().startswith('- '):
                out.append('<li>'+inline(lines[i].strip()[2:],base)+'</li>');i+=1
            out.append('</ul>');continue
        para=[line];i+=1
        while i<len(lines) and lines[i].strip() and not re.match(r'^(#|\||- )',lines[i].strip()):para.append(lines[i].strip());i+=1
        out.append('<p>'+inline(' '.join(para),base)+'</p>')
    return '\n'.join(out),nav

START='<!-- MODEL-COMPARISON-START -->';END='<!-- MODEL-COMPARISON-END -->'
NAVSTART='<!-- MODEL-COMPARISON-NAV-START -->';NAVEND='<!-- MODEL-COMPARISON-NAV-END -->'
STYLE='''<style>
.model-comparison{margin:28px 0 18px;padding-top:10px;border-top:3px solid #17365d}
.model-comparison h2,.model-comparison h3{scroll-margin-top:12px}
.comparison-nav{padding:12px 16px;margin:12px 0;background:#eff5ff;border-left:4px solid #155eef}
.comparison-nav a{display:inline-block;margin:3px 16px 3px 0;color:#123b8f}
.comparison-table-wrap{width:100%;overflow-x:auto;margin:10px 0 14px}
.model-comparison table{min-width:700px;font-size:12px;line-height:1.45}
.model-comparison th{background:#e9f0f9}.model-comparison td,.model-comparison th{white-space:normal;word-wrap:break-word}
.model-comparison p{margin:10px 0;line-height:1.6}.model-comparison code{overflow-wrap:anywhere;font-size:12px}
.model-comparison .comparison-back{margin-top:24px}.model-comparison .comparison-back a{font-weight:600}
@media print{.comparison-table-wrap{overflow:visible}.model-comparison table{min-width:0;font-size:9px}.comparison-nav{display:none}}
</style>'''

def build():
    main,nav=render((PACKAGE/'REPORT.md').read_text(encoding='utf-8'),'model-comparison',PACKAGE)
    factors,_=render((PACKAGE/'FACTORS.md').read_text(encoding='utf-8'),'model-comparison-factors',PACKAGE)
    combined_path=ROOT/'research/combined_model_20261029/REPORT.md'
    combined=''
    if combined_path.exists():
        combined,combined_nav=render(combined_path.read_text(encoding='utf-8'),'combined-model',combined_path.parent)
        combined=combined.replace((PACKAGE/'REPORT.md').resolve().as_uri(),'#model-comparison')
        combined=combined.replace('</h2>','</h2><nav class="comparison-nav">'+''.join(f'<a href="#{anchor}">{html.escape(title)}</a>' for anchor,title in combined_nav)+'</nav>',1)
    toc='<nav class="comparison-nav" aria-label="Разделы сравнения">'+''.join(f'<a href="#{anchor}">{html.escape(title)}</a>' for anchor,title in nav)+'<a href="#model-comparison-factors">Полные таблицы факторов</a>'+('<a href="#combined-model">Объединённая модель: девять факторов</a>' if combined else '')+'</nav>'
    main=main.replace('</h2>','</h2>\n'+toc,1)
    block=START+'\n'+STYLE+'\n<section class="model-comparison">\n'+main+'\n'+combined+'\n'+factors+'\n<p class="comparison-back"><a href="#model-comparison">К началу сравнения</a></p>\n</section>\n'+END
    (PACKAGE/'comparison_help_fragment.html').write_text(block,encoding='utf-8')
    navblock=NAVSTART+'<p class="note"><a href="#model-comparison"><b>Сравнение скоринговых моделей Breeze Rules 1 и Rules 2</b></a> — результаты, понятные названия факторов и причины различий.'+(' <a href="#combined-model">Объединённая модель из девяти факторов</a> — исследовательский прототип и результаты проверки.' if combined else '')+'</p>'+NAVEND
    for path in [ROOT/'Breeze/BreezeHelp.html',ROOT/'Breeze/BreezeHelp.standalone.html']:
        source=path.read_text(encoding='utf-8')
        if START in source:source=re.sub(re.escape(START)+r'.*?'+re.escape(END),lambda m:block,source,flags=re.S)
        else:
            assert source.count('</main>')==1
            source=source.replace('</main>',block+'\n</main>')
        if NAVSTART in source:source=re.sub(re.escape(NAVSTART)+r'.*?'+re.escape(NAVEND),lambda m:navblock,source,flags=re.S)
        else:
            assert '<body><main>' in source
            source=source.replace('<body><main>','<body><main>'+navblock,1)
        path.write_text(source,encoding='utf-8')
    print('Embedded full comparison and factor tables in both Help files.')

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--humanize-report',action='store_true');args=parser.parse_args()
    if args.humanize_report:
        path=PACKAGE/'REPORT.md';original=path.read_text(encoding='utf-8')
        if '## Как читать названия и используемые переменные' not in original:
            text=humanize(original)
            glossary='''## Как читать названия и используемые переменные

Коды в скобках нужны только для поиска в программе и таблицах. В тексте каждый фактор назван словами. **Оценка модели** — число баллов; оно не является вероятностью роста. **Длинная позиция** означает владение акцией, **вне рынка** — отсутствие позиции. Покупка или продажа происходит на открытии следующей сессии; Open и Close в исходных формулах обозначают цену открытия и закрытия.

**Устойчивость цены при новом минимуме индекса паники с учётом Decibel (G01).** Проверяется, обновил ли индекс Panic Euphoria минимум последних четырёх различных наблюдений, пока цена SPY удерживает прежнюю опору — минимум закрытий за 20 сессий с допуском в четверть среднего истинного диапазона цены за 14 сессий. Decibel(96), рассчитанный по закрытиям SPY, определяет величину добавки: сравнивается со своей медианой за предыдущие 252 сессии. Это сочетание цены, индекса паники, диапазона колебаний и состояния Decibel, а не просто самостоятельный номер правила.

**Подхват после коррекции с учётом Hurst и денежного потока CMF (PH01).** Детектор помнит предшествующий рост и последующую коррекцию, затем ищет первый зелёный ответ при растущей широте выше SMA50 и удержанной EMA50. Hurst(120) описывает состояние ценового ряда; денежный поток Чайкина CMF(80) учитывает цену и объём. Каждый сравнивается со своей медианой предыдущих 252 сессий; сочетание двух состояний задаёт вес подхвата. Таким образом, одинаковый ценовой подхват может получить разные добавки к оценке.

**EMA20/50/100/200** — экспоненциальные средние цены за соответствующее число сессий. **SMA** — простая скользящая средняя. **Широты B50, B100, B200** в формулах — доли акций S&P 500 выше SMA50, SMA100 и SMA200; четвёртая широта — доля акций Nasdaq-100 выше SMA100. **MyRSI** — используемый в Breeze нормированный индикатор от −1 до +1. **ATRP(1)** — истинный диапазон дневной свечи с учётом гэпа, в процентах от цены закрытия. **Ближайшее контанго VIX** — превышение цены второго фьючерса над первым; front spread в исходных формулах означает этот спред. **Коэффициент Шарпа** сопоставляет среднюю доходность и её изменчивость; максимальная просадка показывает наибольшее падение стоимости счёта от уже достигнутого пика.

'''
            text=text.replace('## Основной вывод',glossary+'## Основной вывод',1)
            path.write_text(text,encoding='utf-8')
    build()
