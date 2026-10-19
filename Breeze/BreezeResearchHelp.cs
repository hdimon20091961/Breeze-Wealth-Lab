using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;

namespace BreezeVL;

internal static class BreezeResearchHelp
{
    private sealed record CatalogEntry(
        string Name,
        string Meaning,
        string ProposedDefinition,
        string Lifecycle,
        string Unresolved);

    private sealed record Sensitivity(
        string Id,
        double BaseWeight,
        double LeftWeight,
        double LeftProfit,
        double LeftDelta,
        double RightWeight,
        double RightProfit,
        double RightDelta,
        double Importance);

    // Supplied Wealth-Lab alpha.13 one-factor report. Importance is defined as
    // max(abs(delta ProfitPct)) for the two tested Strong endpoints.
    private static readonly Sensitivity[] Sensitivities =
    [
        new("T09",-10,-20,163.335,-88.178,0,182.992,-68.522,88.178),
        new("B01",15,0,184.100,-67.413,30,210.292,-41.222,67.413),
        new("V01",5,0,184.537,-66.977,10,223.517,-27.997,66.977),
        new("T07",-10,-20,213.250,-38.263,0,188.702,-62.812,62.812),
        new("T05",5,0,197.347,-54.167,10,233.513,-18.000,54.167),
        new("T08",5,0,207.013,-44.500,10,215.668,-35.845,44.500),
        new("P03",-10,-20,210.490,-41.023,0,210.473,-41.040,41.040),
        new("Z02",5,0,217.637,-33.877,10,258.475,6.962,33.877),
        new("R01",10,0,242.793,-8.720,20,221.253,-30.260,30.260),
        new("P05",5,0,247.165,-4.348,10,221.483,-30.030,30.030),
        new("B02",20,0,226.503,-25.010,40,235.475,-16.038,25.010),
        new("B09",5,0,246.283,-5.230,10,233.802,-17.712,17.712),
        new("Z03",5,0,234.220,-17.293,10,247.410,-4.103,17.293),
        new("B08",10,0,236.130,-15.383,20,245.082,-6.432,15.383),
        new("V08",10,0,245.753,-5.760,20,237.460,-14.053,14.053),
        new("E06",10,0,246.257,-5.257,20,240.407,-11.107,11.107),
        new("V03",-5,-10,242.750,-8.763,0,241.873,-9.640,9.640),
        new("V07",-5,-10,243.090,-8.423,0,246.232,-5.282,8.423),
        new("I01",-5,-10,244.553,-6.960,0,253.490,1.977,6.960),
        new("P02",10,0,245.755,-5.758,20,244.857,-6.657,6.657),
        new("E05",10,0,245.560,-5.953,20,251.490,-0.023,5.953),
        new("C05",10,0,245.567,-5.947,20,251.513,0.000,5.947),
        new("V04",-5,-10,250.710,-0.803,0,245.987,-5.527,5.527),
        new("E01",-5,-10,247.317,-4.197,0,250.750,-0.763,4.197),
        new("E02",10,0,247.470,-4.043,20,249.243,-2.270,4.043),
        new("B03",5,0,249.657,-1.857,10,248.583,-2.930,2.930),
        new("R03",15,0,248.993,-2.520,30,250.857,-0.657,2.520),
        new("C08",-10,-20,251.233,-0.280,0,249.890,-1.623,1.623),
        new("B07",-10,-20,249.997,-1.517,0,252.250,0.737,1.517),
        new("T03",15,0,252.420,0.907,30,251.513,0.000,0.907),
        new("C01",10,0,250.707,-0.807,20,251.513,0.000,0.807),
        new("T06",10,0,251.513,0.000,20,251.513,0.000,0.000)
    ];

    public static string BuildHtml()
    {
        Dictionary<string, CatalogEntry> catalog = LoadCatalog();
        double[] soft = M.PresetWeights(0), standard = M.PresetWeights(1), strong = M.PresetWeights(2);
        var b = new StringBuilder(120_000);
        b.AppendLine("<section id=\"position-research\">");
        b.AppendLine("<h2>Позиционная событийная модель 0.10.0</h2>");
        b.AppendLine("<div class=\"note\"><b>Что перенесено из Wealth-Lab.</b> Один и тот же код ежедневно рассчитывает 74 наблюдаемых состояния после закрытия рынка. Score = clamp(50 + сумма(вес × значение события), 0, 100). Решение исполняется на следующем открытии. Итоговый исследованный движок пороговый: Score ≥ 50 означает 10/10 контрактов, Score &lt; 50 — вне рынка. Будущие данные не используются.</div>");
        b.AppendLine("<p>Три набора весов заморожены по пяти выборочным прогонам. Переключатель <b>Position model</b> на Fuzzy map меняет только набор весов; определения событий остаются одинаковыми.</p>");
        b.AppendLine("<table><thead><tr><th>Профиль</th><th>Назначение</th><th>ProfitPct</th><th>Max DD</th><th>Sharpe</th><th>Recovery</th><th>Позиций</th></tr></thead><tbody>");
        b.AppendLine("<tr><td><b>Soft</b></td><td>минимальная просадка при ProfitPct &gt; 240%</td><td>248,03%</td><td>−9,16%</td><td>1,620</td><td>10,343</td><td>1380</td></tr>");
        b.AppendLine("<tr><td><b>Standard</b></td><td>выбранная визуально сбалансированная equity</td><td>251,19%</td><td>−9,41%</td><td>1,607</td><td>10,872</td><td>1400</td></tr>");
        b.AppendLine("<tr><td><b>Strong</b></td><td>максимальная доходность выборочных прогонов</td><td>251,51%</td><td>−9,37%</td><td>1,611</td><td>10,886</td><td>1380</td></tr>");
        b.AppendLine("</tbody></table>");

        b.AppendLine("<h3>Чувствительность Strong, по убыванию влияния на ProfitPct</h3>");
        b.AppendLine("<p>База Strong = 251,513%. Для каждого ненулевого веса остальные веса фиксировались. Проверялись числовой левый и правый пределы последнего стресс-теста: 0 и 2× исходного веса. <b>Значимость</b> = max(|ΔProfitPct слева|, |ΔProfitPct справа|), в процентных пунктах. Это однофакторная чувствительность, а не доказательство причинности и не новая оптимизация.</p>");
        b.AppendLine("<table class=\"results\"><thead><tr><th>№</th><th>ID и состояние</th><th>Strong</th><th>Левый вес → ProfitPct (Δ)</th><th>Правый вес → ProfitPct (Δ)</th><th>Значимость, п.п.</th></tr></thead><tbody>");
        int rank = 0;
        foreach (Sensitivity s in Sensitivities.OrderByDescending(item => item.Importance).ThenBy(item => item.Id))
        {
            rank++;
            M.Feature feature = M.Features.Single(item => item.Id == s.Id);
            string name = catalog.TryGetValue(s.Id, out CatalogEntry? entry) ? entry.Name : feature.Name;
            b.Append("<tr><td>").Append(rank).Append("</td><td><b>").Append(H(s.Id)).Append("</b> ").Append(H(name))
                .Append("</td><td>").Append(N(s.BaseWeight)).Append("</td><td>")
                .Append(N(s.LeftWeight)).Append(" → ").Append(N(s.LeftProfit)).Append("% (").Append(Signed(s.LeftDelta)).Append(")</td><td>")
                .Append(N(s.RightWeight)).Append(" → ").Append(N(s.RightProfit)).Append("% (").Append(Signed(s.RightDelta)).Append(")</td><td><b>")
                .Append(N(s.Importance)).AppendLine("</b></td></tr>");
        }
        b.AppendLine("</tbody></table>");

        b.AppendLine("<h3>Полный каталог 74 состояний</h3>");
        b.AppendLine("<p>Вес 0 означает только то, что событие не влияет на текущий Score. Оно не удалено: состояние продолжает рассчитываться и остаётся доступным для проверки и будущего изменения определения. Колонка «Реализованная формула» описывает именно действующий код; «Исследовательское описание» сохраняет более широкую исходную идею.</p>");
        b.AppendLine("<table><thead><tr><th>ID</th><th>Состояние</th><th>Soft</th><th>Standard</th><th>Strong</th><th>Статус</th><th>Реализованная формула</th><th>Исследовательское описание</th></tr></thead><tbody>");
        for (int i = 0; i < M.Features.Length; i++)
        {
            M.Feature feature = M.Features[i];
            catalog.TryGetValue(feature.Id, out CatalogEntry? entry);
            bool zero = soft[i] == 0 && standard[i] == 0 && strong[i] == 0;
            string status = zero ? "<b>0 — сохранён</b>" : "активен";
            string description = entry is null
                ? string.Empty
                : $"<b>Смысл:</b> {H(entry.Meaning)}<br><b>Предварительное определение:</b> {H(entry.ProposedDefinition)}<br><b>Жизненный цикл:</b> {H(entry.Lifecycle)}<br><b>Требует уточнения:</b> {H(entry.Unresolved)}";
            b.Append("<tr").Append(zero ? " style=\"background:#f5f6f7;color:#4f5963\"" : string.Empty).Append("><td><b>")
                .Append(H(feature.Id)).Append("</b></td><td>").Append(H(entry?.Name ?? feature.Name))
                .Append("<br><span class=\"small\">").Append(H(feature.Name)).Append("</span></td><td>")
                .Append(N(soft[i])).Append("</td><td>").Append(N(standard[i])).Append("</td><td>")
                .Append(N(strong[i])).Append("</td><td>").Append(status).Append("</td><td>")
                .Append(H(feature.Rule)).Append("</td><td>").Append(description).AppendLine("</td></tr>");
        }
        b.AppendLine("</tbody></table>");
        b.AppendLine("<p class=\"small\">X01–X03 относятся к альтернативным способам исполнения лимитных заявок и намеренно не входят в 74 рыночных состояния текущего скоринга. Их нельзя оценивать как сегодняшнее событие, если исполнение стало известно позднее.</p>");
        b.AppendLine("</section>");
        return b.ToString();
    }

    private static Dictionary<string, CatalogEntry> LoadCatalog()
    {
        Assembly assembly = typeof(BreezeResearchHelp).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(item => item.EndsWith("EventCatalogSource.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return [];
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using JsonDocument document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (JsonElement item in document.RootElement.GetProperty("events").EnumerateArray())
        {
            string id = item[0].GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) continue;
            result[id] = new CatalogEntry(
                item[1].GetString() ?? string.Empty,
                item[6].GetString() ?? string.Empty,
                item[7].GetString() ?? string.Empty,
                item[8].GetString() ?? string.Empty,
                item[9].GetString() ?? string.Empty);
        }
        return result;
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');
    private static string Signed(double value) => value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture).Replace('.', ',');
}
