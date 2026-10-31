using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BreezeVL;

internal sealed record BreezeMarketEntry(
    int Number, string Id, string NameRu, string Family, string Kind,
    string Definition, bool AcceptedSelected, bool AcceptedNonzero,
    BreezeRuleEntry? RuntimeRule, JsonElement ResearchRecord, JsonElement References,
    JsonElement AcceptedMembership)
{
    public bool IsActiveInBreeze => RuntimeRule?.IsActiveInBreeze == true;
}

internal sealed record BreezeMarketCatalogSet(
    string Version, IReadOnlyList<BreezeMarketEntry> Entries, int AcceptedSelected, int AcceptedNonzero)
{
    public int RuntimeCount => Entries.Count(entry => entry.RuntimeRule is not null);
    public int RuntimeActive => Entries.Count(entry => entry.IsActiveInBreeze);
}

internal static class BreezeMarketCatalog
{
    public const string FileName = "BreezeEventCatalog.json";
    private static readonly Lazy<BreezeMarketCatalogSet> CurrentValue = new(Load);
    public static BreezeMarketCatalogSet Current => CurrentValue.Value;

    private static BreezeMarketCatalogSet Load()
    {
        var assembly = typeof(BreezeMarketCatalog).Assembly;
        string external = Path.Combine(Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory, FileName);
        using Stream stream = File.Exists(external) ? File.OpenRead(external) :
            assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith(FileName, StringComparison.Ordinal)))
            ?? throw new FileNotFoundException(FileName);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported market catalogue schema.");
        var runtime = BreezeRuleCatalog.Current.AllRules.ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        var entries = new List<BreezeMarketEntry>();
        foreach (JsonElement item in root.GetProperty("entries").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            runtime.TryGetValue(id, out BreezeRuleEntry? rule);
            bool claimedRuntime = item.GetProperty("runtime_available").GetBoolean();
            if (claimedRuntime != (rule is not null))
                throw new InvalidDataException($"Market catalogue/runtime mismatch for {id}.");
            int number = item.GetProperty("number").GetInt32();
            if (rule is not null && rule.Number != number)
                throw new InvalidDataException($"Permanent rule number changed for {id}.");
            entries.Add(new BreezeMarketEntry(number, id, item.GetProperty("name").GetString()!,
                item.GetProperty("family").GetString()!, item.GetProperty("kind").GetString()!,
                item.GetProperty("definition").GetString()!, item.GetProperty("accepted_selected").GetBoolean(),
                item.GetProperty("accepted_nonzero").GetBoolean(), rule, item.GetProperty("research_record").Clone(),
                item.GetProperty("references").Clone(), item.GetProperty("accepted_membership").Clone()));
        }
        JsonElement counts = root.GetProperty("counts");
        if (entries.Count != counts.GetProperty("catalogue").GetInt32() ||
            runtime.Count != counts.GetProperty("runtime").GetInt32() ||
            entries.Count(entry => entry.IsActiveInBreeze) != counts.GetProperty("runtime_active").GetInt32() ||
            entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count ||
            entries.Select(entry => entry.Number).Distinct().Count() != entries.Count ||
            runtime.Keys.Except(entries.Select(entry => entry.Id), StringComparer.Ordinal).Any() ||
            entries.Count(entry => entry.AcceptedSelected) != counts.GetProperty("accepted_selected").GetInt32() ||
            entries.Count(entry => entry.AcceptedNonzero) != counts.GetProperty("accepted_nonzero").GetInt32())
            throw new InvalidDataException("Market catalogue count/identity validation failed.");
        return new(root.GetProperty("catalog_version").GetString()!, entries,
            counts.GetProperty("accepted_selected").GetInt32(), counts.GetProperty("accepted_nonzero").GetInt32());
    }

    public static string Describe(BreezeMarketEntry entry)
    {
        if (entry.RuntimeRule is BreezeRuleEntry runtime)
            return MarketSetupDescription.Build(runtime) + "\n• Реестр\nДатированный детектор включён в DLL; полный каталог " + Current.Version + "." +
                (runtime.Number > 75 ? "\nНовый расчётный слой; исходные 75 весовых признаков сохранены. Результаты HLC3 относятся только к последующему движению цены.\nИсследование: research/artifacts/ema_t01_t04_experiment_v1/RESULTS.md" : "");
        var text = new StringBuilder();
        text.AppendLine("• Статус");
        string catalogueStatus = entry.ResearchRecord.TryGetProperty("catalogue_status", out JsonElement status)
            ? status.GetString() ?? string.Empty
            : string.Empty;
        text.AppendLine(catalogueStatus == "implemented_contango_classifier"
            ? "Реализовано в едином расчёте контанго New. В Market Setup это объясняющая запись: отдельный датированный ряд события ещё не экспортируется."
            : entry.AcceptedNonzero
                ? "Принято в исследовательскую расчётную модель. В рабочей DLL этот детектор и его добавка ещё не подключены."
                : "Сохранено в общем исследовательском каталоге. В рабочем скоринге DLL не участвует.");
        text.AppendLine("Веса Soft / Standard / Hard для этой исследовательской записи не назначены в DLL.");
        text.AppendLine("• Определение");
        text.AppendLine(entry.Definition);
        if (entry.AcceptedMembership.TryGetProperty("current_weights", out JsonElement weights))
            text.AppendLine("Принятые исследовательские веса по состояниям: " + weights.GetRawText() +
                            ". Они применяются по сохранённой годовой таблице и не являются тремя профилями Breeze.");
        JsonElement record = entry.ResearchRecord;
        foreach (string key in new[] { "test_result", "event_statistics", "primary_path_statistics" })
        {
            if (!record.TryGetProperty(key, out JsonElement values) || values.ValueKind != JsonValueKind.Object) continue;
            text.AppendLine("• Сохранённые результаты");
            AppendMetrics(text, values);
        }
        foreach (string key in new[] { "test_results", "followup_context_results", "all_smoothing_results" })
        {
            if (!record.TryGetProperty(key, out JsonElement results) || results.ValueKind != JsonValueKind.Array) continue;
            text.AppendLine("• Проверенные варианты (включая отрицательные результаты)");
            foreach (JsonElement result in results.EnumerateArray())
                if (result.ValueKind == JsonValueKind.Object) AppendMetrics(text, result);
        }
        text.AppendLine("• Материалы и протоколы");
        foreach (JsonElement link in entry.References.EnumerateArray())
        {
            string path = link.GetProperty("path").GetString()!;
            if (!new[] { ".md", ".json", ".csv", ".py" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            text.AppendLine(path + (link.GetProperty("exists_at_build").GetBoolean() ? string.Empty : " [ссылка требует проверки]"));
        }
        text.AppendLine(catalogueStatus == "implemented_contango_classifier"
            ? "Отдельное датированное значение для дерева Market Setup отсутствует; результат входит в общее состояние Contango New и его пояснение."
            : "Датированное значение в DLL отсутствует; это не нулевое срабатывание и не отрицательный результат исследования.");
        return text.ToString();
    }

    private static void AppendMetrics(StringBuilder text, JsonElement values)
    {
        (string Key, string Label)[] fields =
        [
            ("name", "Вариант"), ("variant", "Вариант"), ("event_starts_full", "Срабатывания за всю историю"),
            ("event_starts_test", "Срабатывания в тесте"), ("event_starts", "Срабатывания"),
            ("active_days_full", "Активные дни"), ("completed", "Завершённые эпизоды"),
            ("censored", "Незавершённые эпизоды"), ("profit", "Прибыль, $"),
            ("delta_profit", "Изменение прибыли, $"), ("profit_improvement_pct", "Изменение прибыли, %"),
            ("max_dd_pct", "Максимальная просадка, %"), ("mean_return_pct", "Средняя доходность, %"),
            ("worst_terminal_return_pct", "Худший итог, %"), ("mean_mae_pct", "Средний MAE, %"),
            ("worst_mae_pct", "Худший MAE, %"), ("changed_holding_days", "Изменённые дни позиции")
        ];
        var parts = new List<string>();
        foreach ((string key, string label) in fields)
            if (values.TryGetProperty(key, out JsonElement value) && value.ValueKind is not JsonValueKind.Null)
            {
                string raw = value.ToString();
                string formatted = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    ? number.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) : raw;
                parts.Add(label + ": " + formatted);
            }
        text.AppendLine(string.Join("; ", parts));
    }
}
