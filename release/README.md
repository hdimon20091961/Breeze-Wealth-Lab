# Market Breeze 0.11.33

Исправлено воспроизведение исходной стратегии Wealth-Lab «что если» для
TorchSharp ZZ1/ZZ2, Jordan ZZ1/ZZ2 и Encog ZZ1/ZZ2.

- Подготовка признаков повторяет эталон: Aroon 48, CorrelationAngle 48,
  Hurst 80, Jocker EMA 20, marketBreadth VWMA 125.
- Bench распознаётся в форматах Bench800 и Bench=697, в том числе после пробела.
  Состав децилей повторяет сортировку эталона, включая одинаковые Bench.
- Ensembles включён: пропорциональная позиция 0–10 (режим 0 исходника).
  Выключен: независимые децили (режим 1).
- Str% за 125 торговых баров не зависит от режима торговли.
- Подготовка признаков и прогнозы трёх GPU-пакетов сохранены.

Проверены все 4539 сетей шести пакетов: нулевые различия классификаций,
ансамблей и децилей относительно эталона. Str%: Encog ZZ1 93,878185;
Encog ZZ2 54,703325; TorchSharp ZZ1 105,122498; ZZ2 80,608806;
ZZ1_J 83,187146; ZZ2_J 103,074119.
Проверены 1819 GPU-сетей: история, децили, Str% и прогнозные сценарии
совпали с предыдущей версией. Проверки интерфейса и расчёта Profit в обоих
режимах прошли.

Полный Profit считается по следующему Open, с оценкой открытой позиции по
последнему Close и капиталом 3000. Начало — 2018-07-15. Это расчёт без затрат
полного движка исполнения Wealth-Lab; небольшие отличия его итогового Profit
не объявляются устранёнными. Подробная проверка находится в
research/ensemble_fix_20261102 и research/ensemble_parity_20261102.

## Состав выпуска

- `Breeze.dll` — Client Extension Market Breeze;
- `Breeze.Strategies.dll` — библиотека стратегий;
- `BreezeHelp.html` — встроенная справка;
- `BreezeRules.json` — исполняемый каталог правил;
- `BreezeEventCatalog.json` — полный каталог исследований;
- `Indicators.JohnEhlers_8Wealth.dll` — реплики индикаторов John Ehlers для
  Wealth-Lab 8, включая используемые классы с суффиксом `_rep`;
- `Проторговка Torch Sharp_Rules.xml` — импортируемая стратегия Wealth-Lab;
- `SHA256SUMS.txt` — контрольные суммы файлов выпуска.

Исходники расширения, всех исследовательских стратегий и проверочных проектов
находятся в корне репозитория GitHub. Описание — `docs/research-strategies.md`.

## Установка

При закрытом Wealth-Lab скопировать в
C:\Program Files\Quantacula, LLC\WealthLab 8:

- Breeze.dll — Client Extension;
- Breeze.Strategies.dll — библиотека стратегий;
- BreezeHelp.html — справка;
- BreezeRules.json — правила скоринга;
- BreezeEventCatalog.json — каталог исследований;
- Indicators.JohnEhlers_8Wealth.dll — реплики индикаторов John Ehlers.

SHA256SUMS.txt содержит контрольные суммы выпуска.
Пакеты нейросетей и рыночные данные не входят в поставку:
используются существующие каталоги D:\TorchSharp4GPU, D:\TorchSharp,
D:\model_ensemble_Encog и D:\fear_greed_close.
После замены DLL перезапустить Wealth-Lab и нажать Recalculate.
