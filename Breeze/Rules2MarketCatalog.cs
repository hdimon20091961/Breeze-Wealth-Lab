using System.Text.Json;
using System.Globalization;
using BreezeRules2.Research;

namespace BreezeVL;

/// <summary>Exact, dated factors used by the frozen Rules2 score.</summary>
internal static class Rules2MarketCatalog
{
    private static readonly JsonElement EmptyJson = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement.Clone();
    internal static readonly BreezeMarketCatalogSet Current = Build();

    internal static IReadOnlyDictionary<string, double> Values(FeatureRow row) =>
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["H01"] = row.EarlyTurn, ["H02"] = row.Failure,
            ["H03"] = row.PullbackResume, ["H04"] = row.Resilience,
            ["H05"] = row.UnconfirmedStress, ["H06"] = row.LowBreadthNewLow,
            ["F01"] = row.BreadthVelocity, ["F02"] = row.EmaFanTrend,
            ["F03"] = row.RecoveryAsymmetry, ["F04"] = row.VolumeConfirmation,
            ["EMA"] = row.EmaContext
        };

    internal static string Describe(BreezeMarketEntry entry)
    {
        BreezeRuleEntry rule = entry.RuntimeRule
            ?? throw new InvalidOperationException($"Rules2 factor {entry.Id} has no runtime detector.");
        string weight = rule.StandardWeight.ToString("+0.##;-0.##;0", CultureInfo.GetCultureInfo("ru-RU"));
        return $"Вес Rules2: {weight}. Профили Soft, Standard и Hard меняют пороги входа/выхода, но не этот вес.\n" +
               $"• Фактическое условие\n{rule.ImplementedFormulaRu}\n" +
               $"• Смысл\n{rule.Meaning}\n" +
               $"• Доступность\nЗначение рассчитывается тем же замороженным движком Rules2, который формирует оценку и целевую позицию. " +
               "Нет значения означает отсутствие исходных данных, а не нулевое срабатывание.\n" +
               $"• Источник\n{rule.Source}";
    }

    private static BreezeMarketCatalogSet Build()
    {
        var definitions = new (string Id, string Name, string Family, double Weight, string Formula, string Meaning)[]
        {
            ("H01", "Ранний поворот при нижнем MyRSI", "H", 1,
                "Первый зелёный бар: MyRSI ≤ −0,9, тело ≥ 10% диапазона дня, растут не менее трёх из четырёх широт.",
                "Раннее восстановление цены и внутренней широты из нижнего насыщения."),
            ("H02", "Подтверждённый срыв раннего восстановления", "H", -4.5,
                "В течение 6 сессий после H01: закрытие ниже минимума якорного дня и не менее трёх широт не растут.",
                "Штраф за подтверждённую неудачу раннего разворота."),
            ("H03", "Возврат над EMA20 при удержанной EMA50", "H", 1,
                "Закрытие пересекает EMA20 вверх; 5 последних закрытий не ниже EMA50; EMA50 не падает; порядок EMA бычий.",
                "Возобновление роста после неглубокой коррекции."),
            ("H04", "Слабая быстрая широта при устойчивой долгой", "H", -2.5,
                "B50 ниже 67,5 и снизилась минимум на 7,5 п.п. за 11 сессий; B200 выше 57,5 и выросла минимум на 2,5 п.п. за 35 сессий.",
                "Расхождение краткосрочной и долгосрочной широты."),
            ("H05", "Ценовой стресс при сохраняющемся контанго VIX", "H", -.25,
                "Не менее 4 сессий: цена ниже EMA20, MyRSI ниже −0,2, ближайший спред VIX выше 0,5%, а рабочее состояние Contango New не ниже Contango. Ролловер обнуляет возраст.",
                "Стресс цены, который скорректированная кривая VIX ещё не подтверждает бэквордацией."),
            ("H06", "Новый минимум без ухудшения низкой широты", "H", 0,
                "Цена ниже минимума предыдущих 20 сессий; B100 ≤ 10 и не хуже уровня широты на прежнем минимуме с допуском одного участника.",
                "Исследовательский фактор с нулевым весом в принятой Rules2."),
            ("F01", "Согласованная скорость четырёх широт", "F", .5,
                "Скорости четырёх широт за 7 сессий согласованы минимум у трёх; средняя скорость и разброс проходят принятые пороги.",
                "Направление и согласованность движения внутренней широты."),
            ("F02", "Согласованные расстояния и наклоны EMA", "F", 1.75,
                "Три зазора и четыре наклона EMA за 4 сессии нормированы на ATRP; согласованы все семь компонентов.",
                "Сила и согласованность веера EMA."),
            ("F03", "Асимметрия падения и восстановления", "F", 0,
                "Пик и дно ищутся в 63 сессиях; падение не менее 2%, восстановление не старше 20 сессий.",
                "Исследовательский фактор с нулевым весом в принятой Rules2."),
            ("F04", "Относительный объём SPY и движение цены", "F", -1,
                "Объём к медиане предыдущих 10 сессий ≥ 1,5 и абсолютное движение цены ≥ 0,25 ATR; знак задаёт движение цены.",
                "Подтверждение движения повышенным объёмом; принятый вес имеет обратный знак."),
            ("EMA", "Контекст порядка четырёх EMA", "EMA", -1.5,
                "+1 при EMA20>EMA50>EMA100>EMA200; −1 при обратном порядке; 0 в смешанном состоянии.",
                "Отдельный контекст порядка средних, реально участвующий в формуле Rules2.")
        };
        var entries = new List<BreezeMarketEntry>(definitions.Length);
        for (int index = 0; index < definitions.Length; index++)
        {
            var d = definitions[index];
            var runtime = new BreezeRuleEntry(index + 1, d.Id, d.Name, d.Id, d.Family, "rules2",
                d.Formula, d.Formula, d.Weight, d.Weight, d.Weight, false, 0, "not_applicable",
                new BreezeRuleActivation("daily", null, [], "new_observation", "when_value_returns_to_zero", "rules2"),
                string.Empty, "Rules2 — замороженный исследовательский движок", "", "", d.Meaning,
                d.Formula, "Ежедневный расчёт по доступным исходным данным.", "")
            { DisplayDirection = d.Weight < 0 ? -1 : 1 };
            entries.Add(new BreezeMarketEntry(index + 1, d.Id, d.Name, d.Family, "rules2", d.Formula,
                true, d.Weight != 0, runtime, EmptyJson, EmptyArray, EmptyJson));
        }
        return new("Rules2 frozen", entries, entries.Count, entries.Count(item => item.AcceptedNonzero));
    }
}
