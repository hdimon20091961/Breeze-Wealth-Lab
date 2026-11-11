"""Insert the operating guide without modifying historical research sections."""
from pathlib import Path
import re
from build_model_comparison_help import ROOT,render
doc=ROOT/'docs/market-breeze-rules-switch.md'
body,_=render(doc.read_text(encoding='utf-8'),'rules-switch-guide',doc.parent)
start='<!-- RULES-SWITCH-HELP-START -->';end='<!-- RULES-SWITCH-HELP-END -->'
block=start+'\n<section>'+body+'</section>\n'+end
for p in [ROOT/'Breeze/BreezeHelp.html',ROOT/'Breeze/BreezeHelp.standalone.html']:
    s=p.read_text(encoding='utf-8')
    s=s.replace('<title>Zweig Breeze</title>','<title>Market Breeze</title>').replace('<h1>Zweig Breeze</h1>','<h1>Market Breeze</h1>')
    s=s.replace('Справка расширения Breeze 0.10.25 для Wealth-Lab 8','Справка Market Breeze 0.11.3 для Wealth-Lab 8')
    s=s.replace('Справка Market Breeze 0.11.0 для Wealth-Lab 8','Справка Market Breeze 0.11.3 для Wealth-Lab 8')
    s=s.replace('Справка Market Breeze 0.11.2 для Wealth-Lab 8','Справка Market Breeze 0.11.3 для Wealth-Lab 8')
    s=s.replace('<b>25 торговых баров</b>','<b>20 торговых баров</b>')
    if start in s:s=re.sub(re.escape(start)+'.*?'+re.escape(end),lambda m:block,s,flags=re.S)
    else:s=s.replace('<h2>Обзор</h2>',block+'\n<h2>Обзор</h2>',1)
    # Replace the old live-score table, whose colours and inferred actions are obsolete.
    a=s.index('<h3>Оценка модели, 0–100 баллов</h3>' if '<h3>Оценка модели, 0–100 баллов</h3>' in s else '<h3>Оценки Rules1 и Rules2, 0–100 баллов</h3>')
    z=s.index('<div class="formula">Score =',a)
    s=s[:a]+'''<h3>Оценки Rules1 и Rules2, 0–100 баллов</h3>
<p>Пять дискретных цветов и торговые пороги описаны в <a href="#rules-switch-guide">руководстве Market Breeze 0.11.3</a>. Цветовая полоса показывает диапазон оценки; целевая позиция зависит от выбранной модели и её состояния. Следующая формула и каталог 75 состояний относятся к Rules1.</p>
'''+s[z:]
    s=s.replace('Нижняя рамка выводит точную арифметику выбранного профиля: стартовые 50 баллов, все ненулевые слагаемые и итог.',
        'Нижняя рамка показывает до десяти наиболее сильных ненулевых вкладов выбранной модели и профиля.')
    s=s.replace('Все 75 состояний пересчитываются ежедневно.','В Rules1 все 75 состояний пересчитываются ежедневно.')
    s=s.replace(
        'Чёрная точка на дате <b>D</b> показывает максимальный действующий Stop, заранее рассчитанный после бара <b>D−1</b>: High самого бара D в нём использоваться не может. Чёрный крестик',
        'Чёрная заполненная точка на дате <b>D</b> показывает максимальный действующий Stop, заранее рассчитанный после бара <b>D−1</b>: High самого бара D в нём использоваться не может. Пустой чёрный кружок в отдельном будущем слоте справа от последней свечи показывает Stop, уже рассчитанный после последнего закрытия для следующей торговой сессии. Если последний бар был днём входа, этот будущий уровень использует High дня входа, но ещё не считается внутридневной защитой завершившегося бара и не изменяет исторический профит. Чёрный крестик')
    s=s.replace(
        'верхний график показывает Last и ближайший действующий Stop с двумя знаками в правом верхнем углу.',
        'верхний график показывает Last и ближайший действующий либо уже рассчитанный будущий Stop с двумя знаками в правом верхнем углу.')
    s=s.replace(
        'Нижняя строка следует порядку <b>дата → Stop → ZZ1, ZZ2… → Active</b>.',
        'Нижняя строка следует порядку <b>дата → Active P% → Stop → ZZ1, ZZ2…</b>.')
    p.write_text(s,encoding='utf-8')
print('Updated Market Breeze operating guide in both Help variants.')
