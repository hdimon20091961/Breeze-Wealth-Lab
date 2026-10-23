using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;

namespace BreezeVL;

internal sealed record BreezeRuleActivation(
    string Mode,
    int? DurationBars,
    int[] DurationCandidates,
    string Retrigger,
    string Cancellation,
    string Status);

internal sealed record BreezeRuleEntry(
    int Number,
    string Id,
    string NameRu,
    string NameCode,
    string Kind,
    string ImplementedFormula,
    double SoftWeight,
    double StandardWeight,
    double HardWeight,
    bool TorchEnabled,
    double TorchWeightThresholdPp,
    string TorchWeightStatus,
    BreezeRuleActivation TorchActivation,
    string TorchNote,
    string Source,
    string SourceStart,
    string SourceEnd,
    string Meaning,
    string ProposedDefinition,
    string Lifecycle,
    string Unresolved);

internal sealed record BreezeRuleSet(
    string SchemaVersion,
    string CatalogVersion,
    string Status,
    string Source,
    IReadOnlyList<BreezeRuleEntry> Rules)
{
    public BreezeRuleEntry ById(string id) =>
        Rules.First(rule => string.Equals(rule.Id, id, StringComparison.Ordinal));

    public double[] BreezeWeights(int profile)
    {
        if (profile is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(profile));
        return Rules.Select(rule => profile switch
        {
            0 => rule.SoftWeight,
            1 => rule.StandardWeight,
            _ => rule.HardWeight
        }).ToArray();
    }
}

internal static class BreezeRuleCatalog
{
    public const string FileName = "BreezeRules.json";
    private static readonly Lazy<BreezeRuleSet> CurrentLazy = new(LoadCore);

    public static BreezeRuleSet Current => CurrentLazy.Value;

    private static BreezeRuleSet LoadCore()
    {
        Assembly assembly = typeof(BreezeRuleCatalog).Assembly;
        string assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        string externalPath = Path.Combine(assemblyDirectory, FileName);
        Stream stream;
        string source;

        if (File.Exists(externalPath))
        {
            stream = File.OpenRead(externalPath);
            source = externalPath;
        }
        else
        {
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(FileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                throw new FileNotFoundException(
                    $"{FileName} was not found beside Breeze.dll and no embedded fallback exists.",
                    externalPath);
            stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource {resourceName} cannot be opened.");
            source = $"embedded:{resourceName}";
        }

        using (stream)
        using (JsonDocument document = JsonDocument.Parse(stream))
        {
            JsonElement root = document.RootElement;
            string schemaVersion = RequiredString(root, "schema_version");
            string catalogVersion = RequiredString(root, "catalog_version");
            string status = RequiredString(root, "status");
            var rules = new List<BreezeRuleEntry>();
            foreach (JsonElement item in root.GetProperty("rules").EnumerateArray())
            {
                JsonElement names = item.GetProperty("names");
                JsonElement detector = item.GetProperty("detector");
                JsonElement breezeWeights = item.GetProperty("breeze").GetProperty("weights");
                JsonElement torch = item.GetProperty("torchsharp");
                JsonElement activation = torch.GetProperty("activation");
                JsonElement documentation = item.GetProperty("documentation");
                JsonElement sourceTime = documentation.GetProperty("source_time");
                rules.Add(new BreezeRuleEntry(
                    item.GetProperty("number").GetInt32(),
                    RequiredString(item, "id"),
                    RequiredString(names, "ru"),
                    RequiredString(names, "code"),
                    RequiredString(item.GetProperty("classification"), "kind"),
                    RequiredString(detector, "implemented_formula"),
                    breezeWeights.GetProperty("soft").GetDouble(),
                    breezeWeights.GetProperty("standard").GetDouble(),
                    breezeWeights.GetProperty("hard").GetDouble(),
                    torch.GetProperty("enabled").GetBoolean(),
                    torch.GetProperty("weight_threshold_pp").GetDouble(),
                    RequiredString(torch, "weight_status"),
                    new BreezeRuleActivation(
                        RequiredString(activation, "mode"),
                        NullableInt(activation.GetProperty("duration_bars")),
                        activation.GetProperty("duration_candidates").EnumerateArray()
                            .Select(value => value.GetInt32()).ToArray(),
                        RequiredString(activation, "retrigger"),
                        RequiredString(activation, "cancellation"),
                        RequiredString(activation, "status")),
                    RequiredString(torch, "note"),
                    RequiredString(documentation, "source"),
                    OptionalString(sourceTime, "start"),
                    OptionalString(sourceTime, "end"),
                    RequiredString(documentation, "meaning"),
                    RequiredString(documentation, "proposed_definition"),
                    RequiredString(documentation, "lifecycle"),
                    RequiredString(documentation, "unresolved")));
            }

            Validate(rules, source);
            return new BreezeRuleSet(schemaVersion, catalogVersion, status, source, rules);
        }
    }

    private static void Validate(IReadOnlyList<BreezeRuleEntry> rules, string source)
    {
        if (rules.Count != M.Features.Length)
            throw new InvalidDataException(
                $"{source}: {rules.Count} rules were loaded, but the compiled detector contains {M.Features.Length} features.");
        if (rules.Select(rule => rule.Number).Distinct().Count() != rules.Count ||
            rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new InvalidDataException($"{source}: rule numbers and IDs must be unique.");

        for (int index = 0; index < rules.Count; index++)
        {
            BreezeRuleEntry rule = rules[index];
            if (rule.Number != index + 1)
                throw new InvalidDataException($"{source}: expected permanent rule number {index + 1}, got {rule.Number}.");
            if (!string.Equals(rule.Id, M.Features[index].Id, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"{source}: rule {rule.Number} is {rule.Id}, but the compiled detector at the same position is {M.Features[index].Id}.");
            if (string.IsNullOrWhiteSpace(rule.ImplementedFormula))
                throw new InvalidDataException($"{source}: {rule.Id} has no implemented formula.");
            double[] numbers =
            [
                rule.SoftWeight, rule.StandardWeight, rule.HardWeight,
                rule.TorchWeightThresholdPp
            ];
            if (numbers.Any(number => !double.IsFinite(number)))
                throw new InvalidDataException($"{source}: {rule.Id} contains a non-finite weight.");
        }
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        string? value = parent.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Required text field '{property}' is empty in {FileName}.");
        return value;
    }

    private static string OptionalString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? NullableInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();

    public static string ActivationText(BreezeRuleEntry rule)
    {
        BreezeRuleActivation activation = rule.TorchActivation;
        if (string.Equals(activation.Status, "rejected_after_test", StringComparison.Ordinal))
            return "отклонено после теста";
        return activation.Mode switch
        {
            "current_bar_only" => "только бар события",
            "while_source_nonzero" => "пока исходное состояние ненулевое",
            "use_source_state" => "по существующему состоянию источника",
            "fixed_bars_to_calibrate" => activation.DurationBars.HasValue
                ? $"{activation.DurationBars.Value.ToString(CultureInfo.InvariantCulture)} торговых баров"
                : $"срок подбирается: {string.Join(", ", activation.DurationCandidates)} баров",
            _ => activation.Mode
        };
    }
}
