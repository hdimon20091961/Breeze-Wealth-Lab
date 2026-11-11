using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using WealthLab.Backtest;
using WealthLab.Core;
using WealthLab.Indicators;
using System.Drawing;
using WealthLab.TASC;
using WealthLab.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using WealthLab.AdvancedSmoothers;

namespace WealthScript6 
{
	public class TorchSharpTradingRulesFuturesStrategy : UserStrategyBase
	{
		#region Constants & Global Config Fields

		private const int FixedShift = 3;
		private const int FixedSmoothingPeriod = 8;
		private const int FixedTargetAtrpPeriod = 14;
		private const int DefaultTargetAtrpShift = 7;
		private const double DefaultTargetAtrpThreshold = 0.6;
		private const double FixedReversalPercentage = 2.5;
		private const string PipelineSymbol = "SPY";
		private const string BreezeExportDirectory = @"D:\fear_greed_close\Breeze";
		private const string Rules1ScoreFileName = "rules1_score.csv";
		private const string Rules2ScoreFileName = "rules2_score.csv";

		// Глобальные параметры (fallback, если индивидуальный INI не найден)
		private double level_class;
		private int calculationMethod;
		private int shift;
		private int smoothingPeriod;
		private int targetAtrpShift;
		private double targetAtrpThreshold;
		private double reversalPercentage;
		private int addLayer;
		private List<string> ActiveFeatureNames = new List<string>();

		private const string TorchSharpRootDir = @"D:\TorchSharp4GPU";
		private const string PendingDirectoryName = "Pending";
		private const string RecurrentTradingPackageDirectoryPrefix = "trading_package_Jordan_ZZ";
		private const string ResearchReportsDirectoryName = "research_reports";
		private const string TeachFileHeader = "predict,model_cnn_fear_greed,risk_measure,model_panic_euphoria,myRSI_rep,trendScore,aroonUp,aroonDown,dIMinus,dIPlus,correlationAngle_rep,correlationTrend,hurst,cmf,decibel,jocker,marketBreadth";
		private static readonly ConcurrentDictionary<string, Lazy<ProcessResult>> TradingPackageCache =
			new ConcurrentDictionary<string, Lazy<ProcessResult>>(StringComparer.Ordinal);

		private const int PTradingMode = 0;
		private const int PTradingThreshold = 1;
		private const int PTradingPackageCalculationMethod = 2;
		private const int PTomorrowBars = 3;
		private const int PDisbalance = 4;
		private const int PControlEnabled = 5;
		private const int PControlScheme = 6;
		private int controlEnabled, controlScheme;
		private bool controlExitLatched, rules2ControlHolding;
		private int emaEpisodeState, emaEpisodeStartBar = -1, emaEpisodeHeldBar = -1;
		private bool emaEpisodePreviousZone;
		private double[] controlBreadthVelocity;
		private TimeSeries controlEma50;

		#endregion

		#region Class Fields

		private TimeSeries predict;
		private TimeSeries cumulative_test;
		private TimeSeries cumulative_training;
		private TimeSeries aggregated_ensemble;
		private TimeSeries aggregated_ensemble_RAW;
		private readonly ProcessResult[] packageResults = new ProcessResult[3];

		private readonly DateTime startDateTest = new DateTime(2018, 2, 26);
		private readonly DateTime endDateTest = new DateTime(2026, 7, 22);
		private readonly DateTime startDateTraining = new DateTime(2020, 2, 27);
		private readonly DateTime endDateTraining = new DateTime(2022, 10, 18);
		private readonly DateTime startDateTraining2 = new DateTime(2026, 2, 1);
		private readonly DateTime endDateTraining2 = new DateTime(2026, 7, 21);

		private const double testWeightDuringTraining = 0.99;

		private int totalModelCount;
		private CachedBaseIndicators _cachedIndicators;
		private Dictionary<string, CachedBaseIndicators> _indicatorCacheByParameters = new Dictionary<string, CachedBaseIndicators>();

		// Для аналитики использования переменных x1-x15
		private List<bool[]> modelVariableUsage = new List<bool[]>();
		private List<ModelPairAnalyticsRecord> modelPairAnalyticsRecords = new List<ModelPairAnalyticsRecord>();
		private static readonly object ResearchReportLock = new object();
		private string _researchLogPath;
		private string _activeResearchReportPath;
		private bool _researchReportsReady;
		private bool _researchAppendHistory;
		private string _researchRunId;
		private string _researchSymbol;

		// === НОВОЕ: Режим торговли, порог и глобальный массив децилей ===
		private int tradingMode; // 0=Пропорц. ансамбль, 1=Независимые децили (>порог)
		private int decileThreshold; // Общий торговый уровень Ensemble % и децилей (30-68)
		private TimeSeries[] decileSeries = new TimeSeries[10]; // 10 на пакет, до 30 в Boss
		private TimeSeries activeDecileCountSeries;
		private TimeSeries disbalanceStateSeries;
		private TimeSeries rules1ScoreSeries;
		private TimeSeries rules2ScoreSeries;
		private int tradingPackageCalculationMethod;
		private int tomorrowBars;
		private int disbalanceLevel;
		private readonly Dictionary<string, Dictionary<string, double>[][]> _tomorrowFeatureCacheByParameters =
			new Dictionary<string, Dictionary<string, double>[][]>(StringComparer.Ordinal);
		private int paramX4, paramX4Smooth, paramX6, paramX6Down, paramX7, paramX7Plus, paramX8, paramX9, paramX10, paramX11, paramX12, paramX13, paramX13HiLoPeriod, paramX13Mode, paramX14, paramX14MaPeriod;

		#endregion

		#region Constructor

		public TorchSharpTradingRulesFuturesStrategy()
		{
			StartIndex = 175;
			AddParameter("Режим торговли", ParameterType.Int32, 1, 0, 1, 1);
			WealthLab.Core.Parameter baseThresholdParameter = AddParameter("Базовый порог", ParameterType.Int32, 50, 40, 60, 2);
			baseThresholdParameter.MakeOptimizable = true;
			WealthLab.Core.Parameter tradingPackageZzParameter = AddParameter("Trading Package ZZ (1=ZZ1, 2=ZZ2, 3=ZZ3, 4=Boss)", ParameterType.Int32, 1, 1, 4, 1);
			tradingPackageZzParameter.MakeOptimizable = false;
			WealthLab.Core.Parameter tomorrowBarsParameter = AddParameter("TomorrowBars", ParameterType.Int32, 0, 0, 100, 5);
			tomorrowBarsParameter.MakeOptimizable = false;
			WealthLab.Core.Parameter disbalanceParameter = AddParameter("Величина дисбаланса", ParameterType.Int32, 0, 0, 5, 1);
			disbalanceParameter.MakeOptimizable = true;
			AddParameter("Управление в дисбалансе (0=исходное, 1=новое)", ParameterType.Int32, 0, 0, 1, 1).MakeOptimizable = true;
			AddParameter("Схема (0=НС,1=Rules1,2=Rules2,3=широты,4=EMA50,5=согласование,6=EMA удержание/НС,7=EMA удержание/восстановление)", ParameterType.Int32, 5, 0, 7, 1).MakeOptimizable = true;

		}

		#endregion

		#region Автономный .jbin-инференс Classic/Jordan/Elman/Jordan-Elman
		private sealed class JbinModel
		{
			private readonly int architecture;
			private readonly int inputSize;
			private readonly int hiddenSize;
			private readonly int exitType;
			private readonly int contextMode;
			private readonly float threshold;
			private readonly float initialContext;
			private readonly float[] inputWeights;
			private readonly float[] recurrentWeights;
			private readonly float[] hiddenBias;
			private readonly float[] secondWeights;
			private readonly float[] secondBias;
			private readonly float[] outputWeights;
			private readonly float outputBias;
			private readonly float[] previousHidden;
			private float previousOutput;

			private JbinModel(int architecture, int inputSize, int hiddenSize, int exitType, int contextMode,
				float threshold, float initialContext, float[] inputWeights, float[] recurrentWeights,
				float[] hiddenBias, float[] secondWeights, float[] secondBias, float[] outputWeights, float outputBias)
			{
				this.architecture = architecture;
				this.inputSize = inputSize;
				this.hiddenSize = hiddenSize;
				this.exitType = exitType;
				this.contextMode = contextMode;
				this.threshold = threshold;
				this.initialContext = initialContext;
				this.inputWeights = inputWeights;
				this.recurrentWeights = recurrentWeights;
				this.hiddenBias = hiddenBias;
				this.secondWeights = secondWeights;
				this.secondBias = secondBias;
				this.outputWeights = outputWeights;
				this.outputBias = outputBias;
				previousHidden = Enumerable.Repeat(initialContext, hiddenSize).ToArray();
				previousOutput = initialContext;
			}

			public int InputSize => inputSize;
			public int Architecture => architecture;
			public int ContextMode => contextMode;
			public (float[] hidden, float output) GetState() => ((float[])previousHidden.Clone(), previousOutput);

			public void SetState(float[] hidden, float output)
			{
				if (hidden == null || hidden.Length != hiddenSize) throw new ArgumentException("Размер рекуррентного состояния не совпадает с моделью.", nameof(hidden));
				Array.Copy(hidden, previousHidden, hiddenSize);
				previousOutput = output;
			}

			public static JbinModel Load(string path)
			{
				using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				using var reader = new BinaryReader(stream, Encoding.UTF8);

				string signature = reader.ReadString();
				if (signature == "NEURAL_SERIES_JBIN") return LoadUnified(reader, stream);

				int architecture = signature switch
				{
					"CLASSIC_MLP" => 1,
					"JORDAN_DETACHED_MLP" => 2,
					"ELMAN_DETACHED_MLP" => 3,
					"JORDAN_ELMAN_DETACHED_MLP" => 4,
					_ => throw new InvalidDataException($"Неизвестная сигнатура .jbin: {signature}")
				};

				int version = reader.ReadInt32();
				if ((architecture == 3 && version != 2) || (architecture != 3 && version != 1))
					throw new InvalidDataException($"Неподдерживаемая версия .jbin: {signature} v{version}.");

				int inputs = reader.ReadInt32();
				if (architecture == 2) reader.ReadInt32(); // legacy serialized input size = base + raw + hard
				int hidden = reader.ReadInt32();
				int outputs = reader.ReadInt32();
				int exitType = reader.ReadInt32();

				if (inputs < 1 || hidden < 1 || outputs != 1 || exitType is < 0 or > 2)
					throw new InvalidDataException("Некорректные размеры или exit_type в .jbin.");
				if (exitType == 2)
					throw new InvalidDataException("Формат .jbin рекуррентного конвейера не поддерживает exit_type=2; выберите 0 или 1.");

				int mode = architecture == 1 ? 0 : reader.ReadInt32();
				reader.ReadInt32(); // epoch
				reader.ReadSingle(); // learning rate
				reader.ReadInt32(); // seed
				reader.ReadInt64(); // feature mask

				float contextThreshold = architecture == 1 ? 0.5f : reader.ReadSingle();
				float initialContext = architecture == 1 ? 0.5f : reader.ReadSingle();

				float[] inputWeights;
				float[] recurrentWeights;
				float[] hiddenBias;
				float[] outputWeights;
				float outputBias;

				if (architecture == 1)
				{
					reader.ReadDouble(); // LM mu
					inputWeights = ReadArray(reader, hidden * inputs, "classic input weights");
					recurrentWeights = Array.Empty<float>();
					hiddenBias = ReadArray(reader, hidden, "classic hidden bias");
					outputWeights = ReadArray(reader, hidden, "classic output weights");
					outputBias = reader.ReadSingle();
				}
				else if (architecture == 2)
				{
					int serializedColumns = inputs + 2;
					float[] serialized = ReadFixed(reader, hidden * serializedColumns, "Jordan weights");

					inputWeights = new float[hidden * inputs];
					int recurrentColumns = mode == 3 ? 2 : 1;
					recurrentWeights = new float[hidden * recurrentColumns];

					for (int h = 0; h < hidden; h++)
					{
						Array.Copy(serialized, h * serializedColumns, inputWeights, h * inputs, inputs);

						if (mode == 1) recurrentWeights[h * recurrentColumns] = serialized[h * serializedColumns + inputs];
						else if (mode == 2) recurrentWeights[h * recurrentColumns] = serialized[h * serializedColumns + inputs + 1];
						else
						{
							recurrentWeights[h * 2] = serialized[h * serializedColumns + inputs];
							recurrentWeights[h * 2 + 1] = serialized[h * serializedColumns + inputs + 1];
						}
					}

					hiddenBias = ReadFixed(reader, hidden, "Jordan hidden bias");
					outputWeights = ReadFixed(reader, hidden, "Jordan output weights");
					outputBias = reader.ReadSingle();
				}
				else
				{
					reader.ReadDouble(); // LM mu
					inputWeights = ReadArray(reader, hidden * inputs, "input weights");

					int recurrentColumns = architecture == 3
						? hidden * (mode == 3 ? 2 : 1)
						: hidden + (mode == 3 ? 2 : 1);

					recurrentWeights = ReadArray(reader, hidden * recurrentColumns, "recurrent weights");
					hiddenBias = ReadArray(reader, hidden, "hidden bias");
					outputWeights = ReadArray(reader, hidden, "output weights");
					outputBias = reader.ReadSingle();
				}

				if (!float.IsFinite(outputBias) || stream.Position != stream.Length)
					throw new InvalidDataException("Повреждённый .jbin: неконечный bias или лишние данные.");

				return new JbinModel(architecture, inputs, hidden, exitType, mode, contextThreshold, initialContext,
					inputWeights, recurrentWeights, hiddenBias, Array.Empty<float>(), Array.Empty<float>(), outputWeights, outputBias);
			}

			private static JbinModel LoadUnified(BinaryReader reader, Stream stream)
			{
				int version = reader.ReadInt32();
				if (version != 1) throw new InvalidDataException($"Неподдерживаемая версия NEURAL_SERIES_JBIN: {version}.");

				int payloadLength = reader.ReadInt32();
				if (payloadLength < 0 || payloadLength > stream.Length - stream.Position - sizeof(ulong))
					throw new InvalidDataException("Некорректная длина содержимого JBIN.");

				byte[] payload = reader.ReadBytes(payloadLength);
				if (payload.Length != payloadLength) throw new EndOfStreamException("JBIN оборван до конца содержимого.");

				ulong storedChecksum = reader.ReadUInt64();
				if (storedChecksum != Fnv64(payload)) throw new InvalidDataException("Контрольная сумма JBIN не совпадает.");
				if (stream.Position != stream.Length) throw new InvalidDataException("После JBIN обнаружены лишние данные.");

				using var body = new BinaryReader(new MemoryStream(payload, false), Encoding.UTF8);

				int architecture = body.ReadInt32();
				int mode = body.ReadInt32();
				int inputs = body.ReadInt32();
				int hidden = body.ReadInt32();
				int outputs = body.ReadInt32();
				int exitType = body.ReadInt32();
				body.ReadInt32(); // epoch
				body.ReadSingle(); // learning rate
				body.ReadInt32(); // seed
				body.ReadInt64(); // feature mask
				float contextThreshold = body.ReadSingle();
				float initialContext = body.ReadSingle();
				body.ReadDouble(); // LM mu

				if (architecture is < 1 or > 4 || mode is < 0 or > 3 || inputs < 1 || hidden < 1 || outputs != 1 || exitType is < 0 or > 2)
					throw new InvalidDataException("Некорректные метаданные unified JBIN.");

				int tensorCount = body.ReadInt32();
				if (tensorCount is < 1 or > 32) throw new InvalidDataException($"Некорректное число тензоров: {tensorCount}.");

				var tensors = new Dictionary<string, float[]>(StringComparer.Ordinal);
				for (int tensorIndex = 0; tensorIndex < tensorCount; tensorIndex++)
				{
					string name = body.ReadString();
					if (body.ReadByte() != 1) throw new InvalidDataException($"Неподдерживаемый тип тензора {name}.");

					int rank = body.ReadInt32();
					if (rank is < 1 or > 4) throw new InvalidDataException($"Некорректный ранг тензора {name}.");

					int count = 1;
					for (int dimension = 0; dimension < rank; dimension++)
					{
						int size = body.ReadInt32();
						if (size < 1) throw new InvalidDataException($"Некорректная форма тензора {name}.");
						count = checked(count * size);
					}

					if (!tensors.TryAdd(name, ReadFixed(body, count, name)))
						throw new InvalidDataException($"Тензор {name} записан повторно.");
				}

				if (body.BaseStream.Position != body.BaseStream.Length)
					throw new InvalidDataException("В содержимом JBIN остались лишние данные.");

				float[] Required(string name, int expected)
				{
					if (!tensors.TryGetValue(name, out float[] values) || values.Length != expected)
						throw new InvalidDataException($"Отсутствует тензор {name} из {expected} элементов.");
					return values;
				}

				int recurrentColumns = architecture switch
				{
					1 => 0,
					2 => mode == 3 ? 2 : 1,
					3 => hidden * (mode == 3 ? 2 : 1),
					4 => hidden + (mode == 3 ? 2 : 1),
					_ => 0
				};

				return new JbinModel(architecture, inputs, hidden, exitType, mode, contextThreshold, initialContext,
					Required("hidden.weight", hidden * inputs),
					architecture == 1 ? Array.Empty<float>() : Required("recurrent.weight", hidden * recurrentColumns),
					Required("hidden.bias", hidden),
					exitType == 2 ? Required("hidden2.weight", 2 * hidden) : Array.Empty<float>(),
					exitType == 2 ? Required("hidden2.bias", 2) : Array.Empty<float>(),
					Required("output.weight", exitType == 2 ? 2 : hidden),
					Required("output.bias", 1)[0]);
			}

			private static ulong Fnv64(ReadOnlySpan<byte> bytes)
			{
				ulong hash = 14695981039346656037UL;
				foreach (byte value in bytes) hash = unchecked((hash ^ value) * 1099511628211UL);
				return hash;
			}

			private static float[] ReadArray(BinaryReader reader, int expected, string name)
			{
				int count = reader.ReadInt32();
				if (count != expected) throw new InvalidDataException($"{name}: ожидалось {expected}, найдено {count}.");
				return ReadFixed(reader, count, name);
			}

			private static float[] ReadFixed(BinaryReader reader, int count, string name)
			{
				var result = new float[count];
				for (int i = 0; i < count; i++)
				{
					result[i] = reader.ReadSingle();
					if (!float.IsFinite(result[i])) throw new InvalidDataException($"{name}: неконечный коэффициент {i}.");
				}
				return result;
			}

			public float Forward(float[] x)
			{
				if (x.Length != inputSize) throw new ArgumentException("Размер входа не совпадает с .jbin.", nameof(x));

				var currentHidden = new float[hiddenSize];
				int recurrentColumns = hiddenSize == 0 ? 0 : recurrentWeights.Length / hiddenSize;

				for (int h = 0; h < hiddenSize; h++)
				{
					float sum = hiddenBias[h];
					for (int i = 0; i < inputSize; i++) sum += inputWeights[h * inputSize + i] * x[i];

					int wr = h * recurrentColumns;

					if (architecture == 2)
					{
						float hard = previousOutput >= threshold ? 1f : 0f;
						sum += recurrentWeights[wr] * (contextMode == 2 ? hard : previousOutput);
						if (contextMode == 3) sum += recurrentWeights[wr + 1] * hard;
					}
					else if (architecture == 3)
					{
						for (int j = 0; j < hiddenSize; j++)
						{
							float hard = previousHidden[j] >= threshold ? 1f : 0f;
							sum += recurrentWeights[wr + j] * (contextMode == 2 ? hard : previousHidden[j]);
							if (contextMode == 3) sum += recurrentWeights[wr + hiddenSize + j] * hard;
						}
					}
					else if (architecture == 4)
					{
						for (int j = 0; j < hiddenSize; j++) sum += recurrentWeights[wr + j] * previousHidden[j];

						float hard = previousOutput >= threshold ? 1f : 0f;
						sum += recurrentWeights[wr + hiddenSize] * (contextMode == 2 ? hard : previousOutput);
						if (contextMode == 3) sum += recurrentWeights[wr + hiddenSize + 1] * hard;
					}

					currentHidden[h] = Sigmoid(sum);
				}

				float raw = outputBias;

				if (exitType == 2)
				{
					for (int second = 0; second < 2; second++)
					{
						float sum = secondBias[second];
						for (int h = 0; h < hiddenSize; h++) sum += secondWeights[second * hiddenSize + h] * currentHidden[h];
						raw += outputWeights[second] * Sigmoid(sum);
					}
				}
				else
				{
					for (int h = 0; h < hiddenSize; h++) raw += outputWeights[h] * currentHidden[h];
				}

				float prediction = exitType is 1 or 2 ? Sigmoid(raw) : raw;

				Array.Copy(currentHidden, previousHidden, hiddenSize);
				previousOutput = prediction;

				return prediction;
			}

			private static float Sigmoid(float value) => value >= 0
				? 1f / (1f + MathF.Exp(-value))
				: MathF.Exp(value) / (1f + MathF.Exp(value));
		}
		#endregion


		#region Cached Indicators Structure

		private class CachedBaseIndicators
		{
			public TimeSeries model_cnn_fear_greed_base;
			public TimeSeries risk_measure_base;
			public TimeSeries model_panic_euphoria_base;
			public TimeSeries myRSI_rep_base;
			public TimeSeries trendScore_base;
			public TimeSeries aroonUp_base;
			public TimeSeries aroonDown_base;
			public TimeSeries dIMinus_base;
			public TimeSeries dIPlus_base;
			public TimeSeries correlationAngle_rep_base;
			public TimeSeries correlationTrend_base;
			public TimeSeries hurst_base;
			public TimeSeries cmf_base;
			public TimeSeries decibel_base;
			public TimeSeries jocker_base;
			public TimeSeries marketBreadth_base;
			public int modelStartIndex;
		}

		#endregion

		#region Process Result

		private class ProcessResult
		{
			public TimeSeries Ensemble { get; set; }
			public TimeSeries EnsembleRAW { get; set; }
			public double[] TomorrowClassifiedCounts { get; set; }
			public TimeSeries[] TomorrowHistoryCounts { get; set; }
			public int TomorrowModelCount { get; set; }
			public TimeSeries Target { get; set; }
			public string TargetDescription { get; set; }
			public List<(string FileName, double BenchValue, TimeSeries ForecastTS)> AllModels { get; set; }
			public int ModelCount { get; set; }
			public int LastModelStartIndex { get; set; }
			public List<bool[]> VariableUsage { get; set; }
			public List<ModelPairAnalyticsRecord> PairAnalyticsRecords { get; set; }
		}

		private class ModelPairAnalyticsRecord
		{
			public bool[] Usage { get; set; }
			public double Bench { get; set; }
		}

		#endregion

		#region Model Parameters (для индивидуальной загрузки)
		private class ModelParameters
		{
			public int networkType = 1;
			public int contextMode = 1;
			public double contextThreshold = 0.5;
			public double initialContext = 0.5;
			public string modelSetId = "";
			public double level_class = 0.5;
			public int calculationMethod = 1;
			public int shift = 3;
			public int smoothingPeriod = 8;
			public int targetAtrpPeriod = FixedTargetAtrpPeriod;
			public int targetAtrpShift = DefaultTargetAtrpShift;
			public double targetAtrpThreshold = DefaultTargetAtrpThreshold;
			public double reversalPercentage = 2.5;
			public int addLayer = 3;
			public int exit_type = 1;
			public int lossFunction = 1;
			public double reentryPenaltyLambda = 0;
			public int reentryPenaltyNorm = 3;
			public double reentryPenaltyLevel = 0.4;
			public int inputGradationLevels = 0;
			public int validationWithoutGradation = 0;
			public double adamL2 = 0.01;
			public double headL1 = 0;
			public double headL2 = 0;
			public double hiddenL1 = 0;
			public double hiddenL2 = 0;
			public double headDropout = 0;
			public double headPruningThreshold = 0;

			public int trainProfitWeightPercent = 100;
			public int profitMetricSchema = 1;
			public double storedTrainRaw = double.NaN;

			public double reCross3Percent = double.NaN;

			public double hiddenMean = double.NaN, hiddenMedian = double.NaN, hiddenP90 = double.NaN, hiddenP95 = double.NaN, hiddenP99 = double.NaN, hiddenMax = double.NaN, hiddenRms = double.NaN;
			public double outputMean = double.NaN, outputMedian = double.NaN, outputP90 = double.NaN, outputP95 = double.NaN, outputP99 = double.NaN, outputMax = double.NaN, outputRms = double.NaN;

			public string orthoConfigKey = "";
			public int orthoGroupModels;
			public double orthoBestBench = double.NaN, orthoBenchLossPercent = double.NaN, orthoBestBenchReCross3 = double.NaN, orthoReCrossImprovementPoints = double.NaN;
			public int orthoNaturalCandidates, orthoPoolAfterMinimum, orthoBenchRank, orthoReCrossRank, orthoSelectionOrder, orthoTopModelsLimit, orthoFinalBenchRankWithinW;

			public int paramX4 = 14;
			public int paramX4Smooth = 8;
			public int paramX6 = 40;
			public int paramX6Down = 40;
			public int paramX7 = 7;
			public int paramX7Plus = 14;
			public int paramX8 = 28;
			public int paramX9 = 35;
			public int paramX10 = 80;
			public int paramX11 = 80;
			public int paramX12 = 96;
			public int paramX13 = 3;
			public int paramX13HiLoPeriod = 140;
			public int paramX13Mode = 1;
			public int paramX14 = 3;
			public int paramX14MaPeriod = 64;
		}
		#endregion

		#region Fresh research reports

		private string ResearchReportsDirectory => Path.Combine(TorchSharpRootDir, ResearchReportsDirectoryName);

		private void InitializeResearchReports(BarHistory bars)
		{
			try
			{
				string directory = ResearchReportsDirectory;
				Directory.CreateDirectory(directory);
				const bool appendRequested = true;
				bool optimizationRun = ExecutionMode == StrategyExecutionMode.Optimization;
				_researchAppendHistory = appendRequested || optimizationRun;
				_researchRunId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
				_researchSymbol = bars.Symbol ?? string.Empty;

				lock (ResearchReportLock)
				{
					if (!_researchAppendHistory)
					{
						foreach (string oldFile in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
						{
							try { File.Delete(oldFile); }
							catch { }
						}
					}

					string started = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
					string historyMode = optimizationRun && !appendRequested ? "Append (forced for Optimization)" : (_researchAppendHistory ? "Append" : "Fresh");
					_researchLogPath = Path.Combine(directory, "debug_log_current.txt");
					string runHeader =
						$"\r\n================ RUN {_researchRunId} ================\r\n" +
						$"Started={started}\r\nBars={bars.Count}\r\nSymbol={_researchSymbol}\r\n" +
						$"ExecutionMode={ExecutionMode}\r\n" +
						$"ReportsHistory={historyMode}\r\n\r\n";
					WriteResearchStart(_researchLogPath, runHeader);
					WriteResearchStart(Path.Combine(directory, "run_info.txt"),
						$"\r\n[RUN {_researchRunId}]\r\nStarted={started}\r\nBars={bars.Count}\r\nSymbol={_researchSymbol}\r\n" +
						$"ExecutionMode={ExecutionMode}\r\n" +
						$"ReportsHistoryRequested={(appendRequested ? "Append" : "Fresh")}\r\n" +
						$"ReportsHistory={historyMode}\r\n" +
						BuildResearchParameterSnapshot());
				}
				_activeResearchReportPath = null;
				_researchReportsReady = true;
			}
			catch (Exception ex)
			{
				_researchReportsReady = false;
				base.WriteToDebugLog($"Не удалось подготовить research_reports: {ex.Message}", false);
			}
		}

		private void WriteResearchStart(string path, string content)
		{
			lock (ResearchReportLock)
			{
				if (_researchAppendHistory)
					File.AppendAllText(path, content, new UTF8Encoding(false));
				else
					File.WriteAllText(path, content, new UTF8Encoding(false));
			}
		}

		private string BuildResearchParameterSnapshot()
		{
			var snapshot = new StringBuilder();
			snapshot.Append("ParameterCount=").Append(Parameters.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

			for (int i = 0; i < Parameters.Count; i++)
			{
				WealthLab.Core.Parameter parameter = Parameters[i];
				string name = (parameter.Name ?? $"Parameter{i}")
					.Replace("\r", " ")
					.Replace("\n", " ")
					.Replace("=", ":");
				object rawValue = parameter.Value;
				string value = rawValue is IFormattable formattable
					? formattable.ToString(null, CultureInfo.InvariantCulture)
					: Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;

				snapshot.Append("Parameter.")
					.Append(i.ToString("D2", CultureInfo.InvariantCulture))
					.Append('.')
					.Append(name)
					.Append('=')
					.Append(value)
					.Append("\r\n");
			}

			snapshot.Append("Constant.Shift=").Append(FixedShift.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			snapshot.Append("Constant.SmoothingPeriod=").Append(FixedSmoothingPeriod.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			snapshot.Append("Constant.TargetAtrpPeriod=").Append(FixedTargetAtrpPeriod.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			snapshot.Append("Constant.TargetAtrpShift=").Append(DefaultTargetAtrpShift.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			snapshot.Append("Constant.TargetAtrpThreshold=").Append(DefaultTargetAtrpThreshold.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
			snapshot.Append("Constant.ReversalPercentage=").Append(FixedReversalPercentage.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");

			return snapshot.ToString();
		}

		private void BeginResearchReport(string reportName)
		{
			if (!_researchReportsReady) return;
			try
			{
				string safeName = Regex.Replace(reportName ?? "report", @"[^A-Za-z0-9_.-]+", "_");
				_activeResearchReportPath = Path.Combine(ResearchReportsDirectory, safeName + ".txt");
				WriteResearchStart(_activeResearchReportPath,
					$"\r\n================ RUN {_researchRunId} ================\r\n" +
					$"Report={reportName}\r\nSymbol={_researchSymbol}\r\n\r\n");
			}
			catch (Exception ex)
			{
				_activeResearchReportPath = null;
				WriteToDebugLog($"Не удалось создать отдельный исследовательский отчёт {reportName}: {ex.Message}", false);
			}
		}

		private void EndResearchReport()
		{
			_activeResearchReportPath = null;
		}

		private void WriteToDebugLog(string message, bool append)
		{
			base.WriteToDebugLog(message, append);
			if (!_researchReportsReady) return;

			string line = (message ?? string.Empty) + Environment.NewLine;
			lock (ResearchReportLock)
			{
				try
				{
					File.AppendAllText(_researchLogPath, line, new UTF8Encoding(false));
					if (!string.IsNullOrEmpty(_activeResearchReportPath))
						File.AppendAllText(_activeResearchReportPath, line, new UTF8Encoding(false));
				}
				catch
				{
					// Файловый отчёт не должен останавливать торговую стратегию.
				}
			}
		}

		#endregion

		public override void Initialize(BarHistory bars)
		{
			// === ИНИЦИАЛИЗАЦИЯ ВСЕХ TIME SERIES СРАЗУ (защита от null) ===
			cumulative_test = new TimeSeries(bars.DateTimes, 0);
			cumulative_training = new TimeSeries(bars.DateTimes, 0);
			predict = new TimeSeries(bars.DateTimes, double.NaN);
			aggregated_ensemble = new TimeSeries(bars.DateTimes, 0);
			aggregated_ensemble_RAW = new TimeSeries(bars.DateTimes, 0.0);
			activeDecileCountSeries = new TimeSeries(bars.DateTimes, 0.0);

			// СБРОС СОСТОЯНИЯ
			_cachedIndicators = null;
			_indicatorCacheByParameters = new Dictionary<string, CachedBaseIndicators>();
			_tomorrowFeatureCacheByParameters.Clear();
			ActiveFeatureNames = new List<string>();
			totalModelCount = 0;
			modelVariableUsage = new List<bool[]>();
			modelPairAnalyticsRecords = new List<ModelPairAnalyticsRecord>();

			// Фиксированные пакеты ZZ1/ZZ2/ZZ3 сформированы для SPY.
			// Эта стратегия только загружает и проторговывает их; внешние консоли не запускаются.
			if (!string.Equals(bars.Symbol, PipelineSymbol, StringComparison.OrdinalIgnoreCase))
			{
				base.WriteToDebugLog(
					$"[ПРОПУСК] {bars.Symbol}: фиксированные торговые пакеты ZZ1/ZZ2/ZZ3 предназначены для {PipelineSymbol}.",
					false);
				return;
			}

			Directory.CreateDirectory(TorchSharpRootDir);
			InitializeResearchReports(bars);

			DateTime initializeStart = DateTime.Now;

			tradingMode = Parameters[PTradingMode].AsInt;
			decileThreshold = Parameters[PTradingThreshold].AsInt;
			tomorrowBars = Parameters[PTomorrowBars].AsInt;
			disbalanceLevel = Parameters[PDisbalance].AsInt;
			controlEnabled = Parameters[PControlEnabled].AsInt;
			controlScheme = Parameters[PControlScheme].AsInt;
			controlExitLatched = false;
			rules2ControlHolding = false;
			emaEpisodeState = 0;
			emaEpisodePreviousZone = false;
			emaEpisodeStartBar = emaEpisodeHeldBar = -1;
			addLayer = 3;
			level_class = 0.5;
			shift = FixedShift;
			smoothingPeriod = FixedSmoothingPeriod;
			targetAtrpShift = DefaultTargetAtrpShift;
			targetAtrpThreshold = DefaultTargetAtrpThreshold;
			reversalPercentage = FixedReversalPercentage;
			paramX4 = 14;
			paramX4Smooth = 7;
			paramX6 = 40;
			paramX6Down = 48;
			paramX7 = 7;
			paramX7Plus = 14;
			paramX8 = 28;
			paramX9 = 35;
			paramX10 = 120;
			paramX11 = 80;
			paramX12 = 96;
			paramX13 = 3;
			paramX13HiLoPeriod = 140;
			paramX13Mode = 1;
			paramX14 = 3;
			paramX14MaPeriod = 64;
			tradingPackageCalculationMethod = Parameters[PTradingPackageCalculationMethod].AsInt;
			calculationMethod = tradingPackageCalculationMethod == 4 ? 1 : tradingPackageCalculationMethod;
			decileSeries = new TimeSeries[tradingPackageCalculationMethod == 4 ? 30 : 10];
			for (int g = 0; g < decileSeries.Length; g++)
				decileSeries[g] = new TimeSeries(bars.DateTimes, 0.0);
			packageResults[0] = null;
			packageResults[1] = null;
			packageResults[2] = null;
			LoadAndPlotRulesScores(bars);
			PreparePositionControl(bars);
			PlotTimeSeries(controlEma50, "EMA50", "Price", WLColor.Red, PlotStyle.Line);

			WriteToDebugLog(
				$"Research reports: {ResearchReportsDirectory}; режим={(_researchAppendHistory ? "история с дозаписью" : "только текущий запуск")}; RunId={_researchRunId}.",
				false);
			WriteToDebugLog($"=== РЕЖИМ ТОРГОВЛИ: {(tradingMode == 0 ? "ПО 10 ПОЗИЦИЙ НА ПАКЕТ ПРИ ENSEMBLE % >= " + decileThreshold : "НЕЗАВИСИМЫЕ ДЕЦИЛИ (>= " + decileThreshold + ")")} ===", false);
			WriteToDebugLog($"=== ПРОТОРГОВКА: {(tradingPackageCalculationMethod == 4 ? "Boss ZZ1 + ZZ2 + ZZ3 раздельно, до 30 позиций" : "ZZ" + tradingPackageCalculationMethod)}; уровень пересечения={decileThreshold} ===", false);
			WriteToDebugLog("=== RULES: на отдельной панели показаны Rules 1 и Rules 2; торговый порог фиксирован ===", false);
			WriteToDebugLog($"=== FUTURES: TomorrowBars={tomorrowBars}; Ensemble=FULL ===", false);
			WriteToDebugLog("=== ЦВЕЙГ Z02: проверен и отклонён; в торговом пороге не используется ===", false);
			WriteToDebugLog("=== КОНТУР МОДЕЛЕЙ: Jordan/Elman / .jbin ===", false);

			ActiveFeatureNames = new List<string> { "model_cnn_fear_greed", "risk_measure", "model_panic_euphoria", "myRSI_rep", "trendScore", "aroonUp", "aroonDown", "dIMinus", "dIPlus", "correlationAngle_rep", "correlationTrend", "hurst", "cmf", "decibel", "jocker", "marketBreadth" };
			WriteToDebugLog("=== ВНЕШНИЕ КОНСОЛИ GPU И ORTHO НЕ ЗАПУСКАЮТСЯ ===", false);

			bool bossPackages = tradingPackageCalculationMethod == 4;
			int[] packageMethods = bossPackages ? new[] { 1, 2, 3 } : new[] { tradingPackageCalculationMethod };

			foreach (int packageMethod in packageMethods)
			{
				string packageLabel = $"ZZ{packageMethod}";
				string packagePath = GetTradingPackageDirectory(packageMethod);
				calculationMethod = packageMethod;

				// ZigZag рисуется только один раз, а SuperSmoother — без сдвига.
				if (packageMethod == 1)
				{
					CalculatePredictWithShift(bars);
					PlotZz1SuperSmoother(bars);
				}
				else if (packageMethod == 2)
					CalculatePredictWithPeakTrough(bars, true);
				else
					CalculatePredictWithAtrpImpulse(bars, (bars.Close + bars.High + bars.Low) / 3);

				WriteToDebugLog($"=== ОБРАБОТКА СОХРАНЁННОГО ПАКЕТА {packageLabel}: {packagePath} ===", false);
				ProcessResult result = ProcessTradingPackageCached(
					bars, packagePath, $"ensemblePR_{packageLabel}.txt", packageLabel, packageMethod);
				packageResults[packageMethod - 1] = result;
				totalModelCount += result.ModelCount;
				if (result.VariableUsage != null)
					modelVariableUsage.AddRange(result.VariableUsage);
				if (result.PairAnalyticsRecords != null)
					modelPairAnalyticsRecords.AddRange(result.PairAnalyticsRecords);

				for (int i = 0; i < bars.Count; i++)
				{
					aggregated_ensemble[i] += result.Ensemble[i];
					aggregated_ensemble_RAW[i] += result.EnsembleRAW[i];
				}

				TimeSeries ensemblePercent = new TimeSeries(bars.DateTimes, 0.0);
				for (int i = 0; i < bars.Count; i++)
					ensemblePercent[i] = result.Ensemble[i] * 100.0 / result.ModelCount;
				WLColor ensembleColor = bossPackages
					? (packageMethod == 1 ? WLColor.Black : packageMethod == 2 ? WLColor.Red : WLColor.Blue)
					: WLColor.Purple;
				PlotTimeSeries(ensemblePercent, $"Ensemble % {packageLabel}", "Ensemble %", ensembleColor, PlotStyle.Line);
				PlotTomorrowForecasts(bars, result, packageLabel, ensembleColor, bossPackages);

				int decileOffset = bossPackages ? (packageMethod - 1) * 10 : 0;
				BuildAndPlotDeciles(bars, result.AllModels, packageLabel, result.ModelCount, decileOffset);
				BuildLeaveOneOutAnalysis(bars, result.AllModels, packageLabel, result.ModelCount, result.LastModelStartIndex, result.Ensemble);
				BuildDecileContributionAnalysis(bars, result.AllModels, packageLabel, result.ModelCount, result.LastModelStartIndex, result.Ensemble);
			}

			if (tradingPackageCalculationMethod == 3 && packageResults[2]?.Target != null)
			{
				const string zz3PredictPane = "ZZ3 Predict";
				PlotTimeSeries(
					packageResults[2].Target,
					packageResults[2].TargetDescription,
					zz3PredictPane,
					WLColor.DarkGreen,
					PlotStyle.Line);
				DrawHorzLine(0.5, WLColor.Gray, 1, LineStyle.Dashed, zz3PredictPane, false);
				SetPaneDrawingOptions(zz3PredictPane, 25);
			}

			TimeSeries tradingThresholdSeries = new TimeSeries(bars.DateTimes, decileThreshold);
			PlotTimeSeries(tradingThresholdSeries, "Базовый торговый порог", "Ensemble %", WLColor.Green, PlotStyle.Line);
			SetPaneDrawingOptions("Ensemble %", 25);
			// Wealth-Lab может переназначить уже отрисованную TimeSeries другой панели.
			// Для децилей используем отдельный экземпляр с теми же процентными значениями.
			TimeSeries decileThresholdDisplay = new TimeSeries(bars.DateTimes, decileThreshold);
			PlotTimeSeries(decileThresholdDisplay, "Базовый порог, %", "Decile", WLColor.Green, PlotStyle.Line);
			SetPaneDrawingOptions("Decile", 125);
			PlotActiveDecileCount(bars);
			PlotDecileImbalance(bars);

			// === АНАЛИТИКА ПО ИСПОЛЬЗОВАНИЮ ПЕРЕМЕННЫХ x1...x15 (ВСЕГДА!) ===
			if (totalModelCount > 0 && modelVariableUsage.Count > 0)
			{
				BeginResearchReport("feature_usage");
				WriteToDebugLog("", false);
				WriteToDebugLog("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
				WriteToDebugLog("│                                      АНАЛИТИКА ПО ИСПОЛЬЗОВАНИЮ ПЕРЕМЕННЫХ x1...x15                                            │", false);
				WriteToDebugLog("├────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);

				int[] usageCount = new int[15];
				for (int m = 0; m < modelVariableUsage.Count; m++)
				{
					for (int i = 0; i < 15; i++)
					{
						if (modelVariableUsage[m][i])
							usageCount[i]++;
					}
				}

				string headerRow = "│";
				for (int i = 1; i <= 15; i++)
				{
					headerRow += $" x{i,2}  │";
				}
				WriteToDebugLog(headerRow, false);

				string percentRow = "│";
				for (int i = 0; i < 15; i++)
				{
					double usagePercent = (double)usageCount[i] / modelVariableUsage.Count * 100.0;
					percentRow += $" {usagePercent,4:F0}%│";
				}
				WriteToDebugLog(percentRow, false);

				int[,] pairUsageCount = new int[15, 15];
				for (int m = 0; m < modelVariableUsage.Count; m++)
				{
					for (int i = 0; i < 15; i++)
					{
						if (!modelVariableUsage[m][i]) continue;
						for (int j = i + 1; j < 15; j++)
						{
							if (modelVariableUsage[m][j])
								pairUsageCount[i, j]++;
						}
					}
				}

				WriteToDebugLog("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
				WriteToDebugLog("│                                    ПАРНОЕ ИСПОЛЬЗОВАНИЕ (%)                                                                    │", false);
				WriteToDebugLog("├────────────┬────┬────┬────────┬────┬────┬────────┬────┬────┬────────┬────┬────┤", false);
				WriteToDebugLog("│        │ x1 │ x2 │ x3 │ x4 │ x5 │ x6 │ x7 │ x8 │ x9 │x10 │x11 │x12 │x13 │x14 │x15│", false);
				WriteToDebugLog("├────────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┤", false);

				for (int i = 0; i < 15; i++)
				{
					string pairRow = $"│  x{i + 1,2}  │";
					for (int j = 0; j < 15; j++)
					{
						if (j <= i)
						{
							pairRow += "    │";
						}
						else
						{
							double percent = (double)pairUsageCount[i, j] / modelVariableUsage.Count * 100.0;
							pairRow += $" {percent,3:F0}│";
						}
					}
					WriteToDebugLog(pairRow, false);
				}
				WriteToDebugLog("└────────────────┴────┴────────┴────┴────┴────────┴────────┴────────┴────┴────", false);
				EndResearchReport();

				BuildPairSelectionAndSynergyAnalytics();
			}

			DateTime initializeEnd = DateTime.Now;
			WriteToDebugLog($"=== ОБЩЕЕ ВРЕМЯ ВЫПОЛНЕНИЯ INITIALIZE: {(initializeEnd - initializeStart).TotalSeconds:F2} сек ===", false);
		}

		private void PlotTomorrowForecasts(
			BarHistory bars,
			ProcessResult result,
			string packageLabel,
			WLColor ensembleColor,
			bool bossMode)
		{
			if (bars == null || bars.Count == 0 || result == null ||
				result.TomorrowModelCount <= 0 || result.TomorrowClassifiedCounts == null)
				return;

			int last = bars.Count - 1;
			double[] currentPr = new double[5];
			for (int scenario = 0; scenario < currentPr.Length; scenario++)
				currentPr[scenario] = result.TomorrowClassifiedCounts[scenario] * 100.0 / result.TomorrowModelCount;

			string[] scenarioNames = { "-2%", "-1%", "0%", "+1%", "+2%" };
			WLColor[] colors = { WLColor.Red, WLColor.Red, ensembleColor, WLColor.Green, WLColor.Green };

			// Реального t+1 в Wealth-Lab ещё нет: все пять актуальных сценариев
			// показываются полыми квадратами на последнем существующем баре.
			for (int scenario = 0; scenario < currentPr.Length; scenario++)
				DrawText("□", last, currentPr[scenario], colors[scenario], bossMode ? 18 : 20, "Ensemble %", true);

			if (bossMode)
			{
				WriteToDebugLog(
					$"FUTURES BOSS {packageLabel}: Models={result.TomorrowModelCount} | " +
					$"-2%={currentPr[0]:F2}% -1%={currentPr[1]:F2}% 0%={currentPr[2]:F2}% +1%={currentPr[3]:F2}% +2%={currentPr[4]:F2}%",
					false);
				return;
			}

			TimeSeries[] historyPlots = new TimeSeries[5];
			int firstDisplay = Math.Max(1, bars.Count - tomorrowBars);
			for (int scenario = 0; scenario < historyPlots.Length; scenario++)
			{
				historyPlots[scenario] = new TimeSeries(bars.DateTimes, double.NaN);
				for (int bar = firstDisplay; bar < bars.Count; bar++)
					historyPlots[scenario][bar] =
						result.TomorrowHistoryCounts[scenario][bar] * 100.0 / result.TomorrowModelCount;
			}

			// Сначала квадраты, затем исторические точки: вчерашний прогноз на сегодня
			// остаётся видимым поверх актуального прогноза сегодня -> завтра.
			for (int scenario = 0; scenario < historyPlots.Length; scenario++)
				PlotTimeSeries(
					historyPlots[scenario],
					$"Tomorrow {packageLabel} {scenarioNames[scenario]}",
					"Ensemble %",
					colors[scenario],
					PlotStyle.Dots);

			const double weakOpacity = 4.0;
			const double mediumOpacity = 7.0;
			const double strongOpacity = 10.0;
			for (int bar = firstDisplay; bar < last; bar++)
			{
				double threshold = decileThreshold;
				int above = 0, below = 0;
				for (int scenario = 0; scenario < historyPlots.Length; scenario++)
				{
					double value = historyPlots[scenario][bar];
					if (value > threshold) above++;
					else if (value < threshold) below++;
				}
				SetTomorrowBackground(bars, bar, above - below, weakOpacity, mediumOpacity, strongOpacity);
			}

			double currentThreshold = decileThreshold;
			int currentAbove = 0, currentBelow = 0;
			for (int scenario = 0; scenario < currentPr.Length; scenario++)
			{
				if (currentPr[scenario] > currentThreshold) currentAbove++;
				else if (currentPr[scenario] < currentThreshold) currentBelow++;
			}
			SetTomorrowBackground(bars, last, currentAbove - currentBelow, weakOpacity, mediumOpacity, strongOpacity);

			WriteToDebugLog(
				$"FUTURES {packageLabel}: TomorrowBars={tomorrowBars}; Models={result.TomorrowModelCount} | " +
				$"-2%={currentPr[0]:F2}% -1%={currentPr[1]:F2}% 0%={currentPr[2]:F2}% +1%={currentPr[3]:F2}% +2%={currentPr[4]:F2}%",
				false);
		}

		private void SetTomorrowBackground(
			BarHistory bars,
			int bar,
			int balance,
			double weakOpacity,
			double mediumOpacity,
			double strongOpacity)
		{
			if (balance == 0)
				return;

			int absoluteBalance = Math.Abs(balance);
			double opacity = absoluteBalance >= 5
				? strongOpacity
				: absoluteBalance >= 3 ? mediumOpacity : weakOpacity;
			int alpha = Math.Max(0, Math.Min(255, (int)Math.Round(255.0 * opacity / 100.0)));
			WLColor background = balance > 0
				? new WLColor((byte)alpha, (byte)0, (byte)128, (byte)0)
				: new WLColor((byte)alpha, (byte)255, (byte)0, (byte)0);
			SetBackgroundColor(bars, bar, background, "Ensemble %");
		}

		#region Trading Package Location

		private const string ActiveModelExtension = ".jbin";

		private string GetTradingPackageDirectory(int method) =>
			Path.Combine(
				TorchSharpRootDir,
				RecurrentTradingPackageDirectoryPrefix +
				method.ToString(CultureInfo.InvariantCulture));

		#endregion

		#region Load Individual Model Parameters
		private ModelParameters LoadIndividualModelParameters(string iniFilePath, string ptFileName)
		{
			if (!File.Exists(iniFilePath))
			{
				WriteToDebugLog($"  [ПРОПУСК] Модель {ptFileName} пропущена: не найден соответствующий INI-файл ({Path.GetFileName(iniFilePath)}).", false);
				return null;
			}

			var pars = new ModelParameters();

			var iniParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			var lines = File.ReadAllLines(iniFilePath);
			foreach (var rawLine in lines)
			{
				string line = rawLine.Trim();
				if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[")) continue;

				int eqPos = line.IndexOf('=');
				if (eqPos <= 0) continue;

				string key = line.Substring(0, eqPos).Trim();
				string value = line.Substring(eqPos + 1).Trim();

				iniParams[key] = value;
			}

			string GetVal(string key, string def) => iniParams.TryGetValue(key, out string v) && !string.IsNullOrWhiteSpace(v) ? v : def;
			double GetDouble(string key, double def = double.NaN) => double.TryParse(GetVal(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : def;
			int GetInt(string key, int def = 0) => int.TryParse(GetVal(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : def;

			pars.modelSetId = GetVal("ModelSetId", "");
			pars.networkType = GetInt("network_type", 1);
			pars.contextMode = GetInt("context_mode", GetInt("jordan_mode", 1));
			pars.contextThreshold = GetDouble("jordan_threshold", 0.5);
			pars.initialContext = GetDouble("jordan_initial_context", 0.5);

			pars.level_class = double.Parse(GetVal("level_class", level_class.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
			pars.calculationMethod = int.Parse(GetVal("CalculationMethod", calculationMethod.ToString()), CultureInfo.InvariantCulture);
			pars.shift = int.Parse(GetVal("Shift", shift.ToString()), CultureInfo.InvariantCulture);
			pars.smoothingPeriod = int.Parse(GetVal("SmoothingPeriod", smoothingPeriod.ToString()), CultureInfo.InvariantCulture);
			pars.targetAtrpPeriod = int.Parse(GetVal("TargetAtrpPeriod", FixedTargetAtrpPeriod.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
			pars.targetAtrpShift = int.Parse(GetVal("TargetAtrpShift", DefaultTargetAtrpShift.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
			pars.targetAtrpThreshold = double.Parse(GetVal("TargetAtrpThreshold", DefaultTargetAtrpThreshold.ToString("R", CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
			pars.reversalPercentage = double.Parse(GetVal("ReversalPercentage", reversalPercentage.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
			pars.addLayer = int.Parse(GetVal("addLayer", addLayer.ToString()), CultureInfo.InvariantCulture);

			pars.exit_type = int.Parse(GetVal("exit_type", "1"), CultureInfo.InvariantCulture);
			if (pars.exit_type < 0 || pars.exit_type > 2)
				throw new InvalidDataException($"Некорректный exit_type={pars.exit_type} в {Path.GetFileName(iniFilePath)}; допустимы 0, 1 и 2.");

			pars.lossFunction = int.Parse(GetVal("loss_function", "1"), CultureInfo.InvariantCulture);
			pars.reentryPenaltyLambda = double.Parse(GetVal("train_reentry_penalty_lambda", "0"), CultureInfo.InvariantCulture);
			pars.reentryPenaltyNorm = int.Parse(GetVal("train_reentry_penalty_norm", "3"), CultureInfo.InvariantCulture);
			pars.reentryPenaltyLevel = double.Parse(GetVal("train_reentry_penalty_level", "0.4"), CultureInfo.InvariantCulture);

			// Старые модели создавались без градации и с фиксированным сглаживанием MyRSI=8.
			pars.inputGradationLevels = int.Parse(GetVal("InputGradations", "0"), CultureInfo.InvariantCulture);
			pars.validationWithoutGradation = int.Parse(GetVal("ValidationWithoutGradation", "0"), CultureInfo.InvariantCulture);
			pars.adamL2 = double.Parse(GetVal("adam_l2", "0.01"), CultureInfo.InvariantCulture);
			pars.headL1 = double.Parse(GetVal("head_l1", "0"), CultureInfo.InvariantCulture);
			pars.headL2 = double.Parse(GetVal("head_l2", "0"), CultureInfo.InvariantCulture);
			pars.hiddenL1 = double.Parse(GetVal("hidden_l1", "0"), CultureInfo.InvariantCulture);
			pars.hiddenL2 = double.Parse(GetVal("hidden_l2", "0"), CultureInfo.InvariantCulture);
			pars.headDropout = double.Parse(GetVal("head_dropout", "0"), CultureInfo.InvariantCulture);
			pars.headPruningThreshold = double.Parse(GetVal("head_pruning_threshold", "0"), CultureInfo.InvariantCulture);

			pars.trainProfitWeightPercent = int.Parse(
				GetVal("TrainProfitWeightPercent", GetVal("TestWeightDuringTrainingPercent", "100")),
				CultureInfo.InvariantCulture);

			pars.profitMetricSchema = int.Parse(GetVal("ProfitMetricSchema", "1"), CultureInfo.InvariantCulture);

			if (double.TryParse(GetVal("TrainRaw", "NaN"), NumberStyles.Float, CultureInfo.InvariantCulture, out double storedTrainRaw))
				pars.storedTrainRaw = storedTrainRaw;

			pars.reCross3Percent = GetDouble("ReCross3Percent");

			pars.hiddenMean = GetDouble("HiddenMean");
			pars.hiddenMedian = GetDouble("HiddenMedian");
			pars.hiddenP90 = GetDouble("HiddenP90");
			pars.hiddenP95 = GetDouble("HiddenP95");
			pars.hiddenP99 = GetDouble("HiddenP99");
			pars.hiddenMax = GetDouble("HiddenMax");
			pars.hiddenRms = GetDouble("HiddenRMS");

			pars.outputMean = GetDouble("OutputMean");
			pars.outputMedian = GetDouble("OutputMedian");
			pars.outputP90 = GetDouble("OutputP90");
			pars.outputP95 = GetDouble("OutputP95");
			pars.outputP99 = GetDouble("OutputP99");
			pars.outputMax = GetDouble("OutputMax");
			pars.outputRms = GetDouble("OutputRMS");

			pars.orthoConfigKey = GetVal("ConfigKey", "");
			pars.orthoGroupModels = GetInt("GroupModels");
			pars.orthoBestBench = GetDouble("BestBench");
			pars.orthoBenchLossPercent = GetDouble("BenchLossPercent");
			pars.orthoBestBenchReCross3 = GetDouble("BestBenchReCross3");
			pars.orthoReCrossImprovementPoints = GetDouble("ReCrossImprovementPoints");
			pars.orthoNaturalCandidates = GetInt("NaturalCandidates");
			pars.orthoPoolAfterMinimum = GetInt("PoolAfterMinimum");
			pars.orthoBenchRank = GetInt("BenchRank");
			pars.orthoReCrossRank = GetInt("ReCrossRank");
			pars.orthoSelectionOrder = GetInt("SelectionOrder");
			pars.orthoTopModelsLimit = GetInt("TopModelsLimit");
			pars.orthoFinalBenchRankWithinW = GetInt("FinalBenchRankWithinW");

			pars.paramX4 = int.Parse(GetVal("Param_X4", paramX4.ToString()), CultureInfo.InvariantCulture);
			pars.paramX4Smooth = int.Parse(GetVal("Param_X4/1", "8"), CultureInfo.InvariantCulture);
			pars.paramX6 = int.Parse(GetVal("Param_X6", paramX6.ToString()), CultureInfo.InvariantCulture);
			pars.paramX6Down = int.Parse(GetVal("Param_X6/1", pars.paramX6.ToString()), CultureInfo.InvariantCulture);
			pars.paramX7 = int.TryParse(GetVal("Param_X7", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedParamX7) && parsedParamX7 > 0 ? parsedParamX7 : 7;
			pars.paramX7Plus = int.TryParse(GetVal("Param_X7/1", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedParamX7Plus) && parsedParamX7Plus > 0 ? parsedParamX7Plus : 14;
			pars.paramX8 = int.Parse(GetVal("Param_X8", paramX8.ToString()), CultureInfo.InvariantCulture);
			pars.paramX9 = int.Parse(GetVal("Param_X9", paramX9.ToString()), CultureInfo.InvariantCulture);
			pars.paramX10 = int.Parse(GetVal("Param_X10", paramX10.ToString()), CultureInfo.InvariantCulture);
			pars.paramX11 = int.Parse(GetVal("Param_X11", paramX11.ToString()), CultureInfo.InvariantCulture);
			pars.paramX12 = int.Parse(GetVal("Param_X12", paramX12.ToString()), CultureInfo.InvariantCulture);
			pars.paramX13 = int.Parse(GetVal("Param_X13", paramX13.ToString()), CultureInfo.InvariantCulture);
			// Старые пакеты не содержат X13/1 и X13/2: период 3 и направленный
			// режим точно воспроизводят прежний расчет Jocker.
			pars.paramX13HiLoPeriod = int.Parse(GetVal("Param_X13/1", "3"), CultureInfo.InvariantCulture);
			pars.paramX13Mode = int.Parse(GetVal("Param_X13/2", "0"), CultureInfo.InvariantCulture);
			if (pars.paramX13Mode < 0 || pars.paramX13Mode > 1)
				throw new InvalidDataException(
					$"Модель {Path.GetFileName(iniFilePath)} создана с удаленным режимом X13/2={pars.paramX13Mode}. " +
					"Текущая версия поддерживает только режимы 0 (направленный) и 1 (sqrt); пакет нужно пересоздать.");
			pars.paramX14 = int.Parse(GetVal("Param_X14", paramX14.ToString()), CultureInfo.InvariantCulture);
			pars.paramX14MaPeriod = int.Parse(GetVal("Param_X14/1", paramX14MaPeriod.ToString()), CultureInfo.InvariantCulture);
			int legacyMarketBreadthEtfVariant = int.Parse(GetVal("MarketBreadthEtfVariant", "0"), CultureInfo.InvariantCulture);
			if (legacyMarketBreadthEtfVariant != 0)
				throw new InvalidDataException($"Модель {Path.GetFileName(iniFilePath)} создана экспериментом с расширенной ETF-корзиной X14. Чистая версия поддерживает только SPY, XLV, XLU, XLE и XLP; пакет нужно пересоздать.");

			return pars;
		}
		#endregion

		#region Read Variable Usage From INI (ТОЛЬКО для аналитики!)

		private bool[] ReadVariableUsageFromIni(string iniFilePath)
		{
			bool[] usage = new bool[15];

			if (!File.Exists(iniFilePath))
				return usage; // все false

			var iniParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var lines = File.ReadAllLines(iniFilePath);

			foreach (var rawLine in lines)
			{
				string line = rawLine.Trim();
				if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[")) continue;
				int eqPos = line.IndexOf('=');
				if (eqPos <= 0) continue;
				string key = line.Substring(0, eqPos).Trim();
				string value = line.Substring(eqPos + 1).Trim();
				iniParams[key] = value;
			}

			const double threshold = 0.001;

			// Читаем x1-x15 ТОЛЬКО для аналитики
			if (iniParams.TryGetValue("x1", out string x1))
				usage[0] = Math.Abs(double.Parse(x1, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x2", out string x2))
				usage[1] = Math.Abs(double.Parse(x2, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x3", out string x3))
				usage[2] = Math.Abs(double.Parse(x3, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x4", out string x4))
				usage[3] = Math.Abs(double.Parse(x4, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x5", out string x5))
				usage[4] = Math.Abs(double.Parse(x5, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x6", out string x6))
				usage[5] = Math.Abs(double.Parse(x6, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x7", out string x7))
				usage[6] = Math.Abs(double.Parse(x7, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x8", out string x8))
				usage[7] = Math.Abs(double.Parse(x8, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x9", out string x9))
				usage[8] = Math.Abs(double.Parse(x9, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x10", out string x10))
				usage[9] = Math.Abs(double.Parse(x10, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x11", out string x11))
				usage[10] = Math.Abs(double.Parse(x11, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x12", out string x12))
				usage[11] = Math.Abs(double.Parse(x12, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x13", out string x13))
				usage[12] = Math.Abs(double.Parse(x13, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x14", out string x14))
				usage[13] = Math.Abs(double.Parse(x14, CultureInfo.InvariantCulture)) > threshold;
			if (iniParams.TryGetValue("x15", out string x15))
				usage[14] = Math.Abs(double.Parse(x15, CultureInfo.InvariantCulture)) > threshold;

			return usage;
		}

		private static double ParseBenchForPairAnalytics(string fileName)
		{
			Match match = Regex.Match(fileName, @"(?:^|_)Bench\s*=?\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
			if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double bench))
				return bench;
			return double.NaN;
		}

		private void BuildPairSelectionAndSynergyAnalytics()
		{
			if (modelPairAnalyticsRecords == null || modelPairAnalyticsRecords.Count == 0)
				return;

			BeginResearchReport("pair_feature_synergy");
			int total = modelPairAnalyticsRecords.Count;
			WriteToDebugLog("", false);
			WriteToDebugLog("════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════", false);
			WriteToDebugLog("│ НОВАЯ ПАРНАЯ АНАЛИТИКА: ЧЕТЫРЕ СОСТОЯНИЯ, LIFT, PHI/MCC, ВЗАИМНАЯ ИНФОРМАЦИЯ И СИНЕРГИЯ BENCH                    │", false);
			WriteToDebugLog("════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════", false);
			WriteToDebugLog($"Моделей в расчёте: {total}. MI измеряется в битах. Bench в состояниях — среднее арифметическое.", false);
			WriteToDebugLog("┌─────────┬──────────────┬──────────────┬──────────────┬──────────────┬─────────┬─────────┬─────────┐", false);
			WriteToDebugLog("│ Пара    │ Оба n/%      │ Только Xi    │ Только Xj    │ Ни одного   │ Lift    │ Phi/MCC │ MI, бит │", false);
			WriteToDebugLog("├─────────┼──────────────┼──────────────┼──────────────┼──────────────┼─────────┼─────────┼─────────┤", false);

			var synergyRows = new List<(int I, int J, double B11, int C11, double B10, int C10, double B01, int C01, double B00, int C00, double Synergy)>();

			for (int i = 0; i < 15; i++)
			{
				for (int j = i + 1; j < 15; j++)
				{
					int n11 = 0, n10 = 0, n01 = 0, n00 = 0;
					double sum11 = 0, sum10 = 0, sum01 = 0, sum00 = 0;
					int benchCount11 = 0, benchCount10 = 0, benchCount01 = 0, benchCount00 = 0;

					foreach (ModelPairAnalyticsRecord record in modelPairAnalyticsRecords)
					{
						bool a = record.Usage != null && record.Usage.Length > i && record.Usage[i];
						bool b = record.Usage != null && record.Usage.Length > j && record.Usage[j];
						bool hasBench = !double.IsNaN(record.Bench) && !double.IsInfinity(record.Bench);

						if (a && b)
						{
							n11++;
							if (hasBench) { sum11 += record.Bench; benchCount11++; }
						}
						else if (a)
						{
							n10++;
							if (hasBench) { sum10 += record.Bench; benchCount10++; }
						}
						else if (b)
						{
							n01++;
							if (hasBench) { sum01 += record.Bench; benchCount01++; }
						}
						else
						{
							n00++;
							if (hasBench) { sum00 += record.Bench; benchCount00++; }
						}
					}

					double pA = (double)(n11 + n10) / total;
					double pB = (double)(n11 + n01) / total;
					double p11 = (double)n11 / total;
					double lift = pA > 0 && pB > 0 ? p11 / (pA * pB) : double.NaN;

					double phiDenominator = Math.Sqrt((double)(n11 + n10) * (n01 + n00) * (n11 + n01) * (n10 + n00));
					double phi = phiDenominator > 0 ? ((double)n11 * n00 - (double)n10 * n01) / phiDenominator : double.NaN;
					double mutualInformation =
						MutualInformationTerm(n11, n11 + n10, n11 + n01, total) +
						MutualInformationTerm(n10, n11 + n10, n10 + n00, total) +
						MutualInformationTerm(n01, n01 + n00, n11 + n01, total) +
						MutualInformationTerm(n00, n01 + n00, n10 + n00, total);

					string pair = $"x{i + 1}-x{j + 1}";
					WriteToDebugLog($"│ {pair,-7} │ {FormatState(n11, total),-12} │ {FormatState(n10, total),-12} │ {FormatState(n01, total),-12} │ {FormatState(n00, total),-12} │ {FormatMetric(lift),7} │ {FormatMetric(phi),7} │ {FormatMetric(mutualInformation),7} │", false);

					double b11 = MeanOrNaN(sum11, benchCount11);
					double b10 = MeanOrNaN(sum10, benchCount10);
					double b01 = MeanOrNaN(sum01, benchCount01);
					double b00 = MeanOrNaN(sum00, benchCount00);
					double synergy = AreFinite(b11, b10, b01, b00) ? b11 - b10 - b01 + b00 : double.NaN;
					synergyRows.Add((i, j, b11, benchCount11, b10, benchCount10, b01, benchCount01, b00, benchCount00, synergy));
				}
			}

			WriteToDebugLog("└─────────┴──────────────┴──────────────┴──────────────┴──────────────┴─────────┴─────────┴─────────┘", false);
			WriteToDebugLog("", false);
			WriteToDebugLog("┌─────────┬────────────────┬────────────────┬────────────────┬────────────────┬────────────┐", false);
			WriteToDebugLog("│ Пара    │ Bench оба (n)  │ Только Xi (n)  │ Только Xj (n)  │ Ни одного (n) │ Synergy    │", false);
			WriteToDebugLog("├─────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────┤", false);

			foreach (var row in synergyRows)
			{
				string pair = $"x{row.I + 1}-x{row.J + 1}";
				WriteToDebugLog($"│ {pair,-7} │ {FormatBenchState(row.B11, row.C11),-14} │ {FormatBenchState(row.B10, row.C10),-14} │ {FormatBenchState(row.B01, row.C01),-14} │ {FormatBenchState(row.B00, row.C00),-14} │ {FormatMetric(row.Synergy, "F2"),10} │", false);
			}

			WriteToDebugLog("└─────────┴────────────────┴────────────────┴────────────────┴────────────────┴────────────┘", false);
			EndResearchReport();
		}

		private static double MutualInformationTerm(int cellCount, int rowCount, int columnCount, int total)
		{
			if (cellCount <= 0 || rowCount <= 0 || columnCount <= 0 || total <= 0) return 0.0;
			double pCell = (double)cellCount / total;
			double pRow = (double)rowCount / total;
			double pColumn = (double)columnCount / total;
			return pCell * (Math.Log(pCell / (pRow * pColumn)) / Math.Log(2.0));
		}

		private static double MeanOrNaN(double sum, int count) => count > 0 ? sum / count : double.NaN;

		private static bool AreFinite(params double[] values) =>
			values.All(value => !double.IsNaN(value) && !double.IsInfinity(value));

		private static string FormatState(int count, int total) =>
			$"{count}/{((double)count / total * 100.0):F1}%";

		private static string FormatMetric(double value, string format = "F4") =>
			double.IsNaN(value) || double.IsInfinity(value) ? "N/A" : value.ToString(format, CultureInfo.InvariantCulture);

		private static string FormatBenchState(double mean, int count) =>
			double.IsNaN(mean) || double.IsInfinity(mean) ? "N/A" : $"{mean.ToString("F1", CultureInfo.InvariantCulture)} ({count})";

		#endregion

		#region Process Trading Package

		private ProcessResult ProcessTradingPackageCached(
			BarHistory bars,
			string packagePath,
			string outputFileName,
			string packageLabel,
			int expectedCalculationMethod)
		{
			bool includeTomorrowHistory = tradingPackageCalculationMethod != 4;
			string cacheKey = BuildTradingPackageCacheKey(
				bars,
				packagePath,
				expectedCalculationMethod,
				tomorrowBars,
				includeTomorrowHistory);
			var candidate = new Lazy<ProcessResult>(
				() => ProcessTradingPackage(bars, packagePath, outputFileName, packageLabel, expectedCalculationMethod),
				true);
			Lazy<ProcessResult> cached = TradingPackageCache.GetOrAdd(cacheKey, candidate);
			bool cacheHit = !ReferenceEquals(candidate, cached);

			try
			{
				ProcessResult result = cached.Value;
				WriteToDebugLog(
					$"Trading Package {packageLabel}: {(cacheHit ? "CACHE HIT — готовые прогнозы" : "CACHE MISS — пакет рассчитан и сохранён") }.",
					false);
				return result;
			}
			catch
			{
				TradingPackageCache.TryRemove(cacheKey, out _);
				throw;
			}
		}

		private static string BuildTradingPackageCacheKey(
			BarHistory bars,
			string packagePath,
			int expectedCalculationMethod,
			int tomorrowBars,
			bool includeTomorrowHistory)
		{
			unchecked
			{
				const ulong offset = 14695981039346656037UL;
				const ulong prime = 1099511628211UL;
				ulong hash = offset;

				void MixLong(long value)
				{
					hash ^= (ulong)value;
					hash *= prime;
				}

				void MixString(string value)
				{
					foreach (char character in value ?? string.Empty)
					{
						hash ^= character;
						hash *= prime;
					}
				}

				MixString(bars.Symbol);
				MixLong(expectedCalculationMethod);
				MixLong(tomorrowBars);
				MixLong(includeTomorrowHistory ? 1 : 0);
				MixLong(bars.Count);
				for (int bar = 0; bar < bars.Count; bar++)
				{
					MixLong(bars.DateTimes[bar].Ticks);
					MixLong(BitConverter.DoubleToInt64Bits(bars.Open[bar]));
					MixLong(BitConverter.DoubleToInt64Bits(bars.High[bar]));
					MixLong(BitConverter.DoubleToInt64Bits(bars.Low[bar]));
					MixLong(BitConverter.DoubleToInt64Bits(bars.Close[bar]));
				}

				string fullPackagePath = Path.GetFullPath(packagePath);
				MixString(fullPackagePath);
				if (Directory.Exists(fullPackagePath))
				{
					IEnumerable<string> files = Directory.EnumerateFiles(fullPackagePath)
						.Where(path => path.EndsWith(".jbin", StringComparison.OrdinalIgnoreCase) ||
							path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
						.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
					foreach (string file in files)
					{
						FileInfo info = new FileInfo(file);
						MixString(info.Name);
						MixLong(info.Length);
						MixLong(info.LastWriteTimeUtc.Ticks);
					}
				}

				return $"{fullPackagePath}|{expectedCalculationMethod}|T{tomorrowBars}|H{(includeTomorrowHistory ? 1 : 0)}|{bars.Count}|{hash:X16}";
			}
		}

		private ProcessResult ProcessTradingPackage(
			BarHistory bars,
			string packagePath,
			string outputFileName,
			string packageLabel,
			int expectedCalculationMethod)
		{
			if (!Directory.Exists(packagePath))
			{
				throw new DirectoryNotFoundException(
					$"Торговый пакет {packageLabel} не найден: {packagePath}. " +
					"Проторговка использует только заранее сформированные пакеты ZZ1/ZZ2/ZZ3.");
			}

			string[] modelFiles = Directory.GetFiles(packagePath, "*" + ActiveModelExtension);
			Array.Sort(modelFiles, StringComparer.OrdinalIgnoreCase);

			if (modelFiles.Length == 0)
			{
				throw new InvalidOperationException($"В торговом пакете {packageLabel} нет {ActiveModelExtension}-файлов: {packagePath}");
			}

			int successfullyLoadedModelCount = 0;
			TimeSeries local_aggregated_ensemble = new TimeSeries(bars.DateTimes, 0);
			TimeSeries local_aggregated_ensemble_RAW = new TimeSeries(bars.DateTimes, 0.0);

			double sumForecast = 0, sumLevelClass = 0;
			int countForecast = 0, countLevelClass = 0;

			var modelsData = new List<(string FileName, double BenchValue, TimeSeries ForecastTS)>();
			var packageVariableUsage = new List<bool[]>();
			var packagePairAnalyticsRecords = new List<ModelPairAnalyticsRecord>();
			TimeSeries packageTarget = null;
			string packageTargetDescription = null;
			int recordNumber = 1;
			int lastModelStartIndex = StartIndex;
			List<string> configFileReportLines = new List<string>();
			bool includeTomorrowHistory = tradingPackageCalculationMethod != 4;
			double[] tomorrowClassifiedCounts = new double[5];
			TimeSeries[] tomorrowHistoryCounts = new TimeSeries[5];
			for (int scenario = 0; scenario < tomorrowHistoryCounts.Length; scenario++)
				tomorrowHistoryCounts[scenario] = new TimeSeries(bars.DateTimes, 0.0);
			int tomorrowModelCount = 0;

			BeginResearchReport($"loaded_models_{packageLabel}");
			WriteToDebugLog("┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
			WriteToDebugLog($"│                                               ОТЧЁТ ПО ЗАГРУЖЕННЫМ МОДЕЛЯМ ({packageLabel})                                                          │", false);
			WriteToDebugLog("├────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
			WriteToDebugLog("│ №   │ Файл модели                                                         │ Прогноз │Уровень│0/1│   Дата    │", false);

			foreach (string modelFile in modelFiles)
			{
				string ptFileName = Path.GetFileName(modelFile);
				string iniFile = Path.ChangeExtension(modelFile, ".ini");

				// === ЗАГРУЗКА ИНДИВИДУАЛЬНЫХ ПАРАМЕТРОВ ===
				ModelParameters modelPars = LoadIndividualModelParameters(iniFile, ptFileName);

				if (modelPars == null)
				{
					continue;
				}

				if (modelPars.calculationMethod != expectedCalculationMethod)
				{
					WriteToDebugLog(
						$"  [ПРОПУСК] {ptFileName}: INI содержит CalculationMethod={modelPars.calculationMethod}, " +
						$"а выбран пакет ZZ{expectedCalculationMethod}.",
						false);
					continue;
				}

				if (expectedCalculationMethod == 3 && packageTarget == null)
				{
					packageTarget = CalculateTargetForModel(bars, modelPars);
					packageTargetDescription =
						$"ZZ3 ATRP Predict 0/1 P{modelPars.targetAtrpPeriod} H{modelPars.targetAtrpShift} " +
						$"K{modelPars.targetAtrpThreshold.ToString("F2", CultureInfo.InvariantCulture)}";
				}

				// === ЧТЕНИЕ x1-x15 ИЗ INI ТОЛЬКО ДЛЯ АНАЛИТИКИ ===
				bool[] currentUsage = ReadVariableUsageFromIni(iniFile);
				packageVariableUsage.Add(currentUsage);
				tomorrowModelCount++;

				// Пересчет predict для этой модели (если CalculationMethod отличается от глобального)
				if (modelPars.calculationMethod != calculationMethod)
				{
					int oldCalcMethod = calculationMethod;
					int oldShift = shift;
					int oldSmoothingPeriod = smoothingPeriod;
					int oldTargetAtrpShift = targetAtrpShift;
					double oldTargetAtrpThreshold = targetAtrpThreshold;
					double oldReversalPercentage = reversalPercentage;

					calculationMethod = modelPars.calculationMethod;
					shift = modelPars.shift;
					smoothingPeriod = modelPars.smoothingPeriod;
					targetAtrpShift = modelPars.targetAtrpShift;
					targetAtrpThreshold = modelPars.targetAtrpThreshold;
					reversalPercentage = modelPars.reversalPercentage;

					switch (modelPars.calculationMethod)
					{
						case 1: CalculatePredictWithShift(bars); break;
						case 2: CalculatePredictWithPeakTrough(bars); break;
						case 3: CalculatePredictWithAtrpImpulse(bars, (bars.Close + bars.High + bars.Low) / 3); break;
					}

					calculationMethod = oldCalcMethod;
					shift = oldShift;
					smoothingPeriod = oldSmoothingPeriod;
					targetAtrpShift = oldTargetAtrpShift;
					targetAtrpThreshold = oldTargetAtrpThreshold;
					reversalPercentage = oldReversalPercentage;
				}

				// Каждый накопленный файл модели восстанавливается со своими Param_X.
				CachedBaseIndicators currentGlobalCache = _cachedIndicators;
				CachedBaseIndicators modelIndicatorCache = GetOrCreateIndicatorsForModel(bars, modelPars);
				_cachedIndicators = modelIndicatorCache;
				Dictionary<string, TimeSeries> featureMap = BuildFeatureMapFromCache(bars, out int modelStartIndex);
				_cachedIndicators = currentGlobalCache;
				lastModelStartIndex = modelStartIndex;

				// Инференс
				TimeSeries forecastTS_RAW = RunJbinWithTomorrowInMemory(
					modelFile,
					bars,
					featureMap,
					modelStartIndex,
					modelPars,
					modelIndicatorCache,
					includeTomorrowHistory,
					tomorrowHistoryCounts,
					tomorrowClassifiedCounts);
				successfullyLoadedModelCount++;
				TimeSeries forecastTS = new TimeSeries(bars.DateTimes);

				// Классификация с индивидуальным level_class
				for (int i = 0; i < forecastTS_RAW.Count; i++)
				{
					forecastTS[i] = forecastTS_RAW[i] >= modelPars.level_class ? 1 : 0;
					local_aggregated_ensemble[i] += forecastTS[i];
					local_aggregated_ensemble_RAW[i] += forecastTS_RAW[i];
				}

				double benchFromFile = double.NaN;
				var match = Regex.Match(ptFileName, @"(?:^|_)Bench\s*=?\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
				if (match.Success)
					double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out benchFromFile);

				modelsData.Add((ptFileName, benchFromFile, forecastTS));
				packagePairAnalyticsRecords.Add(new ModelPairAnalyticsRecord
				{
					Usage = currentUsage,
					Bench = ParseBenchForPairAnalytics(ptFileName)
				});

				int lastIdx = bars.Count - 1;

				double forecastValue = double.NaN;
				if (bars.Count > 0 && forecastTS_RAW != null && forecastTS_RAW.Count > 0)
					forecastValue = forecastTS_RAW[lastIdx];

				if (!double.IsNaN(forecastValue)) { sumForecast += forecastValue; countForecast++; }
				if (!double.IsNaN(modelPars.level_class)) { sumLevelClass += modelPars.level_class; countLevelClass++; }

				if (!double.IsNaN(forecastValue))
				{
					bool isAbove = forecastValue >= modelPars.level_class;
					string classificationResult = isAbove ? "1" : "0";
					string dateStr = bars.DateTimes[lastIdx].ToString("yyyy-MM-dd");
					string numberStr = recordNumber.ToString("D3");

					string row = $"│ {numberStr,-3} │ {ptFileName,-35} │ {forecastValue,7:F4} │ {modelPars.level_class,5:F2} │ {classificationResult,1} │ {dateStr,10} │";
					configFileReportLines.Add(row);
					recordNumber++;
				}
			}

			if (successfullyLoadedModelCount == 0)
				throw new InvalidOperationException(
					$"Пакет {packageLabel} не содержит ни одной комплектной модели с CalculationMethod={expectedCalculationMethod}.");

			// Вывод итогов таблицы
			double avgForecast = countForecast > 0 ? sumForecast / countForecast : double.NaN;
			double avgLevelClass = countLevelClass > 0 ? sumLevelClass / countLevelClass : double.NaN;

			string itogRow = $"│ИТОГО│ {"",-66} │ {avgForecast,7:F4} │ {avgLevelClass,5:F2} │ {"",1} │ {"",10} │";
			WriteToDebugLog(itogRow, false);
			foreach (string line in configFileReportLines) WriteToDebugLog(line, false);
			WriteToDebugLog("└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘", false);
			EndResearchReport();

			// Сохранение ensemblePR для пакета
			try
			{
				TimeSeries localPR = new TimeSeries(bars.DateTimes, 0);
				if (successfullyLoadedModelCount > 0)
				{
					for (int i = 0; i < local_aggregated_ensemble.Count; i++)
						localPR[i] = (local_aggregated_ensemble[i] / successfullyLoadedModelCount) * 100.0;
				}

				string ensemblePRPath = Path.Combine(TorchSharpRootDir, outputFileName);
				using (StreamWriter writer = new StreamWriter(ensemblePRPath))
				{
					for (int i = StartIndex; i < localPR.Count; i++)
					{
						DateTime date = bars.DateTimes[i];
						double value = localPR[i];
						writer.WriteLine($"{date:yyyy-MM-dd},{value:F2}");
					}
				}
				WriteToDebugLog($"Ансамбль {packageLabel} сохранён в файл: {ensemblePRPath} (Успешно загружено моделей: {successfullyLoadedModelCount})", false);
			}
			catch (Exception ex)
			{
				WriteToDebugLog($"Ошибка при записи файла {outputFileName}: {ex.Message}", false);
			}

			return new ProcessResult
			{
				Ensemble = local_aggregated_ensemble,
				EnsembleRAW = local_aggregated_ensemble_RAW,
				TomorrowClassifiedCounts = tomorrowClassifiedCounts,
				TomorrowHistoryCounts = tomorrowHistoryCounts,
				TomorrowModelCount = tomorrowModelCount,
				Target = packageTarget,
				TargetDescription = packageTargetDescription,
				AllModels = modelsData,
				ModelCount = successfullyLoadedModelCount,
				LastModelStartIndex = lastModelStartIndex,
				VariableUsage = packageVariableUsage,
				PairAnalyticsRecords = packagePairAnalyticsRecords
			};
		}

		private TimeSeries CalculateTargetForModel(BarHistory bars, ModelParameters modelPars)
		{
			TimeSeries savedPredict = predict;
			int savedCalculationMethod = calculationMethod;
			int savedShift = shift;
			int savedSmoothingPeriod = smoothingPeriod;
			int savedTargetAtrpShift = targetAtrpShift;
			double savedTargetAtrpThreshold = targetAtrpThreshold;
			double savedReversalPercentage = reversalPercentage;

			try
			{
				calculationMethod = modelPars.calculationMethod;
				shift = modelPars.shift;
				smoothingPeriod = modelPars.smoothingPeriod;
				targetAtrpShift = modelPars.targetAtrpShift;
				targetAtrpThreshold = modelPars.targetAtrpThreshold;
				reversalPercentage = modelPars.reversalPercentage;

				switch (calculationMethod)
				{
					case 1: CalculatePredictWithShift(bars); break;
					case 2: CalculatePredictWithPeakTrough(bars); break;
					case 3: CalculatePredictWithAtrpImpulse(bars, (bars.Close + bars.High + bars.Low) / 3); break;
					default: throw new ArgumentOutOfRangeException(nameof(modelPars.calculationMethod));
				}
				return predict;
			}

			finally
			{
				predict = savedPredict;
				calculationMethod = savedCalculationMethod;
				shift = savedShift;
				smoothingPeriod = savedSmoothingPeriod;
				targetAtrpShift = savedTargetAtrpShift;
				targetAtrpThreshold = savedTargetAtrpThreshold;
				reversalPercentage = savedReversalPercentage;
			}
		}

		#endregion

		#region Jordan/Elman In-Memory Inference
		private TimeSeries RunJbinInMemory(string modelFilePath, BarHistory bars, Dictionary<string, TimeSeries> featureMap, int modelStartIndex, ModelParameters modelPars)
		{
			TimeSeries resultSeries = new TimeSeries(bars.DateTimes, double.NaN);

			try
			{
				JbinModel model = JbinModel.Load(modelFilePath);

				if (model.InputSize != ActiveFeatureNames.Count)
					throw new InvalidDataException($".jbin ожидает {model.InputSize} входов, а INI задаёт {ActiveFeatureNames.Count}.");

				if (modelPars.networkType is >= 1 and <= 4 && model.Architecture != modelPars.networkType)
					throw new InvalidDataException($"Архитектура .jbin ({model.Architecture}) не совпадает с network_type={modelPars.networkType}.");

				if (model.Architecture != 1 && modelPars.contextMode is >= 1 and <= 3 && model.ContextMode != modelPars.contextMode)
					throw new InvalidDataException($"Контекст .jbin ({model.ContextMode}) не совпадает с context_mode={modelPars.contextMode}.");

				var input = new float[model.InputSize];

				for (int i = modelStartIndex; i < bars.Count; i++)
				{
					for (int j = 0; j < input.Length; j++)
					{
						double value = featureMap[ActiveFeatureNames[j]][i];
						input[j] = double.IsFinite(value) ? (float)value : 0f;
					}

					float prediction = model.Forward(input);

					if (!float.IsFinite(prediction))
						throw new InvalidDataException($"Неконечный прогноз на позиции {i - modelStartIndex}: {prediction}.");

					resultSeries[i] = prediction;
				}
			}
			catch (Exception ex)
			{
				string message = $"Ошибка .jbin-инференса модели {Path.GetFileName(modelFilePath)}: {ex.Message}";
				WriteToDebugLog(message, false);
				throw new InvalidDataException(message, ex);
			}

			return resultSeries;
		}

		private TimeSeries RunJbinWithTomorrowInMemory(
			string modelFilePath,
			BarHistory bars,
			Dictionary<string, TimeSeries> featureMap,
			int modelStartIndex,
			ModelParameters modelPars,
			CachedBaseIndicators modelIndicatorCache,
			bool includeTomorrowHistory,
			TimeSeries[] tomorrowHistoryCounts,
			double[] tomorrowClassifiedCounts)
		{
			TimeSeries resultSeries = new TimeSeries(bars.DateTimes, double.NaN);

			try
			{
				JbinModel model = JbinModel.Load(modelFilePath);
				ValidateJbinModel(model, modelPars);

				Dictionary<string, double>[][] tomorrowFeatures =
					GetOrCreateTomorrowFeatureValues(bars, modelPars, modelIndicatorCache, includeTomorrowHistory);
				int firstHistoricalOrigin = includeTomorrowHistory
					? Math.Max(modelStartIndex, bars.Count - tomorrowBars - 1)
					: bars.Count;
				var input = new float[model.InputSize];

				for (int bar = modelStartIndex; bar < bars.Count; bar++)
				{
					FillModelInput(input, featureMap, bar);
					float prediction = model.Forward(input);
					if (!float.IsFinite(prediction))
						throw new InvalidDataException($"Неконечный прогноз на позиции {bar - modelStartIndex}: {prediction}.");
					resultSeries[bar] = prediction;

					bool isHistoricalOrigin = includeTomorrowHistory && bar >= firstHistoricalOrigin && bar <= bars.Count - 2;
					bool isCurrentOrigin = bar == bars.Count - 1;
					if ((!isHistoricalOrigin && !isCurrentOrigin) || tomorrowFeatures[bar] == null)
						continue;

					(float[] hidden, float output) stateAfterRealBar = model.GetState();
					for (int scenario = 0; scenario < 5; scenario++)
					{
						model.SetState(stateAfterRealBar.hidden, stateAfterRealBar.output);
						FillModelInput(input, tomorrowFeatures[bar][scenario]);
						float tomorrowPrediction = model.Forward(input);
						if (!float.IsFinite(tomorrowPrediction))
							throw new InvalidDataException($"Неконечный Tomorrow-прогноз: bar={bar}, scenario={scenario}.");

						if (tomorrowPrediction >= modelPars.level_class)
						{
							if (isCurrentOrigin)
								tomorrowClassifiedCounts[scenario] += 1.0;
							else
								tomorrowHistoryCounts[scenario][bar + 1] += 1.0;
						}
					}

					// Ни одна сценарная ветвь не должна изменять реальную рекуррентную историю.
					model.SetState(stateAfterRealBar.hidden, stateAfterRealBar.output);
				}
			}
			catch (Exception ex)
			{
				string message = $"Ошибка .jbin/Futures-инференса модели {Path.GetFileName(modelFilePath)}: {ex.Message}";
				WriteToDebugLog(message, false);
				throw new InvalidDataException(message, ex);
			}

			return resultSeries;
		}

		private void ValidateJbinModel(JbinModel model, ModelParameters modelPars)
		{
			if (model.InputSize != ActiveFeatureNames.Count)
				throw new InvalidDataException($".jbin ожидает {model.InputSize} входов, а стратегия задаёт {ActiveFeatureNames.Count}.");
			if (modelPars.networkType is >= 1 and <= 4 && model.Architecture != modelPars.networkType)
				throw new InvalidDataException($"Архитектура .jbin ({model.Architecture}) не совпадает с network_type={modelPars.networkType}.");
			if (model.Architecture != 1 && modelPars.contextMode is >= 1 and <= 3 && model.ContextMode != modelPars.contextMode)
				throw new InvalidDataException($"Контекст .jbin ({model.ContextMode}) не совпадает с context_mode={modelPars.contextMode}.");
		}

		private void FillModelInput(float[] input, Dictionary<string, TimeSeries> featureMap, int bar)
		{
			for (int feature = 0; feature < input.Length; feature++)
			{
				double value = featureMap[ActiveFeatureNames[feature]][bar];
				input[feature] = double.IsFinite(value) ? (float)value : 0f;
			}
		}

		private void FillModelInput(float[] input, Dictionary<string, double> featureValues)
		{
			for (int feature = 0; feature < input.Length; feature++)
			{
				double value = featureValues.TryGetValue(ActiveFeatureNames[feature], out double found) ? found : 0.0;
				input[feature] = double.IsFinite(value) ? (float)value : 0f;
			}
		}
		#endregion

		#region Build Feature Map From Cache

		private Dictionary<string, TimeSeries> BuildFeatureMapFromCache(BarHistory bars, out int modelStartIndex)
		{
			modelStartIndex = _cachedIndicators.modelStartIndex;
			Dictionary<string, TimeSeries> map = new Dictionary<string, TimeSeries>(StringComparer.OrdinalIgnoreCase);

			map["model_cnn_fear_greed"] = _cachedIndicators.model_cnn_fear_greed_base;
			map["risk_measure"] = _cachedIndicators.risk_measure_base;
			map["model_panic_euphoria"] = _cachedIndicators.model_panic_euphoria_base;
			map["myRSI_rep"] = _cachedIndicators.myRSI_rep_base;
			map["trendScore"] = _cachedIndicators.trendScore_base;
			map["aroonUp"] = _cachedIndicators.aroonUp_base;
			map["aroonDown"] = _cachedIndicators.aroonDown_base;
			map["dIMinus"] = _cachedIndicators.dIMinus_base;
			map["dIPlus"] = _cachedIndicators.dIPlus_base;
			map["correlationAngle_rep"] = _cachedIndicators.correlationAngle_rep_base;
			map["correlationTrend"] = _cachedIndicators.correlationTrend_base;
			map["hurst"] = _cachedIndicators.hurst_base;
			map["cmf"] = _cachedIndicators.cmf_base;
			map["decibel"] = _cachedIndicators.decibel_base;
			map["jocker"] = _cachedIndicators.jocker_base;
			map["marketBreadth"] = _cachedIndicators.marketBreadth_base;

			return map;
		}

		#endregion

		#region Precompute Base Indicators
		private CachedBaseIndicators GetOrCreateIndicatorsForModel(BarHistory bars, ModelParameters modelPars)
		{
			string key = $"{modelPars.inputGradationLevels}|{modelPars.validationWithoutGradation}|{modelPars.paramX4}|{modelPars.paramX4Smooth}|{modelPars.paramX6}|{modelPars.paramX6Down}|{modelPars.paramX7}|{modelPars.paramX7Plus}|{modelPars.paramX8}|{modelPars.paramX9}|{modelPars.paramX10}|{modelPars.paramX11}|{modelPars.paramX12}|{modelPars.paramX13}|{modelPars.paramX13HiLoPeriod}|{modelPars.paramX13Mode}|{modelPars.paramX14}|{modelPars.paramX14MaPeriod}";

			if (_indicatorCacheByParameters.TryGetValue(key, out CachedBaseIndicators cached)) return cached;

			int oldX4 = paramX4, oldX4Smooth = paramX4Smooth, oldX6 = paramX6, oldX6Down = paramX6Down, oldX7 = paramX7, oldX7Plus = paramX7Plus, oldX8 = paramX8, oldX9 = paramX9, oldX10 = paramX10;
			int oldX11 = paramX11, oldX12 = paramX12, oldX13 = paramX13, oldX13HiLoPeriod = paramX13HiLoPeriod, oldX13Mode = paramX13Mode, oldX14 = paramX14, oldX14MaPeriod = paramX14MaPeriod;
			try
			{
				paramX4 = modelPars.paramX4;
				paramX4Smooth = modelPars.paramX4Smooth;
				paramX6 = modelPars.paramX6;
				paramX6Down = modelPars.paramX6Down;
				paramX7 = modelPars.paramX7;
				paramX7Plus = modelPars.paramX7Plus;
				paramX8 = modelPars.paramX8;
				paramX9 = modelPars.paramX9;
				paramX10 = modelPars.paramX10;
				paramX11 = modelPars.paramX11;
				paramX12 = modelPars.paramX12;
				paramX13 = modelPars.paramX13;
				paramX13HiLoPeriod = modelPars.paramX13HiLoPeriod;
				paramX13Mode = modelPars.paramX13Mode;
				paramX14 = modelPars.paramX14;
				paramX14MaPeriod = modelPars.paramX14MaPeriod;
				// При инференсе подаём исходные признаки без множителей и масок.
				// Неактивные входы уже обнулены весами в сохранённой .jbin-модели.
				cached = PrecomputeBaseIndicators(bars, new double[]
				{
					1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1
				}, modelPars.inputGradationLevels, modelPars.validationWithoutGradation);

				_indicatorCacheByParameters[key] = cached;
				return cached;
			}
			finally
			{
				paramX4 = oldX4; paramX4Smooth = oldX4Smooth; paramX6 = oldX6; paramX6Down = oldX6Down; paramX7 = oldX7; paramX7Plus = oldX7Plus; paramX8 = oldX8; paramX9 = oldX9; paramX10 = oldX10;
				paramX11 = oldX11; paramX12 = oldX12; paramX13 = oldX13; paramX13HiLoPeriod = oldX13HiLoPeriod; paramX13Mode = oldX13Mode; paramX14 = oldX14; paramX14MaPeriod = oldX14MaPeriod;
			}
		}

		private CachedBaseIndicators PrecomputeBaseIndicators(BarHistory bars, double[] switches, int gradationLevels, int validationRawMode)
		{
			var cached = new CachedBaseIndicators();

			int maxStartIndex = 175;
			List<IndicatorBase> localStartIndexList = new List<IndicatorBase>();

			double x1 = switches[0], x2 = switches[1], x3 = switches[2], x4 = switches[3], x5 = switches[4];
			double x6 = switches[5], x7 = switches[6], x8 = switches[7], x9 = switches[8], x10 = switches[9];
			double x11 = switches[10], x12 = switches[11], x13 = switches[12], x14 = switches[13];

			BarHistory model_cnn_fear_greed_hist = GetHistory(bars, "MODEL_CNN_FEAR_GREED");
			SMA model_cnn_fear_greedTMP = SMA.Series(model_cnn_fear_greed_hist.Close, 1);
			cached.model_cnn_fear_greed_base = x1 * (model_cnn_fear_greedTMP - 50) / 50;

			BarHistory risk_measure_hist = GetHistory(bars, "RISK_MEASURE");
			SMA risk_measureTMP = SMA.Series(risk_measure_hist.Close, 1);
			cached.risk_measure_base = x2 * (risk_measureTMP - 50) / 50;

			BarHistory model_panic_euphoria_hist = GetHistory(bars, "MODEL_PANIC_EUPHORIA_DAY");
			SMA model_panic_euphoriaTMP = SMA.Series(model_panic_euphoria_hist.Close, 1);
			cached.model_panic_euphoria_base = x3 * (model_panic_euphoriaTMP - 0.5) / 1;

			MyRSI_rep myRSI_repTmp = new MyRSI_rep(bars.Close, paramX4, paramX4Smooth);
			localStartIndexList.Add(myRSI_repTmp);
			cached.myRSI_rep_base = x4 * myRSI_repTmp;

			TrendScore trendScoreTmp = new TrendScore(bars);
			localStartIndexList.Add(trendScoreTmp);
			cached.trendScore_base = x5 * trendScoreTmp * 0.1;

			AroonUp aroonUpTmp = new AroonUp(bars.Close, paramX6);
			localStartIndexList.Add(aroonUpTmp);
			cached.aroonUp_base = x6 * (-50 + aroonUpTmp) / 50;

			AroonDown aroonDownTmp = new AroonDown(bars.Close, paramX6Down);
			localStartIndexList.Add(aroonDownTmp);
			cached.aroonDown_base = x6 * (-50 + aroonDownTmp) / 50;

			DIMinus dIMinusTmp = new DIMinus(bars, paramX7);
			TimeSeries dIMinusW = (-30 + dIMinusTmp) / 30;
			cached.dIMinus_base = x7 * AGC(dIMinusW);

			DIPlus dIPlusTmp = new DIPlus(bars, paramX7Plus);
			TimeSeries dIPlusW = (-25 + dIPlusTmp) / 30;
			cached.dIPlus_base = x7 * AGC(dIPlusW);

			CorrelationAngle_rep correlationAngle_repTmp = new CorrelationAngle_rep(bars.Close, paramX8);
			localStartIndexList.Add(correlationAngle_repTmp);
			cached.correlationAngle_rep_base = x8 * (-0 + correlationAngle_repTmp) / 180;

			IndicatorBase correlationTrendTmp = new CorrelationTrend(bars.Close, paramX9);
			localStartIndexList.Add(correlationTrendTmp);
			cached.correlationTrend_base = x9 * AGC(correlationTrendTmp) / 1;

			Hurst hurstTmpBase = new Hurst(bars.Close, paramX10);
			EhlersInverseFisherTransform_gpt hurstTmp = new EhlersInverseFisherTransform_gpt(hurstTmpBase, 3, 1);
			cached.hurst_base = 7 * x10 * (hurstTmp - 0.6);

			CMF cmfBase = new CMF(bars, paramX11);
			EhlersInverseFisherTransform_gpt cmfTmp = new EhlersInverseFisherTransform_gpt(cmfBase, 3, 10);
			localStartIndexList.Add(cmfTmp);
			cached.cmf_base = x11 * (cmfTmp - 0.1);

			Decibel decibelBase = new Decibel(bars.Close, paramX12);
			EhlersInverseFisherTransform_gpt decibelTmp = new EhlersInverseFisherTransform_gpt(decibelBase, 8, 0.1);
			localStartIndexList.Add(decibelTmp);
			cached.decibel_base = x12 * (decibelTmp + 0.55);

			// === Логика индикатора Jocker перенесена из первой программы (Nasdaq) ===
			BarHistory nasdaqNewHighsBars = GetHistory(bars, "$NASDAQ_NEWHI");
			BarHistory nasdaqNewLowsBars = GetHistory(bars, "$NASDAQ_NEWLO");

			if (nasdaqNewHighsBars != null && nasdaqNewLowsBars != null)
			{
				TimeSeries nasdaqNewHighs = nasdaqNewHighsBars.Close;
				TimeSeries nasdaqNewLows = nasdaqNewLowsBars.Close;

				TimeSeries hiloLogic = new TimeSeries(bars.DateTimes, 0);

				for (int i = 0; i < bars.Count; i++)
				{
					double sumHighs = 0, sumLows = 0;

					for (int j = 0; j < paramX13HiLoPeriod && j <= i; j++)
					{
						sumHighs += nasdaqNewHighs[i - j];
						sumLows += nasdaqNewLows[i - j];
					}

					double sum = sumHighs + sumLows;
					hiloLogic[i] = sum > 0
						? CalculateNormalizedX13(sumHighs, sumLows, sum, paramX13Mode)
						: 0;
				}

				cached.jocker_base = x13 * EMA.Series(hiloLogic, paramX13) / 1.0;
			}
			else
			{
				cached.jocker_base = new TimeSeries(bars.DateTimes, 0.0);
			}

			cached.marketBreadth_base = x14 * CalculateMarketBreadthBase(bars);

			if (gradationLevels > 0)
			{
				cached.model_cnn_fear_greed_base = QuantizeInputSeries(bars, cached.model_cnn_fear_greed_base, gradationLevels, x1, validationRawMode);
				cached.risk_measure_base = QuantizeInputSeries(bars, cached.risk_measure_base, gradationLevels, x2, validationRawMode);
				cached.model_panic_euphoria_base = QuantizeInputSeries(bars, cached.model_panic_euphoria_base, gradationLevels, x3, validationRawMode);
				cached.myRSI_rep_base = QuantizeInputSeries(bars, cached.myRSI_rep_base, gradationLevels, x4, validationRawMode);
				cached.trendScore_base = QuantizeInputSeries(bars, cached.trendScore_base, gradationLevels, x5, validationRawMode);
				cached.aroonUp_base = QuantizeInputSeries(bars, cached.aroonUp_base, gradationLevels, x6, validationRawMode);
				cached.aroonDown_base = QuantizeInputSeries(bars, cached.aroonDown_base, gradationLevels, x6, validationRawMode);
				cached.dIMinus_base = QuantizeInputSeries(bars, cached.dIMinus_base, gradationLevels, x7, validationRawMode);
				cached.dIPlus_base = QuantizeInputSeries(bars, cached.dIPlus_base, gradationLevels, x7, validationRawMode);
				cached.correlationAngle_rep_base = QuantizeInputSeries(bars, cached.correlationAngle_rep_base, gradationLevels, x8, validationRawMode);
				cached.correlationTrend_base = QuantizeInputSeries(bars, cached.correlationTrend_base, gradationLevels, x9, validationRawMode);
				cached.hurst_base = QuantizeInputSeries(bars, cached.hurst_base, gradationLevels, x10, validationRawMode);
				cached.cmf_base = QuantizeInputSeries(bars, cached.cmf_base, gradationLevels, x11, validationRawMode);
				cached.decibel_base = QuantizeInputSeries(bars, cached.decibel_base, gradationLevels, x12, validationRawMode);
				cached.jocker_base = QuantizeInputSeries(bars, cached.jocker_base, gradationLevels, x13, validationRawMode);
				cached.marketBreadth_base = QuantizeInputSeries(bars, cached.marketBreadth_base, gradationLevels, x14, validationRawMode);
			}

			foreach (IndicatorBase ib in localStartIndexList)
			{
				if (ib.FirstValidIndex > maxStartIndex)
					maxStartIndex = ib.FirstValidIndex;
			}

			cached.modelStartIndex = maxStartIndex;
			return cached;
		}

		private Dictionary<string, double>[][] GetOrCreateTomorrowFeatureValues(
			BarHistory bars,
			ModelParameters modelPars,
			CachedBaseIndicators modelIndicatorCache,
			bool includeTomorrowHistory)
		{
			string key = string.Join("|", new[]
			{
				modelPars.inputGradationLevels.ToString(CultureInfo.InvariantCulture),
				modelPars.validationWithoutGradation.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX4.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX4Smooth.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX6.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX6Down.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX7.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX7Plus.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX8.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX9.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX10.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX11.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX12.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX13.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX13HiLoPeriod.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX13Mode.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX14.ToString(CultureInfo.InvariantCulture),
				modelPars.paramX14MaPeriod.ToString(CultureInfo.InvariantCulture),
				includeTomorrowHistory ? "history" : "current",
				tomorrowBars.ToString(CultureInfo.InvariantCulture),
				bars.Count.ToString(CultureInfo.InvariantCulture)
			});

			if (_tomorrowFeatureCacheByParameters.TryGetValue(key, out Dictionary<string, double>[][] cached))
				return cached;

			cached = new Dictionary<string, double>[bars.Count][];
			int firstOrigin = includeTomorrowHistory
				? Math.Max(0, bars.Count - tomorrowBars - 1)
				: Math.Max(0, bars.Count - 1);
			double[] factors = { 0.98, 0.99, 1.00, 1.01, 1.02 };

			for (int origin = firstOrigin; origin < bars.Count; origin++)
			{
				cached[origin] = new Dictionary<string, double>[factors.Length];
				for (int scenario = 0; scenario < factors.Length; scenario++)
					cached[origin][scenario] = BuildTomorrowFeatureValuesAtBar(
						bars, origin, factors[scenario], modelPars, modelIndicatorCache);
			}

			_tomorrowFeatureCacheByParameters[key] = cached;
			return cached;
		}

		private Dictionary<string, double> BuildTomorrowFeatureValuesAtBar(
			BarHistory bars,
			int lastRealBar,
			double priceFactor,
			ModelParameters modelPars,
			CachedBaseIndicators modelIndicatorCache)
		{
			var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
			if (bars == null || bars.Count == 0 || lastRealBar < 0 || lastRealBar >= bars.Count)
				return values;

			BarHistory synthetic = new BarHistory(bars);
			for (int bar = 0; bar <= lastRealBar; bar++)
				synthetic.Add(
					bars.DateTimes[bar], bars.Open[bar], bars.High[bar], bars.Low[bar], bars.Close[bar], bars.Volume[bar]);

			DateTime futureDate = lastRealBar + 1 < bars.Count
				? bars.DateTimes[lastRealBar + 1]
				: bars.DateTimes[lastRealBar].AddDays(1);
			double futurePrice = bars.Close[lastRealBar] * priceFactor;
			// Неизвестный будущий OHLC задаётся нейтрально, а объём переносится с t на t+1.
			synthetic.Add(futureDate, futurePrice, futurePrice, futurePrice, futurePrice, bars.Volume[lastRealBar]);
			int futureBar = synthetic.Count - 1;

			// Внешние непрогнозируемые ряды продолжаются на t+1 тем же значением, что было на t.
			values["model_cnn_fear_greed"] = modelIndicatorCache.model_cnn_fear_greed_base[lastRealBar];
			values["risk_measure"] = modelIndicatorCache.risk_measure_base[lastRealBar];
			values["model_panic_euphoria"] = modelIndicatorCache.model_panic_euphoria_base[lastRealBar];
			values["jocker"] = modelIndicatorCache.jocker_base[lastRealBar];
			values["marketBreadth"] = modelIndicatorCache.marketBreadth_base[lastRealBar];

			values["myRSI_rep"] = QuantizeTomorrowValue(
				new MyRSI_rep(synthetic.Close, modelPars.paramX4, modelPars.paramX4Smooth)[futureBar], modelPars, futureDate);
			values["trendScore"] = QuantizeTomorrowValue(
				new TrendScore(synthetic)[futureBar] * 0.1, modelPars, futureDate);
			values["aroonUp"] = QuantizeTomorrowValue(
				(-50.0 + new AroonUp(synthetic.Close, modelPars.paramX6)[futureBar]) / 50.0, modelPars, futureDate);
			values["aroonDown"] = QuantizeTomorrowValue(
				(-50.0 + new AroonDown(synthetic.Close, modelPars.paramX6Down)[futureBar]) / 50.0, modelPars, futureDate);

			DIMinus diMinus = new DIMinus(synthetic, modelPars.paramX7);
			DIPlus diPlus = new DIPlus(synthetic, modelPars.paramX7Plus);
			values["dIMinus"] = QuantizeTomorrowValue(AGC((-30.0 + diMinus) / 30.0)[futureBar], modelPars, futureDate);
			values["dIPlus"] = QuantizeTomorrowValue(AGC((-25.0 + diPlus) / 30.0)[futureBar], modelPars, futureDate);

			values["correlationAngle_rep"] = QuantizeTomorrowValue(
				new CorrelationAngle_rep(synthetic.Close, modelPars.paramX8)[futureBar] / 180.0, modelPars, futureDate);
			values["correlationTrend"] = QuantizeTomorrowValue(
				AGC(new CorrelationTrend(synthetic.Close, modelPars.paramX9))[futureBar], modelPars, futureDate);

			Hurst hurst = new Hurst(synthetic.Close, modelPars.paramX10);
			EhlersInverseFisherTransform_gpt hurstFisher = new EhlersInverseFisherTransform_gpt(hurst, 3, 1);
			values["hurst"] = QuantizeTomorrowValue(7.0 * (hurstFisher[futureBar] - 0.6), modelPars, futureDate);

			CMF cmf = new CMF(synthetic, modelPars.paramX11);
			EhlersInverseFisherTransform_gpt cmfFisher = new EhlersInverseFisherTransform_gpt(cmf, 3, 10);
			values["cmf"] = QuantizeTomorrowValue(cmfFisher[futureBar] - 0.1, modelPars, futureDate);

			Decibel decibel = new Decibel(synthetic.Close, modelPars.paramX12);
			EhlersInverseFisherTransform_gpt decibelFisher = new EhlersInverseFisherTransform_gpt(decibel, 8, 0.1);
			values["decibel"] = QuantizeTomorrowValue(decibelFisher[futureBar] + 0.55, modelPars, futureDate);

			return values;
		}

		private double QuantizeTomorrowValue(double value, ModelParameters modelPars, DateTime futureDate)
		{
			int levels = modelPars.inputGradationLevels;
			if (levels <= 0 || (modelPars.validationWithoutGradation == 1 && !IsTrainingDate(futureDate)))
				return value;
			if (double.IsNaN(value))
				return double.NaN;

			value = double.IsPositiveInfinity(value) ? 1.0 :
				double.IsNegativeInfinity(value) ? -1.0 :
				Math.Max(-1.0, Math.Min(1.0, value));
			if (levels == 1)
				return 0.0;

			int half = levels / 2;
			int bin = Math.Min(levels - 1, (int)Math.Floor((value + 1.0) * levels / 2.0));
			if ((levels & 1) == 0)
			{
				int signedLevel = bin < half ? bin - half : bin - half + 1;
				return signedLevel / (double)half;
			}

			return (bin - half) / (double)half;
		}

		private TimeSeries QuantizeInputSeries(BarHistory bars, TimeSeries source, int levels, double featureSwitch, int validationRawMode)
		{
			if (levels <= 0 || Math.Abs(featureSwitch) < 0.001)
				return source;

			var result = new TimeSeries(bars.DateTimes, double.NaN);
			int half = levels / 2;

			for (int i = 0; i < source.Count; i++)
			{
				double value = source[i];

				if (double.IsNaN(value))
				{
					result[i] = double.NaN;
					continue;
				}

				if (validationRawMode == 1 && !IsTrainingDate(bars.DateTimes[i]))
				{
					result[i] = value;
					continue;
				}

				value = double.IsPositiveInfinity(value) ? 1.0 :
						double.IsNegativeInfinity(value) ? -1.0 :
						Math.Max(-1.0, Math.Min(1.0, value));

				if (levels == 1)
				{
					result[i] = 0.0;
					continue;
				}

				int bin = Math.Min(levels - 1, (int)Math.Floor((value + 1.0) * levels / 2.0));

				if ((levels & 1) == 0)
				{
					int signedLevel = bin < half ? bin - half : bin - half + 1;
					result[i] = signedLevel / (double)half;
				}
				else
				{
					result[i] = (bin - half) / (double)half;
				}
			}

			return result;
		}

		private static double CalculateNormalizedX13(double highs, double lows, double sum, int mode)
		{
			double value = mode switch
			{
				// Исходный HiloMode 0: [-1; +1], направление сохраняется.
				0 => (highs - lows) / sum,
				// Исходный HiloMode 1: [0; 0.5] -> [-1; +1].
				1 => 4.0 * (Math.Sqrt(Math.Max(0.0, highs * lows)) / sum - 0.25),
				_ => throw new ArgumentOutOfRangeException(nameof(mode), "Режим X13 должен быть 0 или 1.")
			};

			// Защита от микроскопического выхода за границы из-за double-арифметики.
			return Math.Max(-1.0, Math.Min(1.0, value));
		}

		private bool IsTrainingDate(DateTime date) =>
			(date >= startDateTraining && date <= endDateTraining) ||
			(date >= startDateTraining2 && date <= endDateTraining2);

		private TimeSeries CalculateMarketBreadthBase(BarHistory bars)
		{
			string[] tickers = { "SPY", "XLV", "XLU", "XLE", "XLP" };

			int maPeriod = paramX14MaPeriod;
			int emaSmoothPeriod = paramX14;

			var tickerData = new Dictionary<string, (TimeSeries close, TimeSeries vwma)>();

			foreach (string ticker in tickers)
			{
				try
				{
					BarHistory extBars = GetHistory(bars, ticker);

					if (extBars != null && extBars.Count > 0)
					{
						var close = extBars.Close;
						var vwma = new VWMA(close, maPeriod, extBars);
						tickerData[ticker] = (close, vwma);
					}
				}
				catch { }
			}

			var rawBreadth = new TimeSeries(bars.DateTimes, 50.0);

			for (int i = 0; i < bars.Count; i++)
			{
				int upCount = 0, validCount = 0;

				foreach (var kvp in tickerData)
				{
					var (close, vwma) = kvp.Value;

					if (i < close.Count && i < vwma.Count && !double.IsNaN(close[i]) && !double.IsNaN(vwma[i]))
					{
						validCount++;
						if (close[i] > vwma[i]) upCount++;
					}
				}

				if (validCount > 0) rawBreadth[i] = 100.0 * upCount / validCount;
			}

			var smoothed = EMA.Series(rawBreadth, emaSmoothPeriod);
			return 0.01 * (smoothed - 50.0);
		}

		#endregion

		#region CalculatePredict Methods

		private void PlotZz1SuperSmoother(BarHistory bars)
		{
			TimeSeries averagePrice = (bars.Close + bars.High + bars.Low) / 3;
			TimeSeries superSmoother = new SuperSmoother(averagePrice, FixedSmoothingPeriod);
			PlotTimeSeries(
				superSmoother,
				$"ZZ1 SuperSmoother ({FixedSmoothingPeriod})",
				"Price",
				WLColor.Black,
				PlotStyle.Line);
		}

		private void CalculatePredictWithShift(BarHistory bars)
		{
			TimeSeries AverageP = (bars.Close + bars.High + bars.Low) / 3;
			TimeSeries SS8 = new SuperSmoother(AverageP, smoothingPeriod);
			TimeSeries SS8_Shifted = SS8 << shift;

			predict = new TimeSeries(bars.DateTimes, double.NaN);
			for (int i = 1; i < SS8_Shifted.Count; i++)
			{
				if (SS8_Shifted[i] > SS8_Shifted[i - 1]) predict[i] = 1;
				else if (SS8_Shifted[i] < SS8_Shifted[i - 1]) predict[i] = 0;
				else predict[i] = double.NaN;
			}
		}

		private void CalculatePredictWithAtrpImpulse(BarHistory bars, TimeSeries price)
		{
			TimeSeries atrPercent = new ATRP(bars, FixedTargetAtrpPeriod);
			predict = new TimeSeries(bars.DateTimes, double.NaN);

			int firstValid = Math.Max(1, atrPercent.FirstValidIndex);
			int lastValid = price.Count - targetAtrpShift - 1;
			bool isLong = false;

			for (int bar = firstValid; bar <= lastValid; bar++)
			{
				double atr = atrPercent[bar];
				double currentPrice = price[bar];
				double futurePrice = price[bar + targetAtrpShift];

				if (!double.IsNaN(atr) && !double.IsInfinity(atr) && atr > 0.0 &&
					!double.IsNaN(currentPrice) && !double.IsInfinity(currentPrice) && currentPrice != 0.0 &&
					!double.IsNaN(futurePrice) && !double.IsInfinity(futurePrice))
				{
					double futureReturnPercent = 100.0 * (futurePrice - currentPrice) / currentPrice;
					double normalizedImpulse = futureReturnPercent / atr;
					if (normalizedImpulse >= targetAtrpThreshold) isLong = true;
					else if (normalizedImpulse <= -targetAtrpThreshold) isLong = false;
				}

				predict[bar] = isLong ? 1.0 : 0.0;
			}

			predict.FirstValidIndex = firstValid;
		}

		private void CalculatePredictWithPeakTrough(BarHistory bars, bool drawOnPrice = false)
		{
			predict = new TimeSeries(bars.DateTimes, double.NaN);
			TimeSeries averages = (bars.High + bars.Low + bars.Close) / 3;
			PeakTroughCalculator ptc = new PeakTroughCalculator(averages, reversalPercentage, PeakTroughReversalType.Percent);

			for (int n = 1; n < ptc.PeakTroughs.Count; n++)
			{
				int x1 = ptc.PeakTroughs[n - 1].PeakTroughIndex;
				double y1 = ptc.PeakTroughs[n - 1].Value;
				int x2 = ptc.PeakTroughs[n].PeakTroughIndex;
				double y2 = ptc.PeakTroughs[n].Value;

				WLColor lineColor = y2 > y1 ? WLColor.Green : WLColor.Red;
				if (drawOnPrice)
					DrawLine(x1, y1, x2, y2, lineColor, 2, LineStyle.Solid);

				for (int i = x1; i <= x2; i++)
					predict[i] = y2 > y1 ? 1.0 : 0.0;
			}

			if (ptc.PeakTroughs.Count > 0)
			{
				PeakTrough lastPoint = ptc.PeakTroughs[ptc.PeakTroughs.Count - 1];
				int lastIndex = bars.Count - 1;
				double maxAverage = 0, minAverage = 0;

				for (int i = lastPoint.PeakTroughIndex; i <= lastIndex; i++)
				{
					if (maxAverage == 0 || averages[i] > maxAverage) maxAverage = averages[i];
					if (minAverage == 0 || averages[i] < minAverage) minAverage = averages[i];
				}

				bool isLastPeak = lastPoint.Type == PeakTroughType.Peak;
				double predictedEndValue = isLastPeak ? minAverage : maxAverage;

				WLColor forecastLineColor = WLColor.Gray;
				if (drawOnPrice)
					DrawLine(lastPoint.PeakTroughIndex, lastPoint.Value, lastIndex, predictedEndValue, forecastLineColor, 2, LineStyle.Dashed);

				for (int i = lastPoint.PeakTroughIndex; i <= lastIndex; i++)
					predict[i] = !isLastPeak ? 1.0 : 0.0;
			}
		}

		#endregion

		#region Build And Plot Deciles

		private void BuildAndPlotDeciles(BarHistory bars, List<(string FileName, double BenchValue, TimeSeries ForecastTS)> modelsData, string packageLabel, int totalModels, int decileOffset)
		{
			// Кэш ProcessResult разделяется между параллельными проходами оптимизатора.
			// Поэтому исходный список не изменяем и задаём устойчивый порядок для равных Bench.
			var sortedModels = modelsData
				.OrderBy(m => m.BenchValue)
				.ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
				.ToList();
			int totalModelsForDeciles = sortedModels.Count;

			// Каждый пакет занимает собственный блок из десяти децилей.
			for (int g = 0; g < 10; g++)
			{
				this.decileSeries[decileOffset + g] = new TimeSeries(bars.DateTimes, 0.0);
			}

			int[] decileCounts = new int[10];
			double[] decileMinBench = new double[10];
			double[] decileMaxBench = new double[10];
			double[] decileAvgPercent = new double[10];
			double[] decileMeanAll = new double[10];
			double[] decileMAD_50 = new double[10];
			double[] decileAmplitudeRatio_50 = new double[10];

			for (int g = 0; g < 10; g++)
			{
				decileMinBench[g] = double.MaxValue;
				decileMaxBench[g] = double.MinValue;
			}

			for (int i = 0; i < totalModelsForDeciles; i++)
			{
				int groupIndex = (int)(i * 10.0 / totalModelsForDeciles);
				if (groupIndex >= 10) groupIndex = 9;

				var model = sortedModels[i];
				decileCounts[groupIndex]++;

				if (!double.IsNaN(model.BenchValue))
				{
					if (model.BenchValue < decileMinBench[groupIndex]) decileMinBench[groupIndex] = model.BenchValue;
					if (model.BenchValue > decileMaxBench[groupIndex]) decileMaxBench[groupIndex] = model.BenchValue;
				}

				for (int bar = 0; bar < model.ForecastTS.Count; bar++)
				{
					this.decileSeries[decileOffset + groupIndex][bar] += model.ForecastTS[bar];
				}
			}

			for (int bar = 0; bar < bars.Count; bar++)
			{
				for (int g = 0; g < 10; g++)
				{
					if (decileCounts[g] > 0) this.decileSeries[decileOffset + g][bar] = (this.decileSeries[decileOffset + g][bar] / decileCounts[g]) * 100.0;
				}
			}

			const double NEUTRAL_LEVEL = 50.0;
			for (int g = 0; g < 10; g++)
			{
				int lastBar = this.decileSeries[decileOffset + g].Count - 1;
				decileAvgPercent[g] = !double.IsNaN(this.decileSeries[decileOffset + g][lastBar]) ? this.decileSeries[decileOffset + g][lastBar] : 0.0;

				double sum = 0, madSum = 0;
				int count = 0;
				for (int bar = 0; bar < this.decileSeries[decileOffset + g].Count; bar++)
				{
					if (!double.IsNaN(this.decileSeries[decileOffset + g][bar]) && this.decileSeries[decileOffset + g][bar] != 0.0)
					{
						sum += this.decileSeries[decileOffset + g][bar];
						madSum += Math.Abs(this.decileSeries[decileOffset + g][bar] - NEUTRAL_LEVEL);
						count++;
					}
				}
				decileMeanAll[g] = count > 0 ? sum / count : 0.0;
				decileMAD_50[g] = count > 0 ? madSum / count : 0.0;
				decileAmplitudeRatio_50[g] = (decileMAD_50[g] / NEUTRAL_LEVEL) * 100.0;
			}

			BeginResearchReport($"bench_deciles_{packageLabel}");
			WriteToDebugLog("", false);
			WriteToDebugLog("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
			WriteToDebugLog($"│                                                                     РАСПРЕДЕЛЕНИЕ МОДЕЛЕЙ ПО 10 ГРАДАЦИЯМ (ДЕЦИЛЯМ) BENCH ({packageLabel})                                                                  │", false);
			WriteToDebugLog("──────────┬──────────────────────────────┬──────────────┬────────────────┬──────────────┬──────────────┬──────────────┐", false);
			WriteToDebugLog("│ Градация │ Кол-во моделей │   Min Bench  │   Max Bench  │ Ср. % голосов  │ Средн. за всё│  MAD от 50   │  Ratio к 50  │", false);
			WriteToDebugLog("│          │                │              │              │ (послед. бар)  │   время      │  (площадь)   │     (%)      │", false);
			WriteToDebugLog("├──────────┼────────────────┼──────────────┼──────────────┼──────────────────────────────┼──────────────┼──────────────", false);

			for (int g = 0; g < 10; g++)
			{
				string minB = decileMinBench[g] == double.MaxValue ? "N/A" : decileMinBench[g].ToString("F2");
				string maxB = decileMaxBench[g] == double.MinValue ? "N/A" : decileMaxBench[g].ToString("F2");
				WriteToDebugLog($"│  {(g + 1) * 10,2}     │      {decileCounts[g],4}      │ {minB,12} │ {maxB,12} │ {decileAvgPercent[g],14:F2} │ {decileMeanAll[g],12:F2} │ {decileMAD_50[g],12:F2} │ {decileAmplitudeRatio_50[g],11:F1}% │", false);
			}
			WriteToDebugLog("└──────────┴──────────────────────────────┴──────────────┴────────────────┴──────────────┴────────────────────────────┘", false);

			int bestDecile = 0, worstDecile = 0;
			double maxMAD = double.MinValue, minMAD = double.MaxValue;
			for (int g = 0; g < 10; g++)
			{
				if (decileMAD_50[g] > maxMAD) { maxMAD = decileMAD_50[g]; bestDecile = g; }
				if (decileMAD_50[g] < minMAD && decileMAD_50[g] > 0) { minMAD = decileMAD_50[g]; worstDecile = g; }
			}
			WriteToDebugLog($"  ★ Дециль с максимальной поляризацией ({packageLabel}): #{bestDecile + 1} (MAD от 50 = {maxMAD:F2}, среднее = {decileMeanAll[bestDecile]:F2}%)", false);
			WriteToDebugLog($"  ○ Дециль с минимальной поляризацией ({packageLabel}): #{worstDecile + 1} (MAD от 50 = {minMAD:F2}, среднее = {decileMeanAll[worstDecile]:F2}%)", false);
			EndResearchReport();

			WLColor[] colors = { WLColor.Red, WLColor.OrangeRed, WLColor.Orange, WLColor.Gold, WLColor.YellowGreen, WLColor.GreenYellow, WLColor.Lime, WLColor.Green, WLColor.DarkGreen, WLColor.Blue };
			for (int g = 0; g < 10; g++)
			{
				PlotTimeSeries(this.decileSeries[decileOffset + g], $"{packageLabel} Decile {g + 1} ({decileCounts[g]} mod)", "Decile", colors[g], PlotStyle.Line);
			}
		}

		private void PreparePositionControl(BarHistory bars)
		{
			controlBreadthVelocity = null;
			controlEma50 = EMA.Series(bars.Close, 50);
			if (controlEnabled == 0 || controlScheme == 0) return;
			if (controlScheme == 3)
			{
				string[] files = { "sp500_sma50.csv", "sp500_sma100.csv", "sp500_sma200.csv", "nasdaq100_sma100.csv" };
				var maps = files.Select(name => ReadControlBreadth(Path.Combine(BreezeExportDirectory, name))).ToArray();
				controlBreadthVelocity = Enumerable.Repeat(double.NaN, bars.Count).ToArray();
				for (int i = 7; i < bars.Count; i++)
				{
					var v = new double[4]; bool known = true;
					for (int k = 0; k < 4; k++)
					{
						if (!maps[k].TryGetValue(bars.DateTimes[i].Date, out double now) ||
							!maps[k].TryGetValue(bars.DateTimes[i-7].Date, out double prior)) { known = false; break; }
						v[k] = (now-prior)/7.0;
					}
					if (!known) continue;
					int positive = v.Count(x => x > 1e-12), negative = v.Count(x => x < -1e-12);
					double speed = v.Average(Math.Abs);
					controlBreadthVelocity[i] = Math.Max(positive,negative) >= 3 && speed + 1e-12 >= .5 && v.Max()-v.Min() <= 5 + 1e-12
						? (positive-negative)/4.0 * Math.Tanh(speed/1.5) : 0;
				}
			}
			// Fail visibly rather than optimise a different algorithm under the selected name.
			TimeSeries required = controlScheme == 1 || controlScheme == 5 ? rules1ScoreSeries : controlScheme == 2 ? rules2ScoreSeries : null;
			if (required != null && !Enumerable.Range(0,bars.Count).Any(i => double.IsFinite(required[i])))
				throw new InvalidOperationException("Выбранная схема управления: нет оценок скоринга. Пересчитайте Market Breeze.");
		}

		private static Dictionary<DateTime,double> ReadControlBreadth(string path)
		{
			var result = new Dictionary<DateTime,double>();
			foreach (string line in File.ReadLines(path).Skip(1))
			{
				var c = line.Split(',');
				if (c.Length > 4 && DateTime.TryParseExact(c[0],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
					&& double.TryParse(c[4],NumberStyles.Float,CultureInfo.InvariantCulture,out double value) && double.IsFinite(value)) result[date.Date] = value;
			}
			return result;
		}

		private int ApplyPositionControl(BarHistory bars, int idx, int current, int neural)
		{
			if (controlEnabled == 0 || controlScheme == 0) return neural;
			if (controlScheme == 6 || controlScheme == 7)
				return ApplyEmaHoldingEpisode(bars, idx, current, neural);
			double score2 = rules2ScoreSeries[idx];
			if (double.IsFinite(score2))
			{
				if (rules2ControlHolding) { if (score2 <= 50) rules2ControlHolding = false; }
				else if (score2 >= 55) rules2ControlHolding = true;
			}
			double magnitude = CalculateDecileImbalance(idx);
			if (!double.IsFinite(magnitude)) return current;
			if (magnitude <= disbalanceLevel) { controlExitLatched = false; return neural; }
			int capacity = decileSeries.Length;
			switch (controlScheme)
			{
				case 1:
					double s = rules1ScoreSeries[idx];
					return double.IsFinite(s) ? (s >= 50 ? capacity : 0) : current;
				case 2:
					return double.IsFinite(score2) ? (rules2ControlHolding ? capacity : 0) : current;
				case 3:
					if (idx > 0 && controlBreadthVelocity[idx] < 0 && controlBreadthVelocity[idx-1] < 0) controlExitLatched = true;
					return controlExitLatched ? 0 : current;
				case 4:
					if (idx > 0 && bars.Close[idx] < controlEma50[idx] && bars.Close[idx-1] < controlEma50[idx-1]) controlExitLatched = true;
					return controlExitLatched ? 0 : current;
				case 5:
					if (idx <= StartIndex) return current;
					double today = rules1ScoreSeries[idx], yesterday = rules1ScoreSeries[idx-1];
					if (!double.IsFinite(today) || !double.IsFinite(yesterday)) return current;
					if (today >= 55 && yesterday >= 55) return Math.Max(current,neural);
					if (today < 45 && yesterday < 45) return Math.Min(current,neural);
					return current;
				default: throw new InvalidOperationException("Неизвестная схема управления позицией.");
			}
		}

		private int ApplyEmaHoldingEpisode(BarHistory bars, int idx, int current, int neural)
		{
			double magnitude = CalculateDecileImbalance(idx);
			if (!double.IsFinite(magnitude)) return current;
			bool zone = magnitude > disbalanceLevel;
			bool newZone = zone && !emaEpisodePreviousZone;
			emaEpisodePreviousZone = zone;
			// Waiting after a completed exit: only a strictly higher close restores neural control.
			if (emaEpisodeState == 2)
			{
				if (bars.Close[idx] > controlEma50[idx]) { emaEpisodeState = 0; return neural; }
				return 0;
			}
			// A new zone can freeze only an actually existing position.
			if (emaEpisodeState == 0 && newZone && current > 0)
			{
				emaEpisodeState = 1;
				emaEpisodeStartBar = idx;
			}
			if (emaEpisodeState == 0) return neural;
			emaEpisodeHeldBar = idx;
			if (idx > StartIndex && bars.Close[idx] < controlEma50[idx] && bars.Close[idx-1] < controlEma50[idx-1])
			{
				emaEpisodeState = controlScheme == 7 ? 2 : 0;
				return 0;
			}
			return current;
		}

		private void LoadAndPlotRulesScores(BarHistory bars)

		{
			rules1ScoreSeries = LoadRulesScoreSeries(
				bars, Path.Combine(BreezeExportDirectory, Rules1ScoreFileName), "Rules 1");
			rules2ScoreSeries = LoadRulesScoreSeries(
				bars, Path.Combine(BreezeExportDirectory, Rules2ScoreFileName), "Rules 2");

			const string paneName = "Rules 1 / Rules 2";
			PlotTimeSeries(rules1ScoreSeries, "Rules 1", paneName, WLColor.DarkBlue, PlotStyle.Line);
			PlotTimeSeries(rules2ScoreSeries, "Rules 2", paneName, WLColor.Red, PlotStyle.Line);
			DrawHorzLine(50.0, WLColor.Gray, 1, LineStyle.Dotted, paneName, false);
			SetPaneMinMax(paneName, 0.0, 100.0);
			SetPaneDrawingOptions(paneName, 50);
		}

		private TimeSeries LoadRulesScoreSeries(BarHistory bars, string path, string modelName)
		{
			TimeSeries result = new TimeSeries(bars.DateTimes, double.NaN);
			if (!File.Exists(path))
			{
				WriteToDebugLog($"[RULES] {modelName}: файл не найден: {path}", false);
				return result;
			}

			Dictionary<DateTime, double> byDate = new Dictionary<DateTime, double>();
			int sourceRows = 0;
			foreach (string line in File.ReadLines(path).Skip(1))
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				string[] columns = line.Split(new[] { ',' }, 3);
				if (columns.Length < 2) continue;
				if (!DateTime.TryParseExact(columns[0].Trim().Trim('"'), "yyyy-MM-dd",
					CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)) continue;
				if (!double.TryParse(columns[1].Trim().Trim('"'), NumberStyles.Float,
					CultureInfo.InvariantCulture, out double score)) continue;
				if (double.IsNaN(score) || double.IsInfinity(score)) continue;
				byDate[date.Date] = Math.Max(0.0, Math.Min(100.0, score));
				sourceRows++;
			}

			int matchedBars = 0;
			for (int bar = 0; bar < bars.Count; bar++)
			{
				// Только точное совпадение с фактическим дневным баром SPY; ближайшая дата не подставляется.
				if (byDate.TryGetValue(bars.DateTimes[bar].Date, out double score))
				{
					result[bar] = score;
					matchedBars++;
				}
			}

			WriteToDebugLog(
				$"[RULES] {modelName}: прочитано {sourceRows}, совпало с точными датами SPY {matchedBars}.", false);
			return result;
		}

		private void PlotActiveDecileCount(BarHistory bars)
		{
			activeDecileCountSeries = new TimeSeries(bars.DateTimes, 0.0);
			for (int bar = 0; bar < bars.Count; bar++)
			{
				int activeCount = 0;
				double activeThreshold = decileThreshold;
				for (int g = 0; g < decileSeries.Length; g++)
				{
					if (decileSeries[g] != null && decileSeries[g][bar] >= activeThreshold)
						activeCount++;
				}
				activeDecileCountSeries[bar] = activeCount;
			}

			string paneName = decileSeries.Length == 30 ? "Positions 0-30" : "Positions 0-10";
			PlotTimeSeries(activeDecileCountSeries, "Active Deciles", paneName, WLColor.DarkBlue, PlotStyle.Line);
			SetPaneDrawingOptions(paneName, 25);
		}

		private void PlotDecileImbalance(BarHistory bars)
		{
			const string paneName = "Disbalance";
			disbalanceStateSeries = new TimeSeries(bars.DateTimes, double.NaN);
			for (int bar = 0; bar < bars.Count; bar++)
			{
				double magnitude = CalculateDecileImbalance(bar);
				if (double.IsFinite(magnitude))
					disbalanceStateSeries[bar] = magnitude > disbalanceLevel ? 1.0 : 0.0;
			}

			PlotTimeSeries(disbalanceStateSeries,
				$"Disbalance (1=есть, 0=нет; допуск={disbalanceLevel})",
				paneName, WLColor.DarkOrange, PlotStyle.Line);
			SetPaneMinMax(paneName, 0.0, 1.0);
			SetPaneDrawingOptions(paneName, 75);
		}

		private double CalculateDecileImbalance(int bar)
		{
			double worstPackageImbalance = double.NaN;
			for (int offset = 0; offset < decileSeries.Length; offset += 10)
			{
				int aboveOrEqual = 0;
				int below = 0;
				int available = 0;
				int end = Math.Min(offset + 10, decileSeries.Length);
				for (int g = offset; g < end; g++)
				{
					TimeSeries series = decileSeries[g];
					if (series == null) continue;
					double value = series[bar];
					if (double.IsNaN(value) || double.IsInfinity(value)) continue;
					available++;
					if (value >= decileThreshold) aboveOrEqual++;
					else below++;
				}

				if (available == 0) continue;
				double packageImbalance = Math.Min(aboveOrEqual, below);
				worstPackageImbalance = double.IsNaN(worstPackageImbalance)
					? packageImbalance
					: Math.Max(worstPackageImbalance, packageImbalance);
			}
			return worstPackageImbalance;
		}

		#endregion

		#region Leave-One-Out Analysis

		private void BuildLeaveOneOutAnalysis(BarHistory bars, List<(string FileName, double BenchValue, TimeSeries ForecastTS)> modelsData, string packageLabel, int packageModelCount, int lastModelStartIndex, TimeSeries localAggregatedEnsemble)
		{
			double SimulateStrategyEquity(TimeSeries desiredQtySeries, BarHistory barsHist, int startIndex)
			{
				if (barsHist == null || barsHist.Count < 2) return 0.0;
				double cumProfit = 0.0;
				int currentQty = 0;
				int startBar = Math.Max(1, startIndex);
				for (int i = startBar; i < barsHist.Count; i++)
				{
					double dp = barsHist.Close[i] - barsHist.Close[i - 1];
					cumProfit += currentQty * dp;
					double rawDesired = desiredQtySeries[i];
					if (double.IsNaN(rawDesired) || double.IsInfinity(rawDesired)) rawDesired = 0.0;
					int desired = (int)Math.Round(rawDesired, MidpointRounding.AwayFromZero);
					desired = Math.Max(0, Math.Min(10, desired));
					currentQty = desired;
				}
				return cumProfit;
			}

			int analysisStartIndex = lastModelStartIndex > 0 ? lastModelStartIndex : StartIndex;

			TimeSeries fullDesiredQty = new TimeSeries(bars.DateTimes, 0.0);
			double fullGradFactor = packageModelCount / 10.0;

			for (int i = 0; i < bars.Count; i++)
			{
				if (packageModelCount <= 10)
					fullDesiredQty[i] = localAggregatedEnsemble[i];
				else
					fullDesiredQty[i] = localAggregatedEnsemble[i] / fullGradFactor;
			}

			double equityFull = SimulateStrategyEquity(fullDesiredQty, bars, analysisStartIndex);

			var sortedModels = modelsData
				.OrderBy(m => m.BenchValue)
				.ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
				.ToList();
			int totalModelsForDeciles = sortedModels.Count;

			TimeSeries[] decileSeriesArray = new TimeSeries[10];
			for (int g = 0; g < 10; g++) decileSeriesArray[g] = new TimeSeries(bars.DateTimes, 0.0);

			int[] decileCounts = new int[10];

			for (int i = 0; i < totalModelsForDeciles; i++)
			{
				int groupIndex = (int)(i * 10.0 / totalModelsForDeciles);
				if (groupIndex >= 10) groupIndex = 9;
				decileCounts[groupIndex]++;

				for (int bar = 0; bar < sortedModels[i].ForecastTS.Count; bar++)
					decileSeriesArray[groupIndex][bar] += sortedModels[i].ForecastTS[bar];
			}

			for (int bar = 0; bar < bars.Count; bar++)
			{
				for (int g = 0; g < 10; g++)
				{
					if (decileCounts[g] > 0)
						decileSeriesArray[g][bar] = (decileSeriesArray[g][bar] / decileCounts[g]) * 100.0;
				}
			}

			double[] decileIndividualEquity = new double[10];
			double[] decileLOOEquity = new double[10];
			double[] decileDelta = new double[10];
			double[] decileDeltaPercent = new double[10];

			for (int g = 0; g < 10; g++)
			{
				TimeSeries decileSeriesG = decileSeriesArray[g];

				TimeSeries indivDesiredQty = new TimeSeries(bars.DateTimes, 0.0);
				for (int i = 0; i < bars.Count; i++)
					indivDesiredQty[i] = decileSeriesG[i] / 10.0;
				decileIndividualEquity[g] = SimulateStrategyEquity(indivDesiredQty, bars, analysisStartIndex);

				TimeSeries looDesiredQty = new TimeSeries(bars.DateTimes, 0.0);
				int remainingModels = packageModelCount - decileCounts[g];

				if (remainingModels <= 0)
				{
					decileLOOEquity[g] = 0.0;
				}
				else
				{
					for (int i = 0; i < bars.Count; i++)
					{
						double decileSum = decileSeriesG[i] * decileCounts[g] / 100.0;
						double aggWithout = localAggregatedEnsemble[i] - decileSum;

						if (remainingModels <= 10)
							looDesiredQty[i] = aggWithout;
						else
						{
							double looGradFactor = remainingModels / 10.0;
							looDesiredQty[i] = aggWithout / looGradFactor;
						}
					}
					decileLOOEquity[g] = SimulateStrategyEquity(looDesiredQty, bars, analysisStartIndex);
				}

				decileDelta[g] = equityFull - decileLOOEquity[g];
				decileDeltaPercent[g] = Math.Abs(equityFull) > 0.001 ? (decileDelta[g] / equityFull) * 100.0 : 0.0;
			}

			BeginResearchReport($"decile_leave_one_out_{packageLabel}");
			WriteToDebugLog("", false);
			WriteToDebugLog("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────......", false);
			WriteToDebugLog($"│                                                              АНАЛИЗ ВКЛАДА КАЖДОГО ДЕЦИЛЯ В ИТОГОВУЮ ЭКВИТИ (Leave-One-Out) ({packageLabel})                                                              │", false);
			WriteToDebugLog("──────────┬────────────────┬────────────────────────────────────────────────┬────────────────────────────────", false);
			WriteToDebugLog("│ Градация │ Кол-во моделей │ Инд. эквити    │  LOO эквити    │   Δ Вклад      │ Вклад (%)      │   Вердикт      │", false);
			WriteToDebugLog("│          │                │ (сам по себе)  │ (без дециля)   │ (полная - LOO) │ от полной      │                │", false);
			WriteToDebugLog("├──────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────────", false);

			for (int g = 0; g < 10; g++)
			{
				string verdict = decileDelta[g] > 0.01 ? "★ ПОЛЕЗЕН" : (decileDelta[g] < -0.01 ? "✗ ВРЕДИТ" : "○ НЕЙТРАЛ");
				WriteToDebugLog($"│  {(g + 1) * 10,2}     │      {decileCounts[g],4}      │ {decileIndividualEquity[g],14:F2} │ {decileLOOEquity[g],14:F2} │ {decileDelta[g],+14:F2} │ {decileDeltaPercent[g],+12:F1}% │ {verdict,-14} │", false);
			}

			WriteToDebugLog("├──────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────────┤", false);
			WriteToDebugLog($"│   ИТОГО  │     {packageModelCount,4}      │ {"",-14} │ {"",-14} │ {equityFull,+14:F2} │ {"100.0%",13} │ Полная сист. │", false);
			WriteToDebugLog("└──────────┴────────────────┴────────────────┴────────────────────────────────┴────────────────┴────────────────", false);

			var usefulDeciles = new List<int>();
			var harmfulDeciles = new List<int>();
			var neutralDeciles = new List<int>();

			for (int g = 0; g < 10; g++)
			{
				if (decileDelta[g] > 0.01) usefulDeciles.Add(g);
				else if (decileDelta[g] < -0.01) harmfulDeciles.Add(g);
				else neutralDeciles.Add(g);
			}

			WriteToDebugLog($"  ★ Полезных децилей (Δ > 0) ({packageLabel}): {usefulDeciles.Count} — " + string.Join(", ", usefulDeciles.Select(g => $"#{g + 1}(Δ={decileDelta[g]:+F2})")), false);
			WriteToDebugLog($"  ✗ Вредных децилей (Δ < 0) ({packageLabel}): {harmfulDeciles.Count} — " + string.Join(", ", harmfulDeciles.Count > 0 ? harmfulDeciles.Select(g => $"#{g + 1}(Δ={decileDelta[g]:+F2})") : new[] { "—" }), false);
			WriteToDebugLog($"  ○ Нейтральных децилей (Δ ≈ 0) ({packageLabel}): {neutralDeciles.Count}", false);

			if (harmfulDeciles.Count > 0)
			{
				WriteToDebugLog($"  РЕКОМЕНДАЦИЯ ({packageLabel}): следующие децили снижают общую эквити и кандидаты на отключение:", false);
				foreach (int g in harmfulDeciles)
					WriteToDebugLog($"    - Дециль #{g + 1} (моделей: {decileCounts[g]}) → Δ = {decileDelta[g]:+F2} ({decileDeltaPercent[g]:+F1}%)", false);
			}

			if (usefulDeciles.Count > 0)
			{
				WriteToDebugLog($"  НАИБОЛЕЕ ЦЕННЫЕ децили ({packageLabel}) (максимальный положительный вклад):", false);
				var topUseful = usefulDeciles.OrderByDescending(g => decileDelta[g]).Take(3);
				foreach (int g in topUseful)
					WriteToDebugLog($"    + Дециль #{g + 1} (моделей: {decileCounts[g]}, Инд.эквити = {decileIndividualEquity[g]:F2}) → Δ = {decileDelta[g]:+F2} ({decileDeltaPercent[g]:+F1}%)", false);
			}
			EndResearchReport();
		}

		#endregion

		#region Decile Contribution Analysis

		private void BuildDecileContributionAnalysis(BarHistory bars, List<(string FileName, double BenchValue, TimeSeries ForecastTS)> modelsData, string packageLabel, int packageModelCount, int lastModelStartIndex, TimeSeries localAggregatedEnsemble)
		{
			double CalculateEquity(TimeSeries qtySeries, BarHistory barsHist, int startIndex)
			{
				if (barsHist == null || barsHist.Count < 2) return 0.0;
				double cumProfit = 0.0;
				int currentQty = 0;
				int startBar = Math.Max(1, startIndex);
				for (int i = startBar; i < barsHist.Count; i++)
				{
					double dp = barsHist.Close[i] - barsHist.Close[i - 1];
					cumProfit += currentQty * dp;
					double rawDesired = qtySeries[i];
					if (double.IsNaN(rawDesired) || double.IsInfinity(rawDesired)) rawDesired = 0.0;
					int desired = (int)Math.Round(rawDesired, MidpointRounding.AwayFromZero);
					desired = Math.Max(0, Math.Min(10, desired));
					currentQty = desired;
				}
				return cumProfit;
			}

			int analysisStartIndex = lastModelStartIndex > 0 ? lastModelStartIndex : StartIndex;

			// Рассчитываем equity полного ансамбля
			TimeSeries fullEnsembleQty = new TimeSeries(bars.DateTimes, 0.0);
			double fullGradFactor = packageModelCount / 10.0;

			for (int i = 0; i < bars.Count; i++)
			{
				if (packageModelCount <= 10)
					fullEnsembleQty[i] = localAggregatedEnsemble[i];
				else
					fullEnsembleQty[i] = localAggregatedEnsemble[i] / fullGradFactor;
			}
			double ensembleEquity = CalculateEquity(fullEnsembleQty, bars, analysisStartIndex);

			// Сортируем модели и распределяем по децилям
			var sortedModels = modelsData
				.OrderBy(m => m.BenchValue)
				.ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
				.ToList();
			int totalModelsForDeciles = sortedModels.Count;

			TimeSeries[] decileSeriesArray = new TimeSeries[10];
			for (int g = 0; g < 10; g++)
				decileSeriesArray[g] = new TimeSeries(bars.DateTimes, 0.0);

			int[] decileCounts = new int[10];

			for (int i = 0; i < totalModelsForDeciles; i++)
			{
				int groupIndex = (int)(i * 10.0 / totalModelsForDeciles);
				if (groupIndex >= 10) groupIndex = 9;
				decileCounts[groupIndex]++;
				for (int bar = 0; bar < sortedModels[i].ForecastTS.Count; bar++)
					decileSeriesArray[groupIndex][bar] += sortedModels[i].ForecastTS[bar];
			}

			for (int bar = 0; bar < bars.Count; bar++)
				for (int g = 0; g < 10; g++)
					if (decileCounts[g] > 0)
						decileSeriesArray[g][bar] = (decileSeriesArray[g][bar] / decileCounts[g]) * 100.0;

			// Рассчитываем equity для каждого дециля
			double[] decileIndividualEquity = new double[10];
			double[] decileInEnsembleEquity = new double[10];
			double[] decileContribution = new double[10];
			double[] decileContributionPercent = new double[10];

			for (int g = 0; g < 10; g++)
			{
				// Индивидуальная equity дециля (торгуется сам по себе)
				TimeSeries indivQty = new TimeSeries(bars.DateTimes, 0.0);
				for (int i = 0; i < bars.Count; i++)
					indivQty[i] = decileSeriesArray[g][i] / 10.0;
				decileIndividualEquity[g] = CalculateEquity(indivQty, bars, analysisStartIndex);

				// Вклад дециля в ансамбль (пропорционально его весу)
				TimeSeries ensembleWithDecileQty = new TimeSeries(bars.DateTimes, 0.0);
				int decileWeight = decileCounts[g];

				for (int i = 0; i < bars.Count; i++)
				{
					// Доля этого дециля в общем ансамбле
					double decileContributionRaw = decileSeriesArray[g][i] * decileWeight / 100.0;
					if (packageModelCount <= 10)
						ensembleWithDecileQty[i] = decileContributionRaw;
					else
					{
						double gradFactor = packageModelCount / 10.0;
						ensembleWithDecileQty[i] = decileContributionRaw / gradFactor;
					}
				}
				decileInEnsembleEquity[g] = CalculateEquity(ensembleWithDecileQty, bars, analysisStartIndex);

				// Разница и процентный вклад
				decileContribution[g] = decileInEnsembleEquity[g];
				decileContributionPercent[g] = Math.Abs(ensembleEquity) > 0.001 ? (decileContribution[g] / ensembleEquity) * 100.0 : 0.0;
			}

			// Вывод таблицы
			BeginResearchReport($"decile_equity_contribution_{packageLabel}");
			WriteToDebugLog("", false);
			WriteToDebugLog("┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐", false);
			WriteToDebugLog($"│                                                   АНАЛИЗ ВКЛАДА ДЕЦИЛЕЙ В EQUITY ({packageLabel})                                                   │", false);
			WriteToDebugLog("├──────────┬─────────┬─────────────────┬──────────────────┬──────────────┬────────────┬────────────┤", false);
			WriteToDebugLog("│  Дециль  │ Моделей │  Индив. Equity  │ В ансамбле (сум) │    Вклад     │  % вклада  │   Эффект   │", false);
			WriteToDebugLog("├──────────┼─────────┼─────────────────┼──────────────────┼──────────────┼────────────┼────────────┤", false);

			for (int g = 0; g < 10; g++)
			{
				string effect = decileIndividualEquity[g] > decileInEnsembleEquity[g] ? "✗" : "✓";
				WriteToDebugLog($"│   {(g + 1) * 10,2}   │   {decileCounts[g],4}  │ {decileIndividualEquity[g],15:F2} │ {decileInEnsembleEquity[g],16:F2} │ {decileContribution[g],+12:F2} │ {decileContributionPercent[g],9:F1}% │    {effect}     │", false);
			}

			WriteToDebugLog("├──────────┼─────────┼─────────────────┼──────────────────┼──────────────┼────────────┼────────────┤", false);
			WriteToDebugLog($"│  ИТОГО   │ {packageModelCount,6} │ {"",-15} │ {ensembleEquity,+16:F2} │ {"",-12} │ {"100.0%",10} │ {"",-12} │", false);
			WriteToDebugLog("└──────────┴─────────┴─────────────────┴──────────────────┴──────────────┴────────────┴────────────┘", false);

			// Статистика
			var bestDecile = Enumerable.Range(0, 10).OrderByDescending(g => decileContributionPercent[g]).First();
			var worstDecile = Enumerable.Range(0, 10).OrderBy(g => decileContributionPercent[g]).First();

			WriteToDebugLog("", false);
			WriteToDebugLog($"  ★ Лучший дециль ({packageLabel}): #{bestDecile + 1} (вклад: {decileContributionPercent[bestDecile]:F1}%, индив. equity: {decileIndividualEquity[bestDecile]:F2})", false);
			WriteToDebugLog($"  ○ Худший дециль ({packageLabel}): #{worstDecile + 1} (вклад: {decileContributionPercent[worstDecile]:F1}%, индив. equity: {decileIndividualEquity[worstDecile]:F2})", false);

			// Сколько децилей работают в плюс
			int profitableDeciles = Enumerable.Range(0, 10).Count(g => decileIndividualEquity[g] > 0);
			WriteToDebugLog($"  Прибыльных децилей ({packageLabel}): {profitableDeciles} из 10", false);
			EndResearchReport();
		}

		#endregion

		#region Execute Method

		public override void Execute(BarHistory bars, int idx)
		{
			if (idx == 0) return;

			DateTime currentDate = bars.DateTimes[idx];
			bool isTeachEverythingWindow =
				(currentDate >= startDateTraining && currentDate <= endDateTraining) ||
				(currentDate >= startDateTraining2 && currentDate <= endDateTraining2);
			// In EMA50 control mode green identifies the actual control zone,
			// rather than the unrelated model-training windows.
			if (controlEnabled == 1 && controlScheme == 4)
			{
				if (disbalanceStateSeries != null && disbalanceStateSeries[idx] == 1.0)
					SetBackgroundColor(bars, idx, WLColor.FromArgb(128, WLColor.Green), "Price");
			}
			else if (!(controlEnabled == 1 && (controlScheme == 6 || controlScheme == 7)) && isTeachEverythingWindow)
				SetBackgroundColor(bars, idx, WLColor.FromArgb(20, WLColor.Green), "Price");
			if (aggregated_ensemble == null || aggregated_ensemble_RAW == null || totalModelCount == 0)
			{
				return;
			}
			double dailyProfit = bars.Close[idx] - bars.Close[idx - 1];

			double rawAvg = aggregated_ensemble_RAW[idx] / totalModelCount;
			int forecastValue = rawAvg >= level_class ? 1 : 0;
			int forecastPrevValue = idx > 0 ? (aggregated_ensemble_RAW[idx - 1] / totalModelCount >= level_class ? 1 : 0) : 0;

			#region Проторговка (Ensemble Logic)

			int currentQty = (int)OpenQuantity;
			int desiredQty = 0;
			double activeThreshold = decileThreshold;

			if (tradingMode == 0)
			{
				// Каждый пакет даёт собственный независимый блок из 10 позиций.
				// В Boss итог равен 0, 10, 20 или 30 без усреднения пакетов.
				foreach (ProcessResult result in packageResults)
				{
					if (result == null || result.ModelCount == 0) continue;
					double ensemblePercent = result.Ensemble[idx] * 100.0 / result.ModelCount;
					if (ensemblePercent >= activeThreshold)
						desiredQty += 10;
				}
			}
			else
			{
				// Независимые децильные сигналы: до 10 на пакет, до 30 в Boss.
				for (int g = 0; g < decileSeries.Length; g++)
				{
					if (this.decileSeries[g] != null && this.decileSeries[g][idx] >= activeThreshold)
					{
						desiredQty++;
					}
				}
			}

			desiredQty = ApplyPositionControl(bars, idx, currentQty, desiredQty);
			if (controlEnabled == 1 && (controlScheme == 6 || controlScheme == 7))
			{
				if (emaEpisodeStartBar == idx)
					SetBackgroundColor(bars, idx, WLColor.FromArgb(128, WLColor.Green), "Price");
				else if (emaEpisodeHeldBar == idx)
					SetBackgroundColor(bars, idx, WLColor.FromArgb(26, WLColor.Green), "Price");
			}

			if (currentQty < desiredQty)
			{
				while (currentQty < desiredQty) { PlaceTrade(bars, TransactionType.Buy, OrderType.Market); currentQty++; }
			}
			else if (currentQty > desiredQty)
			{
				while (currentQty > desiredQty) { PlaceTrade(bars, TransactionType.Sell, OrderType.Market); currentQty--; }
			}

			#endregion

			if (currentDate >= startDateTest && currentDate <= endDateTest)
			{
				if (((forecastValue == 1) && (forecastPrevValue == 1)) || ((forecastValue == 0) && (forecastPrevValue == 1)))
				{
					if (isTeachEverythingWindow)
						cumulative_test[idx] = cumulative_test[idx - 1] + testWeightDuringTraining * dailyProfit;
					else
						cumulative_test[idx] = cumulative_test[idx - 1] + dailyProfit;
				}
				else cumulative_test[idx] = cumulative_test[idx - 1];
			}
			else cumulative_test[idx] = cumulative_test[idx - 1];

			if (isTeachEverythingWindow)
			{
				if (((forecastValue == 1) && (forecastPrevValue == 1)) || ((forecastValue == 0) && (forecastPrevValue == 1)))
					cumulative_training[idx] = cumulative_training[idx - 1] + dailyProfit;
				else
					cumulative_training[idx] = cumulative_training[idx - 1];
			}
			else cumulative_training[idx] = cumulative_training[idx - 1];

		}

		#endregion

		#region Старые вспомогательные методы (оставлены по требованию)

		private void ExportSystemParametersToIni(string directoryPath) { }
		private void DeleteOldFiles(string directoryPath) { }
		private void RenameTemporaryFiles(string directoryPath) { }

		#endregion

		#region Indicator Extensions

		public static TimeSeries AGC(TimeSeries source)
		{
			TimeSeries result = new TimeSeries(source.DateTimes, true);
			double peak = 0.0000001;
			for (int bar = 0; bar < source.Count; bar++)
			{
				if (double.IsNaN(source[bar])) { result[bar] = 0; continue; }
				peak *= 0.991;
				peak = Math.Max(peak, Math.Abs(source[bar]));
				result[bar] = peak != 0 ? source[bar] / peak : 0;
			}
			return result;
		}

		public static TimeSeries AGC(IndicatorBase source)
		{
			TimeSeries input = source;
			TimeSeries result = new TimeSeries(input.DateTimes, true);
			double peak = 0.0000001;
			int startIndex = source.FirstValidIndex;
			for (int bar = startIndex; bar < input.Count; bar++)
			{
				peak *= 0.991;
				peak = Math.Max(peak, Math.Abs(input[bar]));
				result[bar] = peak != 0 ? input[bar] / peak : 0;
			}
			return result;
		}

		#endregion
	}
}

