using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WealthLab.AdvancedSmoothers;
using WealthLab.Backtest;
using WealthLab.Core;
using WealthLab.Indicators;
using WealthLab.TASC;

namespace BreezeVL;

// Direct, read-only inference over the existing Jordan/Elman trading packages.
// The formulas and JBIN reader mirror "GPU проторговка"; no derived CSV is created.
internal sealed class TradingEnsembleInference : UserStrategyBase
{
    private static readonly string[] FeatureNames =
    [
        "model_cnn_fear_greed", "risk_measure", "model_panic_euphoria", "myRSI_rep",
        "trendScore", "aroonUp", "aroonDown", "dIMinus", "dIPlus",
        "correlationAngle_rep", "correlationTrend", "hurst", "cmf", "decibel",
        "jocker", "marketBreadth"
    ];

    private readonly Dictionary<string, FeatureSet> _featureCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, double>[][]> _forecastFeatureCache =
        new(StringComparer.Ordinal);

    public override void Initialize(BarHistory bars) { }
    public override void Execute(BarHistory bars, int idx) { }

    public static IReadOnlyDictionary<string, TradingEnsembleSeries> Calculate(
        BarHistory bars,
        string workingDirectory,
        int forecastDays,
        IProgress<BreadthProgress>? progress,
        CancellationToken cancellationToken)
    {
        var engine = new TradingEnsembleInference();
        var result = new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
        for (int method = 1; method <= 3; method++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string package = $"ZZ{method}";
            progress?.Report(new BreadthProgress($"Торговые ансамбли: расчёт {package} из готового пакета …"));
            result[package] = engine.CalculatePackage(
                bars,
                Path.Combine(workingDirectory, $"trading_package_Jordan_ZZ{method}"),
                package,
                method,
                Math.Max(0, forecastDays),
                cancellationToken);
        }
        return result;
    }

    private TradingEnsembleSeries CalculatePackage(
        BarHistory bars, string packagePath, string packageName, int method, int forecastDays,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(packagePath))
            throw new DirectoryNotFoundException($"Торговый пакет {packageName} не найден: {packagePath}");

        string[] modelFiles = Directory.GetFiles(packagePath, "*.jbin");
        Array.Sort(modelFiles, StringComparer.OrdinalIgnoreCase);
        if (modelFiles.Length == 0)
            throw new InvalidOperationException($"В {packagePath} нет JBIN-моделей.");

        var models = new List<ModelForecast>(modelFiles.Length);
        int firstForecastOrigin = forecastDays > 0
            ? Math.Max(0, bars.Count - forecastDays - 1)
            : bars.Count;
        double[,]? forecastCounts = forecastDays > 0
            ? new double[bars.Count - firstForecastOrigin, 5]
            : null;
        foreach (string modelFile in modelFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelParameters? parameters = LoadParameters(Path.ChangeExtension(modelFile, ".ini"));
            if (parameters is null || parameters.CalculationMethod != method) continue;
            FeatureSet features = GetFeatures(bars, parameters);
            Dictionary<string, double>[][]? forecastFeatures = forecastDays > 0
                ? GetForecastFeatures(bars, parameters, features, forecastDays)
                : null;
            double[] forecast = RunModel(
                modelFile, bars.Count, features, parameters, forecastFeatures,
                firstForecastOrigin, forecastCounts, cancellationToken);
            double bench = ParseBench(Path.GetFileName(modelFile));
            models.Add(new ModelForecast(Path.GetFileName(modelFile), bench, forecast));
        }
        if (models.Count == 0)
            throw new InvalidOperationException($"В {packageName} нет комплектных моделей метода {method}.");

        double[] ensemble = new double[bars.Count];
        foreach (ModelForecast model in models)
            for (int bar = 0; bar < bars.Count; bar++) ensemble[bar] += model.Values[bar];

        List<ModelForecast> sorted = models
            .OrderBy(model => model.Bench)
            .ThenBy(model => model.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deciles = Enumerable.Range(0, 10).Select(_ => new double[bars.Count]).ToArray();
        var counts = new int[10];
        for (int modelIndex = 0; modelIndex < sorted.Count; modelIndex++)
        {
            int group = Math.Min(9, (int)(modelIndex * 10.0 / sorted.Count));
            counts[group]++;
            for (int bar = 0; bar < bars.Count; bar++) deciles[group][bar] += sorted[modelIndex].Values[bar];
        }

        var points = new List<TradingEnsemblePoint>(bars.Count);
        for (int bar = 0; bar < bars.Count; bar++)
        {
            var values = new double[10];
            for (int group = 0; group < 10; group++)
                values[group] = counts[group] == 0 ? 0.0 : 100.0 * deciles[group][bar] / counts[group];
            points.Add(new TradingEnsemblePoint(
                bars.DateTimes[bar].Date,
                100.0 * ensemble[bar] / models.Count,
                values));
        }
        var forecasts = new List<TradingForecastPoint>();
        if (forecastCounts is not null)
        {
            for (int origin = firstForecastOrigin; origin < bars.Count; origin++)
            {
                var scenarioPercentages = new double[5];
                int row = origin - firstForecastOrigin;
                for (int scenario = 0; scenario < scenarioPercentages.Length; scenario++)
                    scenarioPercentages[scenario] = 100.0 * forecastCounts[row, scenario] / models.Count;
                bool isNextBar = origin == bars.Count - 1;
                DateTime displayDate = isNextBar
                    ? NextTradingDate(bars.DateTimes[origin].Date)
                    : bars.DateTimes[origin + 1].Date;
                forecasts.Add(new TradingForecastPoint(displayDate, scenarioPercentages, isNextBar));
            }
        }
        return new TradingEnsembleSeries(packageName, points, forecasts);
    }

    private FeatureSet GetFeatures(BarHistory bars, ModelParameters p)
    {
        string key = string.Join('|', p.InputGradations, p.ValidationWithoutGradation,
            p.X4, p.X4Smooth, p.X6, p.X6Down, p.X7, p.X7Plus, p.X8, p.X9,
            p.X10, p.X11, p.X12, p.X13, p.X13HiLoPeriod, p.X13Mode, p.X14, p.X14MaPeriod);
        if (_featureCache.TryGetValue(key, out FeatureSet? cached)) return cached;

        var map = new Dictionary<string, TimeSeries>(StringComparer.OrdinalIgnoreCase);
        var starts = new List<int>();
        BarHistory fear = GetHistory(bars, "model_cnn_fear_greed");
        BarHistory risk = GetHistory(bars, "risk_measure");
        BarHistory panic = GetHistory(bars, "model_panic_euphoria_day");
        map["model_cnn_fear_greed"] = (SMA.Series(fear.Close, 1) - 50) / 50;
        map["risk_measure"] = (SMA.Series(risk.Close, 1) - 50) / 50;
        map["model_panic_euphoria"] = SMA.Series(panic.Close, 1) - 0.5;

        var myRsi = new MyRSI_rep(bars.Close, p.X4, p.X4Smooth); starts.Add(myRsi.FirstValidIndex); map["myRSI_rep"] = myRsi;
        var trend = new TrendScore(bars); starts.Add(trend.FirstValidIndex); map["trendScore"] = trend * 0.1;
        var aroonUp = new AroonUp(bars.Close, p.X6); starts.Add(aroonUp.FirstValidIndex); map["aroonUp"] = (-50 + aroonUp) / 50;
        var aroonDown = new AroonDown(bars.Close, p.X6Down); starts.Add(aroonDown.FirstValidIndex); map["aroonDown"] = (-50 + aroonDown) / 50;
        map["dIMinus"] = Agc((-30 + new DIMinus(bars, p.X7)) / 30);
        map["dIPlus"] = Agc((-25 + new DIPlus(bars, p.X7Plus)) / 30);
        var angle = new CorrelationAngle_rep(bars.Close, p.X8); starts.Add(angle.FirstValidIndex); map["correlationAngle_rep"] = angle / 180;
        var correlation = new CorrelationTrend(bars.Close, p.X9); starts.Add(correlation.FirstValidIndex); map["correlationTrend"] = Agc(correlation, correlation.FirstValidIndex);
        var hurst = new EhlersInverseFisherTransform_gpt(new Hurst(bars.Close, p.X10), 3, 1); map["hurst"] = 7 * (hurst - 0.6);
        var cmf = new EhlersInverseFisherTransform_gpt(new CMF(bars, p.X11), 3, 10); starts.Add(cmf.FirstValidIndex); map["cmf"] = cmf - 0.1;
        var decibel = new EhlersInverseFisherTransform_gpt(new Decibel(bars.Close, p.X12), 8, 0.1); starts.Add(decibel.FirstValidIndex); map["decibel"] = decibel + 0.55;
        map["jocker"] = BuildJocker(bars, p);
        map["marketBreadth"] = BuildMarketBreadth(bars, p);

        if (p.InputGradations > 0)
            foreach (string name in FeatureNames)
                map[name] = Quantize(bars, map[name], p.InputGradations, p.ValidationWithoutGradation);

        cached = new FeatureSet(map, Math.Max(175, starts.DefaultIfEmpty(175).Max()));
        _featureCache[key] = cached;
        return cached;
    }

    private Dictionary<string, double>[][] GetForecastFeatures(
        BarHistory bars, ModelParameters p, FeatureSet features, int forecastDays)
    {
        string key = string.Join('|', p.InputGradations, p.ValidationWithoutGradation,
            p.X4, p.X4Smooth, p.X6, p.X6Down, p.X7, p.X7Plus, p.X8, p.X9,
            p.X10, p.X11, p.X12, p.X13, p.X13HiLoPeriod, p.X13Mode,
            p.X14, p.X14MaPeriod, forecastDays, bars.Count);
        if (_forecastFeatureCache.TryGetValue(key, out Dictionary<string, double>[][]? cached))
            return cached;

        cached = new Dictionary<string, double>[bars.Count][];
        int firstOrigin = Math.Max(0, bars.Count - forecastDays - 1);
        double[] factors = [0.98, 0.99, 1.00, 1.01, 1.02];
        for (int origin = firstOrigin; origin < bars.Count; origin++)
        {
            cached[origin] = new Dictionary<string, double>[factors.Length];
            for (int scenario = 0; scenario < factors.Length; scenario++)
                cached[origin][scenario] = BuildForecastFeaturesAtBar(
                    bars, origin, factors[scenario], p, features);
        }
        _forecastFeatureCache[key] = cached;
        return cached;
    }

    private static Dictionary<string, double> BuildForecastFeaturesAtBar(
        BarHistory bars, int lastRealBar, double priceFactor,
        ModelParameters p, FeatureSet features)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var synthetic = new BarHistory(bars);
        for (int bar = 0; bar <= lastRealBar; bar++)
            synthetic.Add(bars.DateTimes[bar], bars.Open[bar], bars.High[bar],
                bars.Low[bar], bars.Close[bar], bars.Volume[bar]);

        DateTime futureDate = lastRealBar + 1 < bars.Count
            ? bars.DateTimes[lastRealBar + 1]
            : NextTradingDate(bars.DateTimes[lastRealBar].Date);
        double price = bars.Close[lastRealBar] * priceFactor;
        synthetic.Add(futureDate, price, price, price, price, bars.Volume[lastRealBar]);
        int futureBar = synthetic.Count - 1;

        values["model_cnn_fear_greed"] = features.Map["model_cnn_fear_greed"][lastRealBar];
        values["risk_measure"] = features.Map["risk_measure"][lastRealBar];
        values["model_panic_euphoria"] = features.Map["model_panic_euphoria"][lastRealBar];
        values["jocker"] = features.Map["jocker"][lastRealBar];
        values["marketBreadth"] = features.Map["marketBreadth"][lastRealBar];

        values["myRSI_rep"] = QuantizeForecastValue(
            new MyRSI_rep(synthetic.Close, p.X4, p.X4Smooth)[futureBar], p, futureDate);
        values["trendScore"] = QuantizeForecastValue(new TrendScore(synthetic)[futureBar] * 0.1, p, futureDate);
        values["aroonUp"] = QuantizeForecastValue(
            (-50.0 + new AroonUp(synthetic.Close, p.X6)[futureBar]) / 50.0, p, futureDate);
        values["aroonDown"] = QuantizeForecastValue(
            (-50.0 + new AroonDown(synthetic.Close, p.X6Down)[futureBar]) / 50.0, p, futureDate);
        values["dIMinus"] = QuantizeForecastValue(
            Agc((-30.0 + new DIMinus(synthetic, p.X7)) / 30.0)[futureBar], p, futureDate);
        values["dIPlus"] = QuantizeForecastValue(
            Agc((-25.0 + new DIPlus(synthetic, p.X7Plus)) / 30.0)[futureBar], p, futureDate);
        values["correlationAngle_rep"] = QuantizeForecastValue(
            new CorrelationAngle_rep(synthetic.Close, p.X8)[futureBar] / 180.0, p, futureDate);
        var correlation = new CorrelationTrend(synthetic.Close, p.X9);
        values["correlationTrend"] = QuantizeForecastValue(
            Agc(correlation, correlation.FirstValidIndex)[futureBar], p, futureDate);
        var hurst = new EhlersInverseFisherTransform_gpt(new Hurst(synthetic.Close, p.X10), 3, 1);
        values["hurst"] = QuantizeForecastValue(7.0 * (hurst[futureBar] - 0.6), p, futureDate);
        var cmf = new EhlersInverseFisherTransform_gpt(new CMF(synthetic, p.X11), 3, 10);
        values["cmf"] = QuantizeForecastValue(cmf[futureBar] - 0.1, p, futureDate);
        var decibel = new EhlersInverseFisherTransform_gpt(new Decibel(synthetic.Close, p.X12), 8, 0.1);
        values["decibel"] = QuantizeForecastValue(decibel[futureBar] + 0.55, p, futureDate);
        return values;
    }

    private static double QuantizeForecastValue(double value, ModelParameters p, DateTime date)
    {
        if (p.InputGradations <= 0)
            return value;
        bool training = (date >= new DateTime(2020, 2, 27) && date <= new DateTime(2022, 10, 18)) ||
                        (date >= new DateTime(2026, 2, 1) && date <= new DateTime(2026, 7, 21));
        if (p.ValidationWithoutGradation == 1 && !training)
            return value;
        if (double.IsNaN(value))
            return double.NaN;
        value = double.IsPositiveInfinity(value) ? 1.0 :
            double.IsNegativeInfinity(value) ? -1.0 : Math.Clamp(value, -1.0, 1.0);
        int levels = p.InputGradations;
        if (levels == 1)
            return 0.0;
        int half = levels / 2;
        int bin = Math.Min(levels - 1, (int)Math.Floor((value + 1.0) * levels / 2.0));
        return (levels & 1) == 0
            ? (bin < half ? bin - half : bin - half + 1) / (double)half
            : (bin - half) / (double)half;
    }

    private static DateTime NextTradingDate(DateTime date)
    {
        DateTime next = date.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            next = next.AddDays(1);
        return next;
    }

    private TimeSeries BuildJocker(BarHistory bars, ModelParameters p)
    {
        BarHistory highs = GetHistory(bars, "$NASDAQ_NEWHI");
        BarHistory lows = GetHistory(bars, "$NASDAQ_NEWLO");
        var raw = new TimeSeries(bars.DateTimes, 0.0);
        for (int bar = 0; bar < bars.Count; bar++)
        {
            double sumHigh = 0, sumLow = 0;
            for (int offset = 0; offset < p.X13HiLoPeriod && offset <= bar; offset++)
            { sumHigh += highs.Close[bar - offset]; sumLow += lows.Close[bar - offset]; }
            double sum = sumHigh + sumLow;
            raw[bar] = sum <= 0 ? 0 : p.X13Mode == 0
                ? (sumHigh - sumLow) / sum
                : Math.Clamp(4.0 * (Math.Sqrt(Math.Max(0, sumHigh * sumLow)) / sum - 0.25), -1, 1);
        }
        return EMA.Series(raw, p.X13);
    }

    private TimeSeries BuildMarketBreadth(BarHistory bars, ModelParameters p)
    {
        string[] symbols = ["SPY", "XLV", "XLU", "XLE", "XLP"];
        var inputs = new List<(TimeSeries Close, TimeSeries Vwma)>();
        foreach (string symbol in symbols)
        {
            try
            {
                BarHistory item = symbol == "SPY" ? bars : GetHistory(bars, symbol);
                if (item is { Count: > 0 })
                    inputs.Add((item.Close, new VWMA(item.Close, p.X14MaPeriod, item)));
            }
            catch { }
        }
        var raw = new TimeSeries(bars.DateTimes, 50.0);
        for (int bar = 0; bar < bars.Count; bar++)
        {
            int up = 0, valid = 0;
            foreach ((TimeSeries close, TimeSeries vwma) in inputs)
                if (bar < close.Count && bar < vwma.Count && double.IsFinite(close[bar]) && double.IsFinite(vwma[bar]))
                { valid++; if (close[bar] > vwma[bar]) up++; }
            if (valid > 0) raw[bar] = 100.0 * up / valid;
        }
        return 0.01 * (EMA.Series(raw, p.X14) - 50.0);
    }

    private static TimeSeries Agc(TimeSeries source)
        => Agc(source, 0);

    private static TimeSeries Agc(TimeSeries source, int startIndex)
    {
        var result = new TimeSeries(source.DateTimes, true); double peak = 0.0000001;
        for (int bar = Math.Max(0, startIndex); bar < source.Count; bar++)
        {
            if (double.IsNaN(source[bar])) { result[bar] = 0; continue; }
            peak *= 0.991; peak = Math.Max(peak, Math.Abs(source[bar])); result[bar] = source[bar] / peak;
        }
        return result;
    }

    private static TimeSeries Quantize(BarHistory bars, TimeSeries source, int levels, int validationRaw)
    {
        var result = new TimeSeries(bars.DateTimes, double.NaN); int half = levels / 2;
        for (int bar = 0; bar < source.Count; bar++)
        {
            double value = source[bar];
            if (!double.IsFinite(value)) { result[bar] = double.IsNaN(value) ? double.NaN : Math.Sign(value); continue; }
            DateTime date = bars.DateTimes[bar];
            bool training = (date >= new DateTime(2020,2,27) && date <= new DateTime(2022,10,18)) ||
                            (date >= new DateTime(2026,2,1) && date <= new DateTime(2026,7,21));
            if (validationRaw == 1 && !training) { result[bar] = value; continue; }
            value = Math.Clamp(value, -1, 1);
            if (levels == 1) { result[bar] = 0; continue; }
            int bin = Math.Min(levels - 1, (int)Math.Floor((value + 1) * levels / 2.0));
            result[bar] = (levels & 1) == 0
                ? (bin < half ? bin - half : bin - half + 1) / (double)half
                : (bin - half) / (double)half;
        }
        return result;
    }

    private static double[] RunModel(
        string path, int count, FeatureSet features, ModelParameters p,
        Dictionary<string, double>[][]? forecastFeatures, int firstForecastOrigin,
        double[,]? forecastCounts, CancellationToken token)
    {
        JbinModel model = JbinModel.Load(path);
        if (model.InputSize != FeatureNames.Length) throw new InvalidDataException($"{Path.GetFileName(path)}: ожидается {FeatureNames.Length} входов.");
        if (p.NetworkType is >= 1 and <= 4 && model.Architecture != p.NetworkType) throw new InvalidDataException($"{Path.GetFileName(path)}: архитектура JBIN не совпадает с INI.");
        var result = new double[count]; var input = new float[FeatureNames.Length];
        for (int bar = features.StartIndex; bar < count; bar++)
        {
            if ((bar & 63) == 0) token.ThrowIfCancellationRequested();
            for (int feature = 0; feature < input.Length; feature++)
            { double value = features.Map[FeatureNames[feature]][bar]; input[feature] = double.IsFinite(value) ? (float)value : 0f; }
            result[bar] = model.Forward(input) >= p.Level ? 1.0 : 0.0;
            if (forecastFeatures is null || forecastCounts is null ||
                bar < firstForecastOrigin || forecastFeatures[bar] is null)
                continue;

            (float[] hidden, float output) state = model.GetState();
            for (int scenario = 0; scenario < 5; scenario++)
            {
                model.SetState(state.hidden, state.output);
                Dictionary<string, double> scenarioFeatures = forecastFeatures[bar][scenario];
                for (int feature = 0; feature < input.Length; feature++)
                {
                    double value = scenarioFeatures.TryGetValue(FeatureNames[feature], out double found) ? found : 0.0;
                    input[feature] = double.IsFinite(value) ? (float)value : 0f;
                }
                if (model.Forward(input) >= p.Level)
                    forecastCounts[bar - firstForecastOrigin, scenario] += 1.0;
            }
            model.SetState(state.hidden, state.output);
        }
        return result;
    }

    private static ModelParameters? LoadParameters(string path)
    {
        if (!File.Exists(path)) return null;
        var ini = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim(); int equals = line.IndexOf('=');
            if (equals > 0 && !line.StartsWith('[')) ini[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        string Get(string key, string fallback) => ini.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
        int Int(string key, int fallback) => int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        double Double(string key, double fallback) => double.TryParse(Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
        int x6 = Int("Param_X6", 40), x7 = Int("Param_X7", 7);
        int x13Mode = Int("Param_X13/2", 0);
        if (x13Mode is < 0 or > 1)
            throw new InvalidDataException($"Модель {Path.GetFileName(path)} использует удалённый режим X13/2={x13Mode}.");

        return new ModelParameters(
            Int("network_type", 1), Int("context_mode", Int("jordan_mode", 1)), Double("level_class", .5),
            Int("CalculationMethod", 1), Int("InputGradations", 0), Int("ValidationWithoutGradation", 0),
            Int("Param_X4", 14), Int("Param_X4/1", 8), x6, Int("Param_X6/1", x6),
            x7 > 0 ? x7 : 7, Math.Max(1, Int("Param_X7/1", 14)), Int("Param_X8", 28), Int("Param_X9", 35),
            Int("Param_X10", 120), Int("Param_X11", 80), Int("Param_X12", 96), Int("Param_X13", 3),
            Int("Param_X13/1", 3), x13Mode, Int("Param_X14", 3), Int("Param_X14/1", 64));
    }

    private static double ParseBench(string name)
    {
        Match match = Regex.Match(name, @"(?:^|_)Bench\s*=?\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : double.NaN;
    }

    private sealed record FeatureSet(Dictionary<string, TimeSeries> Map, int StartIndex);
    private sealed record ModelForecast(string FileName, double Bench, double[] Values);
    private sealed record ModelParameters(
        int NetworkType, int ContextMode, double Level, int CalculationMethod,
        int InputGradations, int ValidationWithoutGradation,
        int X4, int X4Smooth, int X6, int X6Down, int X7, int X7Plus,
        int X8, int X9, int X10, int X11, int X12, int X13,
        int X13HiLoPeriod, int X13Mode, int X14, int X14MaPeriod);

    private sealed class JbinModel
    {
        private readonly int _hidden, _exit, _mode;
        private readonly float _threshold;
        private readonly float[] _input, _recurrent, _bias, _second, _secondBias, _output, _previous;
        private readonly float _outputBias;
        private float _previousOutput;
        private JbinModel(int architecture, int inputs, int hidden, int exit, int mode, float threshold, float initial,
            float[] input, float[] recurrent, float[] bias, float[] second, float[] secondBias, float[] output, float outputBias)
        { Architecture=architecture; InputSize=inputs; _hidden=hidden; _exit=exit; _mode=mode; _threshold=threshold; _input=input; _recurrent=recurrent; _bias=bias; _second=second; _secondBias=secondBias; _output=output; _outputBias=outputBias; _previous=Enumerable.Repeat(initial,hidden).ToArray(); _previousOutput=initial; }
        public int Architecture { get; }
        public int InputSize { get; }
        public (float[] hidden, float output) GetState() => ((float[])_previous.Clone(), _previousOutput);
        public void SetState(float[] hidden, float output)
        {
            if (hidden.Length != _previous.Length)
                throw new ArgumentException("Размер рекуррентного состояния не совпадает с JBIN.", nameof(hidden));
            Array.Copy(hidden, _previous, _previous.Length);
            _previousOutput = output;
        }

        public static JbinModel Load(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            string signature = reader.ReadString();
            if (signature == "NEURAL_SERIES_JBIN") return LoadUnified(reader, stream);
            int architecture = signature switch { "CLASSIC_MLP"=>1, "JORDAN_DETACHED_MLP"=>2, "ELMAN_DETACHED_MLP"=>3, "JORDAN_ELMAN_DETACHED_MLP"=>4, _=>throw new InvalidDataException($"Неизвестная JBIN-сигнатура: {signature}") };
            int version=reader.ReadInt32(); if ((architecture==3 && version!=2)||(architecture!=3 && version!=1)) throw new InvalidDataException($"Неподдерживаемая JBIN-версия {version}.");
            int inputs=reader.ReadInt32(); if(architecture==2) reader.ReadInt32(); int hidden=reader.ReadInt32(); int outputs=reader.ReadInt32(); int exit=reader.ReadInt32();
            if(inputs<1||hidden<1||outputs!=1||exit is <0 or >2) throw new InvalidDataException("Некорректные размеры JBIN.");
            int mode=architecture==1?0:reader.ReadInt32(); reader.ReadInt32(); reader.ReadSingle(); reader.ReadInt32(); reader.ReadInt64();
            float threshold=architecture==1?.5f:reader.ReadSingle(), initial=architecture==1?.5f:reader.ReadSingle();
            float[] iw,rw,bias,ow; float ob;
            if(architecture==1){reader.ReadDouble(); iw=ReadArray(reader,hidden*inputs);rw=[];bias=ReadArray(reader,hidden);ow=ReadArray(reader,hidden);ob=reader.ReadSingle();}
            else if(architecture==2){int cols=inputs+2;float[] raw=ReadFixed(reader,hidden*cols);iw=new float[hidden*inputs];int rc=mode==3?2:1;rw=new float[hidden*rc];for(int h=0;h<hidden;h++){Array.Copy(raw,h*cols,iw,h*inputs,inputs);if(mode==1)rw[h*rc]=raw[h*cols+inputs];else if(mode==2)rw[h*rc]=raw[h*cols+inputs+1];else{rw[h*2]=raw[h*cols+inputs];rw[h*2+1]=raw[h*cols+inputs+1];}}bias=ReadFixed(reader,hidden);ow=ReadFixed(reader,hidden);ob=reader.ReadSingle();}
            else{reader.ReadDouble();iw=ReadArray(reader,hidden*inputs);int rc=architecture==3?hidden*(mode==3?2:1):hidden+(mode==3?2:1);rw=ReadArray(reader,hidden*rc);bias=ReadArray(reader,hidden);ow=ReadArray(reader,hidden);ob=reader.ReadSingle();}
            return new JbinModel(architecture,inputs,hidden,exit,mode,threshold,initial,iw,rw,bias,[],[],ow,ob);
        }

        private static JbinModel LoadUnified(BinaryReader reader, Stream stream)
        {
            if(reader.ReadInt32()!=1) throw new InvalidDataException("Неподдерживаемая unified JBIN-версия.");
            int length=reader.ReadInt32();byte[] payload=reader.ReadBytes(length);ulong checksum=reader.ReadUInt64();if(checksum!=Fnv(payload))throw new InvalidDataException("Ошибка контрольной суммы JBIN.");
            using var body=new BinaryReader(new MemoryStream(payload,false),Encoding.UTF8);int architecture=body.ReadInt32(),mode=body.ReadInt32(),inputs=body.ReadInt32(),hidden=body.ReadInt32(),outputs=body.ReadInt32(),exit=body.ReadInt32();body.ReadInt32();body.ReadSingle();body.ReadInt32();body.ReadInt64();float threshold=body.ReadSingle(),initial=body.ReadSingle();body.ReadDouble();int tensorCount=body.ReadInt32();var tensors=new Dictionary<string,float[]>(StringComparer.Ordinal);
            for(int t=0;t<tensorCount;t++){string name=body.ReadString();if(body.ReadByte()!=1)throw new InvalidDataException("Неподдерживаемый тип JBIN.");int rank=body.ReadInt32(),count=1;for(int d=0;d<rank;d++)count=checked(count*body.ReadInt32());tensors[name]=ReadFixed(body,count);}
            float[] Req(string name,int expected)=>tensors.TryGetValue(name,out float[]? v)&&v.Length==expected?v:throw new InvalidDataException($"Нет тензора {name}.");int rc=architecture switch{1=>0,2=>mode==3?2:1,3=>hidden*(mode==3?2:1),4=>hidden+(mode==3?2:1),_=>0};
            return new JbinModel(architecture,inputs,hidden,exit,mode,threshold,initial,Req("hidden.weight",hidden*inputs),architecture==1?[]:Req("recurrent.weight",hidden*rc),Req("hidden.bias",hidden),exit==2?Req("hidden2.weight",2*hidden):[],exit==2?Req("hidden2.bias",2):[],Req("output.weight",exit==2?2:hidden),Req("output.bias",1)[0]);
        }
        private static ulong Fnv(ReadOnlySpan<byte> data){ulong hash=14695981039346656037UL;foreach(byte b in data)hash=unchecked((hash^b)*1099511628211UL);return hash;}
        private static float[] ReadArray(BinaryReader r,int expected){int count=r.ReadInt32();if(count!=expected)throw new InvalidDataException("Некорректный тензор JBIN.");return ReadFixed(r,count);}
        private static float[] ReadFixed(BinaryReader r,int count){var a=new float[count];for(int i=0;i<count;i++){a[i]=r.ReadSingle();if(!float.IsFinite(a[i]))throw new InvalidDataException("Неконечный вес JBIN.");}return a;}
        public float Forward(float[] x)
        {
            var current=new float[_hidden];int rc=_hidden==0?0:_recurrent.Length/_hidden;
            for(int h=0;h<_hidden;h++){float sum=_bias[h];for(int i=0;i<InputSize;i++)sum+=_input[h*InputSize+i]*x[i];int wr=h*rc;if(Architecture==2){float hard=_previousOutput>=_threshold?1f:0f;sum+=_recurrent[wr]*(_mode==2?hard:_previousOutput);if(_mode==3)sum+=_recurrent[wr+1]*hard;}else if(Architecture==3){for(int j=0;j<_hidden;j++){float hard=_previous[j]>=_threshold?1f:0f;sum+=_recurrent[wr+j]*(_mode==2?hard:_previous[j]);if(_mode==3)sum+=_recurrent[wr+_hidden+j]*hard;}}else if(Architecture==4){for(int j=0;j<_hidden;j++)sum+=_recurrent[wr+j]*_previous[j];float hard=_previousOutput>=_threshold?1f:0f;sum+=_recurrent[wr+_hidden]*(_mode==2?hard:_previousOutput);if(_mode==3)sum+=_recurrent[wr+_hidden+1]*hard;}current[h]=Sigmoid(sum);}
            float raw=_outputBias;if(_exit==2){for(int s=0;s<2;s++){float sum=_secondBias[s];for(int h=0;h<_hidden;h++)sum+=_second[s*_hidden+h]*current[h];raw+=_output[s]*Sigmoid(sum);}}else for(int h=0;h<_hidden;h++)raw+=_output[h]*current[h];float prediction=_exit is 1 or 2?Sigmoid(raw):raw;Array.Copy(current,_previous,_hidden);_previousOutput=prediction;return prediction;
        }
        private static float Sigmoid(float v)=>v>=0?1f/(1f+MathF.Exp(-v)):MathF.Exp(v)/(1f+MathF.Exp(v));
    }
}
