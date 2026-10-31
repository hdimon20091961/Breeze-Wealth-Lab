using System.Globalization;
using System.Text;

namespace BreezeVL;

internal static class MarketSetupDescription
{
    public static string Build(BreezeRuleEntry rule)
    {
        string sourceTime = string.IsNullOrWhiteSpace(rule.SourceStart)
            ? string.Empty : $" ({rule.SourceStart}–{rule.SourceEnd})";
        string activation = BreezeRuleCatalog.ActivationText(rule);
        string repeat = rule.TorchActivation.Retrigger switch
        {
            "restart_without_accumulation" => "новое событие запускает срок заново без накопления",
            "new_observation" => "каждое новое событие учитывается отдельно",
            "defined_by_source_Zweig_signal" => "по исходному сигналу Цвейга",
            _ => "не применяется"
        };
        string cancellation = rule.TorchActivation.Cancellation switch
        {
            "new_lower_saturation_R01" => "новое нижнее насыщение MyRSI (R01)",
            "when_Z02_returns_to_zero" => "плотность Цвейга (Z02) вернулась к нулю",
            _ => "исходное значение вернулось к нулю"
        };
        string torchStatus = rule.TorchWeightStatus == "rejected_after_test"
            ? "отклонено после проверки" : "ещё не откалибровано";
        string lifecycle = rule.Id == "T02"
            ? "Проверяется каждый торговый день. Зелёная область означает рост всех четырёх EMA; частичный рост отображается числом растущих средних под курсором."
            : HumanText(rule.Lifecycle);
        if (rule.Id == "U01")
            lifecycle += " На графике: синяя линия — первый выход, жёлтая область — повторное насыщение, красная линия — второй выход, выполнивший все условия U01. Первый выход выделяется как часть уже завершившейся последовательности.";
        string configuration = rule.Parameters is EmaStateParameters p
            ? "• Параметры\nШироты S&P SMA50 / SMA100 / SMA200 / Nasdaq SMA100: " +
              string.Join(" / ", p.Breadths.Select(v => Number(rule.DisplayDirection==1 ? v : 100-v,"0"))) +
              $"%; ATRP(1) ≤ {Number(p.AtrpMaxPercent,"0.##")}%; подтверждение {p.ConfirmationBars} торговых дней.\n" : string.Empty;
        return
            $"Soft: {Weight(rule.SoftWeight)}; Standard: {Weight(rule.StandardWeight)}; Hard: {Weight(rule.HardWeight)}.\n" +
            $"• Фактическое условие\n{rule.ImplementedFormulaRu}\n" +
            configuration +
            (rule.Number > 75 ? "• Статус детектора\nВключён: ежедневный расчёт, график, курсор и пояснение. Веса скоринга отдельно.\n" : string.Empty) +
            $"• Смысл\n{HumanText(rule.Meaning)}\n" +
            $"• Жизненный цикл\n{lifecycle}\n" +
            $"• Применение в TorchSharp\n{activation}; повтор: {repeat}; отмена: {cancellation}.\n" +
            $"• Статус TorchSharp\n{(rule.TorchEnabled ? "Включено" : "Выключено")}; порог: {Weight(rule.TorchWeightThresholdPp)} п.п.; {torchStatus}.\n" +
            $"• Исследовательские вопросы\n{HumanText(rule.Unresolved)}\n" +
            $"• Источник\n{rule.Source}{sourceTime}";
    }

    public static string AtCursor(BreezeRuleEntry rule, MarketSetupPoint? point)
    {
        if (point is null) return "• На выбранную дату\nРасчёт правила пока недоступен.\n";
        var text = new StringBuilder($"• На дату {point.Date:yyyy-MM-dd}\n");
        double value = point.Value(rule.Id);
        text.AppendLine($"Значение правила: {Number(value)}.");
        return text.ToString();
    }

    private static string Number(double value, string format = "0.000") =>
        double.IsFinite(value) ? value.ToString(format, CultureInfo.GetCultureInfo("ru-RU")) : "нет данных";
    private static string Weight(double value) => Number(value, "+0.##;-0.##;0");

    private static string HumanText(string value) => value
        .Replace("all-time high", "исторический максимум", StringComparison.Ordinal)
        .Replace("close-to-close", "от закрытия до закрытия", StringComparison.Ordinal)
        .Replace("Close>Open", "закрытие выше открытия", StringComparison.Ordinal)
        .Replace("High", "максимум дня", StringComparison.Ordinal)
        .Replace("Close", "закрытие", StringComparison.Ordinal)
        .Replace("Open", "открытие", StringComparison.Ordinal)
        .Replace("unknown", "нет данных", StringComparison.Ordinal)
        .Replace("spot", "текущий индекс VIX", StringComparison.Ordinal)
        .Replace("CurveScore", "оценка кривой", StringComparison.Ordinal)
        .Replace("walk-forward", "последовательная вневыборочная", StringComparison.Ordinal)
        .Replace("DTE", "дни до экспирации", StringComparison.Ordinal)
        .Replace("eps", "допуск насыщения 0,001", StringComparison.Ordinal)
        .Replace("RSI/AGC", "MyRSI_rep и AGC", StringComparison.Ordinal)
        .Replace("RSI", "MyRSI", StringComparison.Ordinal)
        .Replace("MyMyRSI", "MyRSI", StringComparison.Ordinal)
        .Replace("B200", "широта SMA200", StringComparison.Ordinal)
        .Replace("B50", "широта SMA50", StringComparison.Ordinal)
        .Replace("BarsSinceSignal", "число баров после сигнала", StringComparison.Ordinal)
        .Replace("Density", "плотность", StringComparison.Ordinal)
        .Replace("RunLength", "длина серии", StringComparison.Ordinal)
        .Replace("Persistent", "устойчивость сигнала", StringComparison.Ordinal);
}
