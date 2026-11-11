using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using WealthLab.Backtest;
using WealthLab.Core;
using WealthLab.Indicators;
using System.Drawing;
using WealthLab.TASC;
using WealthLab.Data;
using System.Globalization;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using WealthLab.AdvancedSmoothers;

using Encog.ML.Data;
using Encog.ML.Data.Basic;
using Encog.Neural.Networks;
using Encog.Persist;

using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace WealthScript1 
{
	public class MyStrategy2779 : UserStrategyBase
	{
		#region Constants & Global Config Fields

		private int Day_100;
		private int AssemblerMode;

		// Глобальные параметры (fallback, если индивидуальный INI не найден)
		private double level_class;
		private int calculationMethod;
		private int shift;
		private int smoothingPeriod;
		private double reversalPercentage;
		private int addLayer;
		private List<string> ActiveFeatureNames = new List<string>();

		private const string TorchSharpRootDir = @"D:\TorchSharp";
		private const string Dir_ZZ1 = "trading_package";
		private const string Dir_ZZ2 = "trading_package_zz2";
		private const string Dir_ZZ1_J = "trading_package_J";
		private const string Dir_ZZ2_J = "trading_package_zz2_J";
		private const string EncogRootDir = @"D:\model_ensemble_Encog";
		private const string Dir_Encog_ZZ1 = "trading_package";
		private const string Dir_Encog_ZZ2 = "trading_package_zz2";

		#endregion

		#region Class Fields

		private TimeSeries predict;
		private TimeSeries cumulative_test;
		private TimeSeries cumulative_training;
		private TimeSeries aggregated_ensemble;
		private TimeSeries aggregated_ensemble_RAW;

		private readonly DateTime startDateTest = new DateTime(2018, 2, 26);
		private readonly DateTime endDateTest = new DateTime(2026, 7, 22);
		private readonly DateTime startDateTraining = new DateTime(2020, 2, 27);
		private readonly DateTime endDateTraining = new DateTime(2022, 10, 18);

		private const double testWeightDuringTraining = 0.99;

		private int totalModelCount;
		private CachedBaseIndicators _cachedIndicators;
		private CachedBaseIndicators _cachedIndicatorsPlus1;
		private CachedBaseIndicators _cachedIndicatorsMinus1;
		private CachedBaseIndicators _cachedIndicatorsPlus2;
		private CachedBaseIndicators _cachedIndicatorsMinus2;
		private Dictionary<string, double>[] _tomorrowFeatureValues;
		private Dictionary<string, double>[][] _tomorrowHistoryFeatureValues;

		// Для аналитики использования переменных x1-x15
		private List<bool[]> modelVariableUsage = new List<bool[]>();

		// === НОВОЕ: Режим торговли, порог и глобальный массив децилей ===
		private int tradingMode; // 0=Пропорц. ансамбль, 1=Независимые децили (>порог)
		private int decileThreshold; // Порог для децилей (38-64%)
		private int whatIfBars; // Глубина отображения WHAT-IF, только последние N баров
		private int tomorrowBars; // Глубина исторического прогноза на следующий бар
		private int tomorrowEnsembleMode; // 0 = Full Ensemble, 1 = Short Ensemble для TOMORROW

		// ===== НАСТРОЙКА МНОГОПОТОЧНОСТИ TOMORROW =====
		// Менять нужно только это число. 20 оставляет часть ресурсов Windows/Wealth-Lab.
		private const int MaxParallelProcessors = 20; // сохранено для экспериментов; optimized Tomorrow batch сейчас не использует Parallel.For
		private TimeSeries[] decileSeries = new TimeSeries[10]; // Глобальный массив децилей для Execute

		#endregion

		#region Constructor

		public MyStrategy2779()
		{
			StartIndex = 175;
			AddParameter("Day_100", ParameterType.Int32, 150, 5, 500, 5);
			AddParameter("1=ZZ1, 2=ZZ2, 3=ZZ1_J, 4=ZZ2_J, 5=Encog_ZZ1, 6=Encog_ZZ2, 7=ALL 6", ParameterType.Int32, 5, 1, 7, 1);
			AddParameter("дубль сделок", ParameterType.Int32, 1, 0, 1, 1);
			AddParameter("equity_pearson", ParameterType.Int32, 1, 0, 1, 1);

			// === НОВОЕ: Параметры режима торговли и порога ===
			AddParameter("Режим торговли", ParameterType.Int32, 1, 0, 1, 1);
			AddParameter("Порог децилей", ParameterType.Int32, 50, 38, 64, 2);
			AddParameter("WhatIfBars", ParameterType.Int32, 110, 10, 200, 10);
			AddParameter("TomorrowBars", ParameterType.Int32, 10, 5, 100, 5);
			AddParameter("Tomorrow Short Ensemble", ParameterType.Int32, 0, 0, 1, 1);
		}

		#endregion

		#region TorchSharp MLP-модель

		private sealed class MLPModel : nn.Module<Tensor, Tensor>
		{
			private readonly Linear hidden;
			private readonly Linear output;
			private readonly bool useSigmoidOutput;

			public MLPModel(int inputSize, int hiddenSize, int outputSize, int exitType) : base("MLPModel")
			{
				hidden = Linear(inputSize, hiddenSize);
				output = Linear(hiddenSize, outputSize);
				useSigmoidOutput = exitType == 1;
				RegisterComponents();
			}

			public override Tensor forward(Tensor x)
			{
				x = torch.sigmoid(hidden.forward(x));
				x = output.forward(x);

				if (useSigmoidOutput)
					x = torch.sigmoid(x);

				return x;
			}
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
			public TimeSeries EnsemblePlus1 { get; set; }
			public TimeSeries EnsembleMinus1 { get; set; }
			public TimeSeries EnsemblePlus2 { get; set; }
			public TimeSeries EnsembleMinus2 { get; set; }
			public double[] TomorrowClassifiedCounts { get; set; }
			public TimeSeries[] TomorrowHistoryCounts { get; set; }
			public int TomorrowModelCount { get; set; }
			public List<(string FileName, double BenchValue, TimeSeries ForecastTS)> AllModels { get; set; }
			public int ModelCount { get; set; }
			public int LastModelStartIndex { get; set; }
		}

		#endregion

		#region Model Parameters (для индивидуальной загрузки)

		private class ModelParameters
		{
			public double level_class = 0.5;
			public int calculationMethod = 1;
			public int shift = 3;
			public int smoothingPeriod = 8;
			public double reversalPercentage = 2.5;
			public int addLayer = 3;
			public int exit_type = 1;

			public float jordan_threshold = 0.5f;
			public float jordan_initial_context = 0.5f;
			public int jordan_mode = 1;
			public int base_input_size = 16;
			public int model_input_size = 18;
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

			// Инициализация глобального массива децилей
			for (int g = 0; g < 10; g++)
			{
				decileSeries[g] = new TimeSeries(bars.DateTimes, 0.0);
			}

			// СБРОС СОСТОЯНИЯ
			_cachedIndicators = null;
			_cachedIndicatorsPlus1 = null;
			_cachedIndicatorsMinus1 = null;
			_cachedIndicatorsPlus2 = null;
			_cachedIndicatorsMinus2 = null;
			ActiveFeatureNames = new List<string>();
			totalModelCount = 0;
			modelVariableUsage = new List<bool[]>();

			DateTime initializeStart = DateTime.Now;

			Day_100 = Parameters[0].AsInt;
			AssemblerMode = Parameters[1].AsInt;
			bool allSixMode = AssemblerMode == 7; // Режим сравнения всех шести ансамблей
			tradingMode = Parameters[4].AsInt; // Новый параметр
			decileThreshold = Parameters[5].AsInt; // Новый параметр
			whatIfBars = Parameters[6].AsInt; // WHAT-IF считаем только для последних N баров
			tomorrowBars = Parameters[7].AsInt; // TOMORROW-прогноз за последние N торговых дней
			tomorrowEnsembleMode = Parameters[8].AsInt; // 0=Full, 1=Short

			AuditHistory.Log($"=== РЕЖИМ ТОРГОВЛИ: {(tradingMode == 0 ? "ПРОПОРЦИОНАЛЬНЫЙ АНСАМБЛЬ" : "НЕЗАВИСИМЫЕ ДЕЦИЛИ (>" + decileThreshold + "%)")} ===", false);

			// 1. Загрузка глобальных параметров (fallback)
			string globalIniPath = Path.Combine(TorchSharpRootDir, "system_param.ini");
			if (File.Exists(globalIniPath))
			{
				LoadGlobalConfig(globalIniPath);
			}
			else
			{
				AuditHistory.Log($"ВНИМАНИЕ: Глобальный файл {globalIniPath} не найден. Используются значения по умолчанию.", false);
				level_class = 0.5;
				calculationMethod = 1;
				shift = 3;
				smoothingPeriod = 8;
				reversalPercentage = 2.5;
				addLayer = 3;
				ActiveFeatureNames = new List<string> { "model_cnn_fear_greed", "risk_measure", "model_panic_euphoria", "myRSI_rep", "trendScore", "aroonUp", "aroonDown", "dIMinus", "dIPlus", "correlationAngle_rep", "correlationTrend", "hurst", "cmf", "decibel", "jocker", "marketBreadth" };
			}

			// 2. Предварительное вычисление базовых индикаторов (ОДИН РАЗ)
			AuditHistory.Log("=== ПРЕДВАРИТЕЛЬНОЕ ВЫЧИСЛЕНИЕ БАЗОВЫХ ИНДИКАТОРОВ ===", false);
			DateTime cacheStart = DateTime.Now;
			_cachedIndicators = PrecomputeBaseIndicators(bars);
			DateTime cacheEnd = DateTime.Now;
			AuditHistory.Log($"Базовые индикаторы вычислены за {(cacheEnd - cacheStart).TotalSeconds:F2} секунд", false);

			// WHAT-IF +/-1% и +/-2% нужен только в обычных режимах 1..6.
			// В режиме 7 эти тяжелые расчеты полностью пропускаются.
			if (!allSixMode)
			{
				AuditHistory.Log("=== WHAT-IF PRICE SENSITIVITY: расчет +/-1% и +/-2% ===", false);
				DateTime whatIfStart = DateTime.Now;
				_cachedIndicatorsPlus1 = PrecomputeWhatIfIndicators(bars, 1.01, whatIfBars);
				_cachedIndicatorsMinus1 = PrecomputeWhatIfIndicators(bars, 0.99, whatIfBars);
				_cachedIndicatorsPlus2 = PrecomputeWhatIfIndicators(bars, 1.02, whatIfBars);
				_cachedIndicatorsMinus2 = PrecomputeWhatIfIndicators(bars, 0.98, whatIfBars);
				AuditHistory.Log($"WHAT-IF признаки ({whatIfBars} последних баров) вычислены за {(DateTime.Now - whatIfStart).TotalSeconds:F2} секунд", false);
			}
			else
			{
				AuditHistory.Log("=== РЕЖИМ 7: WHAT-IF +/-1% и +/-2% НЕ РАССЧИТЫВАЕТСЯ ===", false);
			}

			// Признаки для виртуального завтрашнего бара: -2%, -1%, 0%, +1%, +2% от последнего Close.
			// Внешние признаки фиксируются на последнем известном значении.
			_tomorrowFeatureValues = new[]
			{
				BuildTomorrowFeatureValues(bars, 0.98),
				BuildTomorrowFeatureValues(bars, 0.99),
				BuildTomorrowFeatureValues(bars, 1.00),
				BuildTomorrowFeatureValues(bars, 1.01),
				BuildTomorrowFeatureValues(bars, 1.02)
			};

			// Исторический TOMORROW нужен только в режимах 1..6.
			// В режиме 7 оставляем только актуальный TODAY -> TOMORROW для квадратов.
			if (!allSixMode)
			{
				_tomorrowHistoryFeatureValues = new Dictionary<string, double>[bars.Count][];
				double[] tomorrowFactors = { 0.98, 0.99, 1.00, 1.01, 1.02 };
				int tomorrowHistoryStart = Math.Max(StartIndex, bars.Count - tomorrowBars - 1);
				for (int t = tomorrowHistoryStart; t <= bars.Count - 2; t++)
				{
					_tomorrowHistoryFeatureValues[t] = new Dictionary<string, double>[5];
					for (int sc = 0; sc < 5; sc++)
						_tomorrowHistoryFeatureValues[t][sc] = BuildTomorrowFeatureValuesAtBar(bars, t, tomorrowFactors[sc]);
				}
			}
			else
			{
				_tomorrowHistoryFeatureValues = null;
				AuditHistory.Log("=== РЕЖИМ 7: исторический TOMORROW НЕ РАССЧИТЫВАЕТСЯ ===", false);
			}

			// Predict больше не отображается ни в одном режиме 1..7.
			// Поэтому отдельный расчет Predict здесь не выполняем.
			// Нужные ценовые оверлеи (ZigZag / SuperSmoother 8)
			// рисуются отдельно в конце Initialize по номеру режима.

			ProcessResult result1 = null;
			ProcessResult[] allSixResults = allSixMode ? new ProcessResult[6] : null;
			ProcessResult result2 = null; // оставлено для совместимости нижележащего кода; в режиме 1..6 обрабатывается один ансамбль
			TimeSeries aggregatedPlus1 = new TimeSeries(bars.DateTimes, 0.0);
			TimeSeries aggregatedMinus1 = new TimeSeries(bars.DateTimes, 0.0);
			TimeSeries aggregatedPlus2 = new TimeSeries(bars.DateTimes, 0.0);
			TimeSeries aggregatedMinus2 = new TimeSeries(bars.DateTimes, 0.0);

			// 3. Режимы 1..6: один выбранный ансамбль. Режим 7: сразу все шесть.
			string selectedDir = "";
			string selectedOutput = "";
			string selectedLabel = "";
			bool selectedIsJordan = false;
			bool selectedIsEncog = false;

			if (!allSixMode)
			{
				switch (AssemblerMode)
				{
					case 1: selectedDir = Dir_ZZ1; selectedOutput = "ensemblePR.txt"; selectedLabel = "ZZ1"; break;
					case 2: selectedDir = Dir_ZZ2; selectedOutput = "ensemblePR_ZZ2.txt"; selectedLabel = "ZZ2"; break;
					case 3: selectedDir = Dir_ZZ1_J; selectedOutput = "ensemblePR_J.txt"; selectedLabel = "ZZ1_Jordan"; selectedIsJordan = true; break;
					case 4: selectedDir = Dir_ZZ2_J; selectedOutput = "ensemblePR_ZZ2_J.txt"; selectedLabel = "ZZ2_Jordan"; selectedIsJordan = true; break;
					case 5: selectedDir = Dir_Encog_ZZ1; selectedOutput = "ensemblePR.txt"; selectedLabel = "Encog_ZZ1"; selectedIsEncog = true; break;
					case 6: selectedDir = Dir_Encog_ZZ2; selectedOutput = "ensemblePR_ZZ2.txt"; selectedLabel = "Encog_ZZ2"; selectedIsEncog = true; break;
					default: throw new InvalidOperationException($"Неизвестный AssemblerMode={AssemblerMode}");
				}

				string selectedPath = Path.Combine(selectedIsEncog ? EncogRootDir : TorchSharpRootDir, selectedDir);
				AuditHistory.Log($"=== РЕЖИМ АНСАМБЛЯ: {AssemblerMode} - {selectedLabel} ===", false);
				try
				{
					result1 = selectedIsEncog
						? ProcessEncogTradingPackage(bars, selectedPath, selectedOutput, selectedLabel)
						: ProcessTradingPackage(bars, selectedPath, selectedOutput, selectedLabel, selectedIsJordan);
				}
				catch (Exception ex)
				{
					AuditHistory.Log($"Ошибка при обработке {selectedLabel}: {ex.Message}", false);
				}
			}
			else
			{
				AuditHistory.Log("=== РЕЖИМ АНСАМБЛЯ 7: СРАВНЕНИЕ ВСЕХ 6 АНСАМБЛЕЙ ===", false);

				string[] dirs =
				{
					Dir_ZZ1, Dir_ZZ2, Dir_ZZ1_J, Dir_ZZ2_J, Dir_Encog_ZZ1, Dir_Encog_ZZ2
				};
				string[] outputs =
				{
					"ensemblePR.txt", "ensemblePR_ZZ2.txt", "ensemblePR_J.txt",
					"ensemblePR_ZZ2_J.txt", "ensemblePR.txt", "ensemblePR_ZZ2.txt"
				};
				string[] labels =
				{
					"ZZ1", "ZZ2", "ZZ1_Jordan", "ZZ2_Jordan", "Encog_ZZ1", "Encog_ZZ2"
				};
				bool[] jordanFlags = { false, false, true, true, false, false };
				bool[] encogFlags = { false, false, false, false, true, true };

				for (int p = 0; p < 6; p++)
				{
					string path = Path.Combine(encogFlags[p] ? EncogRootDir : TorchSharpRootDir, dirs[p]);
					try
					{
						AuditHistory.Log($"--- РЕЖИМ 7: обработка {labels[p]} ---", false);
						allSixResults[p] = encogFlags[p]
							? ProcessEncogTradingPackage(bars, path, outputs[p], labels[p])
							: ProcessTradingPackage(bars, path, outputs[p], labels[p], jordanFlags[p]);
					}
					catch (Exception ex)
					{
						AuditHistory.Log($"Ошибка при обработке {labels[p]}: {ex.Message}", false);
						allSixResults[p] = null;
					}
				}
			}

			if (!allSixMode)
			{
				// 4. Присвоение выбранного ансамбля
				if (result1 != null)
				{
					aggregated_ensemble = result1.Ensemble;
					aggregated_ensemble_RAW = result1.EnsembleRAW;
					totalModelCount = result1.ModelCount;
					aggregatedPlus1 = result1.EnsemblePlus1;
					aggregatedMinus1 = result1.EnsembleMinus1;
					aggregatedPlus2 = result1.EnsemblePlus2;
					aggregatedMinus2 = result1.EnsembleMinus2;
				}

				// 5. Прорисовка
				TimeSeries aggregated_ensemblePR_J = new TimeSeries(bars.DateTimes, 0);
				if (totalModelCount > 0)
				{
					for (int i = 0; i < aggregated_ensemble.Count; i++)
						aggregated_ensemblePR_J[i] = (aggregated_ensemble[i] / totalModelCount) * 100.0;
				}
				PlotTimeSeries(aggregated_ensemblePR_J, "aggregated_ensemblePR_J", "Ensemble %", WLColor.Purple, PlotStyle.Line);
				SetPaneDrawingOptions("Ensemble %", 180);

				// WHAT-IF коридор чувствительности ансамбля к изменению цены
				if (totalModelCount > 0)
				{
					TimeSeries prPlus1 = new TimeSeries(bars.DateTimes, double.NaN);
					TimeSeries prMinus1 = new TimeSeries(bars.DateTimes, double.NaN);
					TimeSeries prPlus2 = new TimeSeries(bars.DateTimes, double.NaN);
					TimeSeries prMinus2 = new TimeSeries(bars.DateTimes, double.NaN);
					int whatIfStartBar = Math.Max(0, bars.Count - whatIfBars);
					for (int i = whatIfStartBar; i < bars.Count; i++)
					{
						prPlus1[i] = (aggregatedPlus1[i] / totalModelCount) * 100.0;
						prMinus1[i] = (aggregatedMinus1[i] / totalModelCount) * 100.0;
						prPlus2[i] = (aggregatedPlus2[i] / totalModelCount) * 100.0;
						prMinus2[i] = (aggregatedMinus2[i] / totalModelCount) * 100.0;
					}

					// Горизонтальный ориентир 50% на панели ансамбля.
					TimeSeries level50 = new TimeSeries(bars.DateTimes, 50.0);
					PlotTimeSeriesLine(level50, "50%", "Ensemble %", WLColor.Gray, 1, LineStyle.Dashed);

					PlotTimeSeriesLine(prPlus1, "Aggregate PR +1%", "Ensemble %", WLColor.Green, 1, LineStyle.Dashed);
					PlotTimeSeriesLine(prPlus2, "Aggregate PR +2%", "Ensemble %", WLColor.Green, 1, LineStyle.Dotted);
					PlotTimeSeriesLine(prMinus1, "Aggregate PR -1%", "Ensemble %", WLColor.Red, 1, LineStyle.Dashed);
					PlotTimeSeriesLine(prMinus2, "Aggregate PR -2%", "Ensemble %", WLColor.Red, 1, LineStyle.Dotted);

					// ============================================================
					// MARKET STABILITY / ORDER VIOLATIONS
					//
					// Нормальный порядок:
					//   PR -2% <= PR -1% <= PR 0% <= PR +1% <= PR +2%
					//
					// Значение 0  = все пять кривых стоят в правильном порядке.
					// Значение 1..10 = количество попарных инверсий.
					// Максимум 10 = полностью обратный порядок пяти кривых.
					// Равные значения нарушением порядка не считаются.
					// ============================================================
					TimeSeries marketStability = new TimeSeries(bars.DateTimes, double.NaN);

					for (int i = whatIfStartBar; i < bars.Count; i++)
					{
						double[] orderValues =
						{
						prMinus2[i],
						prMinus1[i],
						aggregated_ensemblePR_J[i],
						prPlus1[i],
						prPlus2[i]
					};

						bool valid = true;
						for (int k = 0; k < orderValues.Length; k++)
						{
							if (double.IsNaN(orderValues[k]))
							{
								valid = false;
								break;
							}
						}

						if (!valid)
							continue;

						int inversions = 0;
						for (int a = 0; a < orderValues.Length - 1; a++)
						{
							for (int b = a + 1; b < orderValues.Length; b++)
							{
								if (orderValues[a] > orderValues[b])
									inversions++;
							}
						}

						marketStability[i] = inversions;
					}

					PlotTimeSeries(
						marketStability,
						"Market",
						"Market Stability",
						WLColor.Blue,
						PlotStyle.Line);

					SetPaneDrawingOptions("Market Stability", 60);

					TimeSeries stabilityZero = new TimeSeries(bars.DateTimes, 0.0);
					PlotTimeSeriesLine(
						stabilityZero,
						" ",
						"Market Stability",
						WLColor.Gray,
						1,
						LineStyle.Dashed);
				}


				// TOMORROW PR — маркеры прогноза следующего торгового бара на общей панели Ensemble %.
				// Режим Full использует все модели; Short — только модели без неизвестных внешних t+1 признаков.
				int tomorrowTotalModelCount = result1 != null ? result1.TomorrowModelCount : 0;
				if (tomorrowTotalModelCount > 0 && bars.Count > 0)
				{
					TimeSeries[] tomorrowPlot = new TimeSeries[5];
					for (int sc = 0; sc < 5; sc++) tomorrowPlot[sc] = new TimeSeries(bars.DateTimes, double.NaN);

					// Исторические прогнозы: рассчитанный на баре t прогноз рисуем на фактическом баре t+1.
					TimeSeries[] histCombined = new TimeSeries[5];
					for (int sc = 0; sc < 5; sc++)
						histCombined[sc] = (result1 != null && result1.TomorrowHistoryCounts != null) ? result1.TomorrowHistoryCounts[sc] : new TimeSeries(bars.DateTimes, 0.0);

					int firstDisplay = Math.Max(1, bars.Count - tomorrowBars);
					for (int sc = 0; sc < 5; sc++)
						for (int i = firstDisplay; i < bars.Count; i++)
							if (histCombined[sc][i] > 0.0 || i < bars.Count - 1)
								tomorrowPlot[sc][i] = histCombined[sc][i] / tomorrowTotalModelCount * 100.0;

					// Самый свежий прогноз (реального t+1 еще нет) показываем на последнем реальном баре.
					double[] tomorrowCountsCombined = new double[5];
					if (result1 != null && result1.TomorrowClassifiedCounts != null)
						Array.Copy(result1.TomorrowClassifiedCounts, tomorrowCountsCombined, 5);

					double[] tomorrowPR = new double[5];
					int last = bars.Count - 1;
					for (int sc = 0; sc < 5; sc++)
					{
						tomorrowPR[sc] = tomorrowCountsCombined[sc] / tomorrowTotalModelCount * 100.0;
					}

					string[] names = { "Tomorrow -2%", "Tomorrow -1%", "Tomorrow 0%", "Tomorrow +1%", "Tomorrow +2%" };
					WLColor[] colors = { WLColor.Red, WLColor.Red, WLColor.Purple, WLColor.Green, WLColor.Green };

					// ===== НАСТРОЙКА ФОНА TOMORROW НА ПАНЕЛИ Ensemble % =====
					// Значения задаются в ПРОЦЕНТАХ непрозрачности (0..100).
					// Менять нужно ТОЛЬКО эти три числа — они действуют и на всю историю, и на последний бар.
					const double TomorrowBgWeak = 4.0;   // 3 против 2
					const double TomorrowBgMedium = 7.0;  // 4 против 1
					const double TomorrowBgStrong = 10.0;  // 5 против 0 (максимум)
														   // =========================================================
														   // Точное попадание сценария в 50% считается нейтральным и уменьшает насыщенность.
					for (int i = firstDisplay; i < last; i++)
					{
						int above50 = 0, below50 = 0;
						for (int sc = 0; sc < 5; sc++)
						{
							double v = tomorrowPlot[sc][i];
							if (double.IsNaN(v)) continue;
							if (v > 50.0) above50++;
							else if (v < 50.0) below50++;
						}
						int balance = above50 - below50;
						if (balance != 0)
						{
							int absBalance = Math.Abs(balance);
							double opacityPercent = absBalance >= 5 ? TomorrowBgStrong : (absBalance >= 3 ? TomorrowBgMedium : TomorrowBgWeak);
							int alpha = (int)Math.Round(255.0 * opacityPercent / 100.0);
							alpha = Math.Max(0, Math.Min(255, alpha));
							WLColor bg = balance > 0
								? new WLColor((byte)alpha, (byte)0, (byte)128, (byte)0)
								: new WLColor((byte)alpha, (byte)255, (byte)0, (byte)0);
							SetBackgroundColor(bars, i, bg, "Ensemble %");
						}
					}

					// Для последнего бара фон определяется АКТУАЛЬНЫМ прогнозом TODAY -> TOMORROW,
					// а не историческим прогнозом вчера -> сегодня.
					int lastAbove50 = 0, lastBelow50 = 0;
					for (int sc = 0; sc < 5; sc++)
					{
						if (tomorrowPR[sc] > 50.0) lastAbove50++;
						else if (tomorrowPR[sc] < 50.0) lastBelow50++;
					}
					int lastBalance = lastAbove50 - lastBelow50;
					if (lastBalance != 0)
					{
						int lastAbsBalance = Math.Abs(lastBalance);
						double lastOpacityPercent = lastAbsBalance >= 5 ? TomorrowBgStrong : (lastAbsBalance >= 3 ? TomorrowBgMedium : TomorrowBgWeak);
						int lastAlpha = (int)Math.Round(255.0 * lastOpacityPercent / 100.0);
						lastAlpha = Math.Max(0, Math.Min(255, lastAlpha));
						WLColor lastBg = lastBalance > 0
							? new WLColor((byte)lastAlpha, (byte)0, (byte)128, (byte)0)
							: new WLColor((byte)lastAlpha, (byte)255, (byte)0, (byte)0);
						SetBackgroundColor(bars, last, lastBg, "Ensemble %");
					}

					// Актуальный прогноз TODAY -> TOMORROW: крупный полый квадрат на последнем баре.
					// Рисуем ЕГО ПЕРВЫМ, чтобы исторический маленький кружок (вчера -> сегодня)
					// затем оказался поверх квадрата, если значения совпадут или почти совпадут.
					for (int sc = 0; sc < 5; sc++)
						DrawText("□", last, tomorrowPR[sc], colors[sc], 20, "Ensemble %", true);

					// Исторический TOMORROW оставляем маленькими круглыми точками.
					// ВАЖНО: последний исторический прогноз больше НЕ заменяется актуальным прогнозом на завтра.
					for (int sc = 0; sc < 5; sc++)
						PlotTimeSeries(tomorrowPlot[sc], names[sc], "Ensemble %", colors[sc], PlotStyle.Dots);
					TimeSeries tomorrow50 = new TimeSeries(bars.DateTimes, 50.0);
					// Общая линия 50% уже нарисована на панели Ensemble %.

					// ============================================================
					// TOMORROW STABILITY / ORDER VIOLATIONS
					//
					// Историческая часть:
					//   используем tomorrowPlot[0..4] (вчера -> сегодня).
					//
					// Последний бар:
					//   используем tomorrowPR[0..4] (сегодня -> завтра).
					//
					// Порядок считается нормальным, если:
					//   -2% <= -1% <= 0% <= +1% <= +2%
					//
					// Метрика = число попарных инверсий (0..10).
					// 0  = полный порядок,
					// 10 = максимально перепутанный порядок.
					// ============================================================
					TimeSeries tomorrowStability = new TimeSeries(bars.DateTimes, double.NaN);

					for (int i = firstDisplay; i < last; i++)
					{
						double[] orderValues =
						{
						tomorrowPlot[0][i],
						tomorrowPlot[1][i],
						tomorrowPlot[2][i],
						tomorrowPlot[3][i],
						tomorrowPlot[4][i]
					};

						bool valid = true;
						for (int k = 0; k < orderValues.Length; k++)
						{
							if (double.IsNaN(orderValues[k]))
							{
								valid = false;
								break;
							}
						}

						if (!valid)
							continue;

						int inversions = 0;
						for (int a = 0; a < orderValues.Length - 1; a++)
						{
							for (int b = a + 1; b < orderValues.Length; b++)
							{
								if (orderValues[a] > orderValues[b])
									inversions++;
							}
						}

						tomorrowStability[i] = inversions;
					}

					int currentTomorrowInversions = 0;
					for (int a = 0; a < tomorrowPR.Length - 1; a++)
					{
						for (int b = a + 1; b < tomorrowPR.Length; b++)
						{
							if (tomorrowPR[a] > tomorrowPR[b])
								currentTomorrowInversions++;
						}
					}
					// Последнее историческое значение НЕ подменяем актуальным прогнозом TODAY -> TOMORROW.
					// На истории оставляем только кружочки, а текущий прогноз показываем отдельным квадратиком.
					tomorrowStability[last] = double.NaN;

					// TOMORROW Stability рисуем на ТОЙ ЖЕ панели, что и Market Stability.
					// История (вчера -> сегодня) — зелёные кружочки.
					PlotTimeSeries(
						tomorrowStability,
						"Tomorrow",
						"Market Stability",
						WLColor.Green,
						PlotStyle.Dots);

					// Последний актуальный прогноз TODAY -> TOMORROW:
					// отдельный зелёный квадратик на последнем баре той же панели.
					DrawText("□", last, currentTomorrowInversions, WLColor.Green, 16, "Market Stability", true);

					AuditHistory.Log($"TOMORROW {(tomorrowEnsembleMode == 0 ? "FULL" : "SHORT")} Ensemble: {tomorrowTotalModelCount} models | PR: -2%={tomorrowPR[0]:F2}%  -1%={tomorrowPR[1]:F2}%  0%={tomorrowPR[2]:F2}%  +1%={tomorrowPR[3]:F2}%  +2%={tomorrowPR[4]:F2}% | Tomorrow Stability={currentTomorrowInversions}", false);
				}

				// 6. Децили и Leave-One-Out анализ
				List<(string FileName, double BenchValue, TimeSeries ForecastTS)> modelsForDeciles = result1?.AllModels;
				int lastStartIndexForDeciles = result1 != null ? result1.LastModelStartIndex : StartIndex;
				if (modelsForDeciles != null && modelsForDeciles.Count > 0)
				{
					string decileLabel = selectedLabel;

					// Явная проверка источника моделей для децилей (для уверенности пользователя)
					if (false && result1 != null && result2 != null)
					{
						AuditHistory.Log($"[ПРОВЕРКА АССЕМБЛЕРА] Режим 3: модели для децилей берутся СУММАРНО. Package1: {result1.ModelCount} шт., Package2: {result2.ModelCount} шт. ИТОГО в объединенном списке: {modelsForDeciles.Count} шт.", false);
					}

					BuildAndPlotDeciles(bars, modelsForDeciles, decileLabel, totalModelCount);
					BuildLeaveOneOutAnalysis(bars, modelsForDeciles, decileLabel, totalModelCount, lastStartIndexForDeciles, aggregated_ensemble);
					BuildDecileContributionAnalysis(bars, modelsForDeciles, decileLabel, totalModelCount, lastStartIndexForDeciles, aggregated_ensemble); // === НОВОЕ ===
				}

				// === АНАЛИТИКА ПО ИСПОЛЬЗОВАНИЮ ПЕРЕМЕННЫХ x1...x15 (ВСЕГДА!) ===
				if (totalModelCount > 0 && modelVariableUsage.Count > 0)
				{
					AuditHistory.Log("", false);
					AuditHistory.Log("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
					AuditHistory.Log("│                                      АНАЛИТИКА ПО ИСПОЛЬЗОВАНИЮ ПЕРЕМЕННЫХ x1...x15                                            │", false);
					AuditHistory.Log("├────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);

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
					AuditHistory.Log(headerRow, false);

					string percentRow = "│";
					for (int i = 0; i < 15; i++)
					{
						double usagePercent = (double)usageCount[i] / modelVariableUsage.Count * 100.0;
						percentRow += $" {usagePercent,4:F0}%│";
					}
					AuditHistory.Log(percentRow, false);

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

					AuditHistory.Log("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
					AuditHistory.Log("│                                    ПАРНОЕ ИСПОЛЬЗОВАНИЕ (%)                                                                    │", false);
					AuditHistory.Log("├────────────┬────┬────┬────────┬────┬────┬────────┬────┬────┬────────┬────┬────┤", false);
					AuditHistory.Log("│        │ x1 │ x2 │ x3 │ x4 │ x5 │ x6 │ x7 │ x8 │ x9 │x10 │x11 │x12 │x13 │x14 │x15│", false);
					AuditHistory.Log("├────────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┼────┤", false);

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
						AuditHistory.Log(pairRow, false);
					}
					AuditHistory.Log("└────────────────┴────┴────────┴────┴────┴────────┴────────┴────────┴────┴────", false);
				}

				// СРАВНЕНИЕ МОДЕЛЕЙ ПО СДЕЛКАМ (дубль сделок ±1 бар) - ТОЛЬКО ИТОГ > 80%
				if (Parameters[2].AsInt == 0 && modelsForDeciles != null && modelsForDeciles.Count > 1)
				{
					List<(string, string, double)> tradeMatchComparisons = new List<(string, string, double)>();

					for (int i = 0; i < modelsForDeciles.Count; i++)
					{
						for (int j = i + 1; j < modelsForDeciles.Count; j++)
						{
							var modelA = modelsForDeciles[i];
							var modelB = modelsForDeciles[j];

							List<int> tradesA = new List<int>();
							List<int> tradesB = new List<int>();

							for (int k = 1; k < modelA.ForecastTS.Count; k++)
							{
								if (modelA.ForecastTS[k] != modelA.ForecastTS[k - 1]) tradesA.Add(k);
								if (modelB.ForecastTS[k] != modelB.ForecastTS[k - 1]) tradesB.Add(k);
							}

							int totalTrades = tradesA.Count + tradesB.Count;
							var matchedA = new HashSet<int>();
							var matchedB = new HashSet<int>();

							foreach (int ta in tradesA)
								foreach (int tb in tradesB)
									if (Math.Abs(ta - tb) <= 1)
									{
										matchedA.Add(ta);
										matchedB.Add(tb);
									}

							double matchPercent = totalTrades == 0 ? 1.0 : (double)(matchedA.Count + matchedB.Count) / totalTrades;
							tradeMatchComparisons.Add((modelA.FileName, modelB.FileName, matchPercent));
						}
					}

					// БЛИЗКИЕ МОДЕЛИ ПО СДЕЛКАМ (>80%)
					double tradeMatchThreshold = 0.8;
					List<(string, string, double)> highMatchPairs = tradeMatchComparisons
						.Where(c => c.Item3 >= tradeMatchThreshold)
						.ToList();

					if (highMatchPairs.Any())
					{
						AuditHistory.Log("", false);
						AuditHistory.Log("┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐", false);
						AuditHistory.Log("│                                          БЛИЗКИЕ МОДЕЛИ ПО СДЕЛКАМ (>80%)                                                   │", false);
						AuditHistory.Log("├────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤", false);
						foreach (var (modelA, modelB, matchPercent) in highMatchPairs)
						{
							AuditHistory.Log($"│ {modelA,-35} и {modelB,-35} │ {matchPercent,18:P2} │", false);
						}
						AuditHistory.Log("└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘", false);
					}
				}

				// КОРРЕЛЯЦИЯ ПИРСОНА ПО ЭКВИТИ - ТОЛЬКО ИТОГ >= 0.9
				if (Parameters[3].AsInt == 0 && modelsForDeciles != null && modelsForDeciles.Count > 1)
				{
					List<(string, string, double)> equityCorrelationComparisons = new List<(string, string, double)>();

					for (int i = 0; i < modelsForDeciles.Count; i++)
					{
						for (int j = i + 1; j < modelsForDeciles.Count; j++)
						{
							var modelA = modelsForDeciles[i];
							var modelB = modelsForDeciles[j];

							List<double> returnsA = new List<double>();
							List<double> returnsB = new List<double>();

							int startIdx = Math.Max(StartIndex, 1);
							for (int k = startIdx; k < Math.Min(modelA.ForecastTS.Count, modelB.ForecastTS.Count); k++)
							{
								double retA = modelA.ForecastTS[k] - modelA.ForecastTS[k - 1];
								double retB = modelB.ForecastTS[k] - modelB.ForecastTS[k - 1];

								if (!double.IsNaN(retA) && !double.IsNaN(retB))
								{
									returnsA.Add(retA);
									returnsB.Add(retB);
								}
							}

							if (returnsA.Count < 2) continue;

							double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
							int n = returnsA.Count;

							for (int k = 0; k < n; k++)
							{
								sumX += returnsA[k];
								sumY += returnsB[k];
								sumXY += returnsA[k] * returnsB[k];
								sumX2 += returnsA[k] * returnsA[k];
								sumY2 += returnsB[k] * returnsB[k];
							}

							double numerator = sumXY - (sumX * sumY) / n;
							double denominator = Math.Sqrt(sumX2 - (sumX * sumX) / n) * Math.Sqrt(sumY2 - (sumY * sumY) / n);
							double correlation = denominator == 0 ? 0 : numerator / denominator;

							equityCorrelationComparisons.Add((modelA.FileName, modelB.FileName, correlation));
						}
					}

					// СИЛЬНО КОРРЕЛИРОВАННЫЕ МОДЕЛИ ПО ДОХОДНОСТЯМ ЭКВИТИ
					double equityCorrelationThreshold = 0.9;
					List<(string, string, double)> highlyCorrelatedEquityModels =
						equityCorrelationComparisons.Where(c => Math.Abs(c.Item3) >= equityCorrelationThreshold).ToList();

					if (highlyCorrelatedEquityModels.Any())
					{
						AuditHistory.Log("", false);
						AuditHistory.Log("┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐", false);
						AuditHistory.Log("│                         СИЛЬНО КОРРЕЛИРОВАННЫЕ МОДЕЛИ ПО ДОХОДНОСТЯМ ЭКВИТИ                         │", false);
						AuditHistory.Log("├────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤", false);
						foreach (var (modelA, modelB, correlation) in highlyCorrelatedEquityModels)
						{
							AuditHistory.Log($"│ {modelA,-35} и {modelB,-35} │ {correlation,10:F2} │", false);
						}
						AuditHistory.Log("└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘", false);
					}
				}

			}
			else
			{
				// ============================================================
				// РЕЖИМ 7 — СРАВНЕНИЕ ВСЕХ ШЕСТИ АГРЕГИРОВАННЫХ АНСАМБЛЕЙ
				//
				// Не строим и не рассчитываем:
				//   - WHAT-IF +/-1% и +/-2%;
				//   - исторические TOMORROW-точки;
				//   - Market Stability / Tomorrow Stability;
				//   - Predict;
				//   - децили и анализы, относящиеся к одному выбранному пакету.
				//
				// Строим:
				//   - 6 базовых Aggregate PR на одной панели Ensemble %;
				//   - только актуальные TODAY -> TOMORROW квадраты:
				//     красные для -2/-1%, зеленые для +1/+2%;
				//   - на ценовом графике ZigZag + SuperSmoother(8).
				// ============================================================
				string[] mode7Labels =
				{
					"ZZ1", "ZZ2", "ZZ1_Jordan", "ZZ2_Jordan", "Encog_ZZ1", "Encog_ZZ2"
				};

				WLColor[] mode7Colors =
				{
					WLColor.Purple,
					WLColor.Blue,
					WLColor.Orange,
					WLColor.Gold,
					WLColor.DarkGreen,
					WLColor.Red
				};

				// Суммарное число ансамблей, находящихся строго выше уровня 50%.
				// Каждый из шести ансамблей даёт 1 при PR > 50 и 0 в остальных случаях.
				// Поэтому итоговый ряд всегда лежит в фиксированном диапазоне 0..6.
				TimeSeries ensemblesAbove50 = new TimeSeries(bars.DateTimes, 0.0);

				for (int p = 0; p < 6; p++)
				{
					ProcessResult r = allSixResults[p];
					if (r == null || r.Ensemble == null || r.ModelCount <= 0)
						continue;

					TimeSeries pr = new TimeSeries(bars.DateTimes, 0.0);
					for (int i = 0; i < bars.Count; i++)
					{
						pr[i] = (r.Ensemble[i] / r.ModelCount) * 100.0;
						if (pr[i] > 50.0)
							ensemblesAbove50[i] += 1.0;
					}

					PlotTimeSeries(
						pr,
						mode7Labels[p],
						"Ensemble %",
						mode7Colors[p],
						PlotStyle.Line);
				}

				SetPaneDrawingOptions("Ensemble %", 180);

				// Общий ориентир 50%.
				TimeSeries mode7Level50 = new TimeSeries(bars.DateTimes, 50.0);
				PlotTimeSeriesLine(mode7Level50, "50%", "Ensemble %", WLColor.Gray, 1, LineStyle.Dashed);

				// Компактная полоса под основным графиком: 0 — все шесть ниже 50%,
				// 6 — все шесть выше 50%. Направляющие фиксируют шкалу 0..6.
				PlotTimeSeries(
					ensemblesAbove50,
					"Ensembles > 50%",
					"Ensembles 0-6",
					WLColor.DarkBlue,
					PlotStyle.Line);
				SetPaneDrawingOptions("Ensembles 0-6", 50);
				TimeSeries ensembleCountZero = new TimeSeries(bars.DateTimes, 0.0);
				TimeSeries ensembleCountSix = new TimeSeries(bars.DateTimes, 6.0);
				PlotTimeSeriesLine(ensembleCountZero, "0", "Ensembles 0-6", WLColor.Gray, 1, LineStyle.Dotted);
				PlotTimeSeriesLine(ensembleCountSix, "6", "Ensembles 0-6", WLColor.Gray, 1, LineStyle.Dotted);

				// Только текущие TODAY -> TOMORROW квадраты.
				// Центральный 0% квадрат в режиме 7 намеренно не рисуем:
				// пользователь попросил оставить именно красные и зеленые квадраты.
				if (bars.Count > 0)
				{
					int last = bars.Count - 1;
					int[] squareScenarios = { 0, 1, 3, 4 }; // -2%, -1%, +1%, +2%
					WLColor[] squareColors = { WLColor.Red, WLColor.Red, WLColor.Green, WLColor.Green };

					for (int p = 0; p < 6; p++)
					{
						ProcessResult r = allSixResults[p];
						if (r == null ||
							r.TomorrowClassifiedCounts == null ||
							r.TomorrowModelCount <= 0)
							continue;

						for (int s = 0; s < squareScenarios.Length; s++)
						{
							int sc = squareScenarios[s];
							double value = r.TomorrowClassifiedCounts[sc] / r.TomorrowModelCount * 100.0;
							DrawText("□", last, value, squareColors[s], 18, "Ensemble %", true);
						}
					}
				}
			}

			// ============================================================
			// ЦЕНОВЫЕ ОВЕРЛЕИ ПО РЕЖИМУ
			//
			// 1,3,5 : ZigZag
			// 2,4,6 : SuperSmoother(8)
			// 7     : ZigZag + SuperSmoother(8)
			//
			// Predict-панель при этом нигде не строится.
			// ============================================================
			if (AssemblerMode == 1 || AssemblerMode == 3 || AssemblerMode == 5 || AssemblerMode == 7)
			{
				DrawZigZagOnPrice(bars);
			}

			if (AssemblerMode == 2 || AssemblerMode == 4 || AssemblerMode == 6 || AssemblerMode == 7)
			{
				PlotSuperSmoother8OnPrice(bars);
			}

			SetBackgroundColorAllPanes(bars, bars.Count - Day_100, WLColor.FromArgb(100, WLColor.Green));

			DateTime initializeEnd = DateTime.Now;
			AuditHistory.Log($"=== ОБЩЕЕ ВРЕМЯ ВЫПОЛНЕНИЯ INITIALIZE: {(initializeEnd - initializeStart).TotalSeconds:F2} сек ===", false);
		}

		#region Global Config Loading

		private void LoadGlobalConfig(string iniFilePath)
		{
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

			level_class = double.Parse(GetVal("level_class", "0.5"), CultureInfo.InvariantCulture);
			calculationMethod = int.Parse(GetVal("CalculationMethod", "1"), CultureInfo.InvariantCulture);
			shift = int.Parse(GetVal("Shift", "3"), CultureInfo.InvariantCulture);
			smoothingPeriod = int.Parse(GetVal("SmoothingPeriod", "8"), CultureInfo.InvariantCulture);
			reversalPercentage = double.Parse(GetVal("ReversalPercentage", "2.5"), CultureInfo.InvariantCulture);
			addLayer = int.Parse(GetVal("addLayer", "3"), CultureInfo.InvariantCulture);

			string header = GetVal("ALLall_Header", "model_cnn_fear_greed,risk_measure,model_panic_euphoria,myRSI_rep,trendScore,aroonUp,aroonDown,dIMinus,dIPlus,correlationAngle_rep,correlationTrend,hurst,cmf,decibel,jocker,marketBreadth");
			ActiveFeatureNames = header.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
		}

		#endregion

		#region Load Individual Model Parameters

		private ModelParameters LoadIndividualModelParameters(string iniFilePath, string modelFileName)
		{
			if (!File.Exists(iniFilePath))
			{
				AuditHistory.Log($"  [ПРОПУСК] Модель {modelFileName} пропущена: не найден соответствующий INI-файл ({Path.GetFileName(iniFilePath)}).", false);
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

			pars.level_class = double.Parse(GetVal("level_class", level_class.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
			pars.calculationMethod = int.Parse(GetVal("CalculationMethod", calculationMethod.ToString()), CultureInfo.InvariantCulture);
			pars.shift = int.Parse(GetVal("Shift", shift.ToString()), CultureInfo.InvariantCulture);
			pars.smoothingPeriod = int.Parse(GetVal("SmoothingPeriod", smoothingPeriod.ToString()), CultureInfo.InvariantCulture);
			pars.reversalPercentage = double.Parse(GetVal("ReversalPercentage", reversalPercentage.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
			pars.addLayer = int.Parse(GetVal("addLayer", addLayer.ToString()), CultureInfo.InvariantCulture);
			pars.exit_type = int.Parse(GetVal("exit_type", "1"), CultureInfo.InvariantCulture);

			pars.jordan_threshold = float.Parse(GetVal("jordan_threshold", "0.5"), CultureInfo.InvariantCulture);
			pars.jordan_initial_context = float.Parse(GetVal("jordan_initial_context", "0.5"), CultureInfo.InvariantCulture);
			pars.jordan_mode = int.Parse(GetVal("jordan_mode", "1"), CultureInfo.InvariantCulture);
			pars.base_input_size = int.Parse(GetVal("base_input_size", "16"), CultureInfo.InvariantCulture);
			pars.model_input_size = int.Parse(GetVal("model_input_size", "18"), CultureInfo.InvariantCulture);

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

		#endregion

		private bool IsEligibleForTomorrowShortEnsemble(string iniFilePath)
		{
			if (!File.Exists(iniFilePath)) return false;
			var ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (string raw in File.ReadAllLines(iniFilePath))
			{
				string line = raw.Trim();
				if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[")) continue;
				int eq = line.IndexOf('=');
				if (eq <= 0) continue;
				ini[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
			}
			const double threshold = 0.001;
			string[] unknownTomorrow = { "x1", "x2", "x3", "x15", "x16" };
			foreach (string key in unknownTomorrow)
			{
				if (ini.TryGetValue(key, out string text) &&
					double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) &&
					Math.Abs(v) > threshold) return false;
			}
			return true;
		}


		#region Encog Integration

		private sealed class EncogModelParameters
		{
			public double LevelClass;
			public int CalculationMethod;
			public int Shift;
			public int SmoothingPeriod;
			public double ReversalPercentage;
			public double[] X = new double[15];
			public List<string> Features = new List<string>();
		}

		private EncogModelParameters LoadEncogParameters(string iniFilePath)
		{
			var ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (string raw in File.ReadAllLines(iniFilePath))
			{
				string line = raw.Trim();
				if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[")) continue;
				int eq = line.IndexOf('=');
				if (eq > 0) ini[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
			}
			string Req(string key)
			{
				if (!ini.TryGetValue(key, out string v) || string.IsNullOrWhiteSpace(v)) throw new Exception($"В INI отсутствует {key}");
				return v;
			}
			var p = new EncogModelParameters();
			p.LevelClass = double.Parse(Req("level_class"), CultureInfo.InvariantCulture);
			p.CalculationMethod = int.Parse(Req("CalculationMethod"), CultureInfo.InvariantCulture);
			p.Shift = int.Parse(Req("Shift"), CultureInfo.InvariantCulture);
			p.SmoothingPeriod = int.Parse(Req("SmoothingPeriod"), CultureInfo.InvariantCulture);
			p.ReversalPercentage = double.Parse(Req("ReversalPercentage"), CultureInfo.InvariantCulture);
			for (int i = 0; i < 15; i++) p.X[i] = double.Parse(Req("x" + (i + 1)), CultureInfo.InvariantCulture);
			p.Features = Req("ALLall_Header").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
			return p;
		}

		private double EncogWeightForFeature(string feature, double[] x)
		{
			if (feature.Equals("model_cnn_fear_greed", StringComparison.OrdinalIgnoreCase)) return x[0];
			if (feature.Equals("risk_measure", StringComparison.OrdinalIgnoreCase)) return x[1];
			if (feature.Equals("model_panic_euphoria", StringComparison.OrdinalIgnoreCase)) return x[2];
			if (feature.Equals("myRSI_rep", StringComparison.OrdinalIgnoreCase)) return x[3];
			if (feature.Equals("trendScore", StringComparison.OrdinalIgnoreCase)) return x[4];
			if (feature.Equals("aroonUp", StringComparison.OrdinalIgnoreCase) || feature.Equals("aroonDown", StringComparison.OrdinalIgnoreCase)) return x[5];
			if (feature.Equals("dIMinus", StringComparison.OrdinalIgnoreCase) || feature.Equals("dIPlus", StringComparison.OrdinalIgnoreCase)) return x[6];
			if (feature.Equals("correlationAngle_rep", StringComparison.OrdinalIgnoreCase)) return x[7];
			if (feature.Equals("correlationTrend", StringComparison.OrdinalIgnoreCase)) return x[8];
			if (feature.Equals("hurst", StringComparison.OrdinalIgnoreCase)) return x[9];
			if (feature.Equals("cmf", StringComparison.OrdinalIgnoreCase)) return x[10];
			if (feature.Equals("decibel", StringComparison.OrdinalIgnoreCase)) return x[11];
			if (feature.Equals("jocker", StringComparison.OrdinalIgnoreCase)) return x[12];
			if (feature.Equals("marketBreadth", StringComparison.OrdinalIgnoreCase)) return x[13];
			return 1.0;
		}

		private Dictionary<string, TimeSeries> BuildEncogFeatureMap(BarHistory bars, CachedBaseIndicators cache, EncogModelParameters p, out int modelStartIndex)
		{
			var map = BuildFeatureMapFromCache(bars, cache, out modelStartIndex);
			foreach (string name in p.Features)
			{
				if (!map.ContainsKey(name)) throw new Exception($"В INI указан неизвестный входной признак: {name}");
				map[name] = EncogWeightForFeature(name, p.X) * map[name];
			}
			return map;
		}

		private bool IsEligibleForEncogTomorrowShort(EncogModelParameters p)
		{
			const double eps = 0.001;
			// Encog: x1/x2/x3 = внешние sentiment/risk, x13 = Jocker (NASDAQ H/L), x14 = Market Breadth.
			return Math.Abs(p.X[0]) <= eps && Math.Abs(p.X[1]) <= eps && Math.Abs(p.X[2]) <= eps && Math.Abs(p.X[12]) <= eps && Math.Abs(p.X[13]) <= eps;
		}

		private TimeSeries RunEncogInMemory(string modelFilePath, BarHistory bars, Dictionary<string, TimeSeries> featureMap, int modelStartIndex, List<string> features)
		{
			TimeSeries result = new TimeSeries(bars.DateTimes, double.NaN);
			BasicNetwork net = (BasicNetwork)EncogDirectoryPersistence.LoadObject(new FileInfo(modelFilePath));
			for (int i = modelStartIndex; i < bars.Count; i++)
			{
				double[] input = new double[features.Count];
				for (int j = 0; j < features.Count; j++) input[j] = featureMap[features[j]][i];
				result[i] = net.Compute(new BasicMLData(input))[0];
			}
			return result;
		}

		private double RunEncogTomorrow(BasicNetwork net, EncogModelParameters p, Dictionary<string, double> values)
		{
			double[] input = new double[p.Features.Count];
			for (int j = 0; j < p.Features.Count; j++)
			{
				string name = p.Features[j];
				double v = values.TryGetValue(name, out double z) ? z : 0.0;
				input[j] = v * EncogWeightForFeature(name, p.X);
			}
			return net.Compute(new BasicMLData(input))[0];
		}

		private ProcessResult ProcessEncogTradingPackage(BarHistory bars, string packagePath, string outputFileName, string packageLabel)
		{
			if (!Directory.Exists(packagePath)) throw new DirectoryNotFoundException(packagePath);
			string[] iniFiles = Directory.GetFiles(packagePath, "*.ini");
			Array.Sort(iniFiles, StringComparer.OrdinalIgnoreCase);
			TimeSeries agg = new TimeSeries(bars.DateTimes, 0.0), aggRaw = new TimeSeries(bars.DateTimes, 0.0);
			TimeSeries p1 = new TimeSeries(bars.DateTimes, 0.0), m1 = new TimeSeries(bars.DateTimes, 0.0), p2 = new TimeSeries(bars.DateTimes, 0.0), m2 = new TimeSeries(bars.DateTimes, 0.0);
			double[] tomorrow = new double[5];
			TimeSeries[] tomorrowHist = new TimeSeries[5];
			for (int sc = 0; sc < 5; sc++) tomorrowHist[sc] = new TimeSeries(bars.DateTimes, 0.0);
			var models = new List<(string FileName, double BenchValue, TimeSeries ForecastTS)>();
			int count = 0, tomorrowCount = 0, lastStart = StartIndex;
			double sumForecast = 0, sumLevelClass = 0, sumBench = 0, sumStrat = 0, sumStratPercent = 0, sumFull = 0;
			int countForecast = 0, countLevelClass = 0, countBench = 0, countStrat = 0, countStratPercent = 0, countFull = 0;
			int recordNumber = 1;
			var reportLines = new List<string>();

			AuditHistory.Log("┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
			AuditHistory.Log($"│                                                  ОТЧЁТ ПО ЗАГРУЖЕННЫМ МОДЕЛЯМ ENCOG ({packageLabel})                                                  │", false);
			AuditHistory.Log("├────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
			AuditHistory.Log("│ №   │ Файл EG                                                             │ Прогноз │Уровень│0/1│  Bench │ Strat  │ Str% │   Full  │   Дата    │", false);

			foreach (string iniFile in iniFiles)
			{
				string egFile = Path.ChangeExtension(iniFile, ".eg");
				if (!File.Exists(egFile)) { AuditHistory.Log($"[ПРОПУСК Encog] нет {Path.GetFileName(egFile)}", false); continue; }
				try
				{
					EncogModelParameters ep = LoadEncogParameters(iniFile);
					ActiveFeatureNames = ep.Features;
					bool[] usage = new bool[15]; for (int u = 0; u < 15; u++) usage[u] = Math.Abs(ep.X[u]) > 1e-10; modelVariableUsage.Add(usage);
					bool useTomorrow = tomorrowEnsembleMode == 0 || IsEligibleForEncogTomorrowShort(ep);
					if (useTomorrow) tomorrowCount++;

					var baseMap = BuildEncogFeatureMap(bars, _cachedIndicators, ep, out int start);
					Dictionary<string, TimeSeries> plus1Map = null;
					Dictionary<string, TimeSeries> minus1Map = null;
					Dictionary<string, TimeSeries> plus2Map = null;
					Dictionary<string, TimeSeries> minus2Map = null;
					if (AssemblerMode != 7)
					{
						plus1Map = BuildEncogFeatureMap(bars, _cachedIndicatorsPlus1, ep, out _);
						minus1Map = BuildEncogFeatureMap(bars, _cachedIndicatorsMinus1, ep, out _);
						plus2Map = BuildEncogFeatureMap(bars, _cachedIndicatorsPlus2, ep, out _);
						minus2Map = BuildEncogFeatureMap(bars, _cachedIndicatorsMinus2, ep, out _);
					}
					lastStart = Math.Max(lastStart, start);
					TimeSeries raw = RunEncogInMemory(egFile, bars, baseMap, start, ep.Features);
					TimeSeries rP1 = AssemblerMode != 7 ? RunEncogInMemory(egFile, bars, plus1Map, start, ep.Features) : null;
					TimeSeries rM1 = AssemblerMode != 7 ? RunEncogInMemory(egFile, bars, minus1Map, start, ep.Features) : null;
					TimeSeries rP2 = AssemblerMode != 7 ? RunEncogInMemory(egFile, bars, plus2Map, start, ep.Features) : null;
					TimeSeries rM2 = AssemblerMode != 7 ? RunEncogInMemory(egFile, bars, minus2Map, start, ep.Features) : null;
					TimeSeries cls = new TimeSeries(bars.DateTimes, 0.0);
					for (int i = 0; i < bars.Count; i++)
					{
						if (!double.IsNaN(raw[i])) { cls[i] = raw[i] >= ep.LevelClass ? 1 : 0; agg[i] += cls[i]; aggRaw[i] += raw[i]; }
						if (AssemblerMode != 7)
						{
							if (!double.IsNaN(rP1[i])) p1[i] += rP1[i] >= ep.LevelClass ? 1 : 0;
							if (!double.IsNaN(rM1[i])) m1[i] += rM1[i] >= ep.LevelClass ? 1 : 0;
							if (!double.IsNaN(rP2[i])) p2[i] += rP2[i] >= ep.LevelClass ? 1 : 0;
							if (!double.IsNaN(rM2[i])) m2[i] += rM2[i] >= ep.LevelClass ? 1 : 0;
						}
					}
					if (useTomorrow)
					{
						BasicNetwork net = (BasicNetwork)EncogDirectoryPersistence.LoadObject(new FileInfo(egFile));
						if (AssemblerMode != 7)
						{
							int hs = Math.Max(start, bars.Count - tomorrowBars - 1);
							for (int t = hs; t <= bars.Count - 2; t++)
								if (_tomorrowHistoryFeatureValues != null && _tomorrowHistoryFeatureValues[t] != null)
									for (int sc = 0; sc < 5; sc++)
										if (RunEncogTomorrow(net, ep, _tomorrowHistoryFeatureValues[t][sc]) >= ep.LevelClass)
											tomorrowHist[sc][t + 1] += 1.0;
						}
						for (int sc = 0; sc < 5; sc++)
							if (RunEncogTomorrow(net, ep, _tomorrowFeatureValues[sc]) >= ep.LevelClass)
								tomorrow[sc] += 1.0;
					}
					double bench = double.NaN;
					var mt = Regex.Match(Path.GetFileName(iniFile), @"Bench=?(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
					if (mt.Success) double.TryParse(mt.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out bench);
					models.Add((Path.GetFileName(iniFile), bench, cls));
					count++;

					int lastIdx = bars.Count - 1;
					double benchValue = double.NaN, stratValue = double.NaN, stratPercent = double.NaN, fullValue = double.NaN;
					if (lastIdx >= Day_100)
					{
						benchValue = bars.Close[lastIdx] - bars.Close[lastIdx - Day_100];
						stratValue = 0.0;
						fullValue = 0.0;
						for (int i = Math.Max(lastIdx - Day_100 + 1, start); i <= lastIdx; i++)
							if (cls[i - 1] == 1)
								stratValue += bars.Close[i] - bars.Close[i - 1];
						for (int i = Math.Max(start + 1, 1); i <= lastIdx; i++)
							if (cls[i - 1] == 1)
								fullValue += bars.Close[i] - bars.Close[i - 1];
						if (benchValue != 0.0) stratPercent = stratValue / benchValue * 100.0;
					}

					double forecastValue = lastIdx >= 0 && !double.IsNaN(raw[lastIdx]) ? raw[lastIdx] : double.NaN;
					if (!double.IsNaN(forecastValue)) { sumForecast += forecastValue; countForecast++; }
					if (!double.IsNaN(ep.LevelClass)) { sumLevelClass += ep.LevelClass; countLevelClass++; }
					if (!double.IsNaN(benchValue)) { sumBench += benchValue; countBench++; }
					if (!double.IsNaN(stratValue)) { sumStrat += stratValue; countStrat++; }
					if (!double.IsNaN(stratPercent)) { sumStratPercent += stratPercent; countStratPercent++; }
					if (!double.IsNaN(fullValue)) { sumFull += fullValue; countFull++; }

					if (!double.IsNaN(forecastValue))
					{
						string number = recordNumber.ToString("D3");
						string fileName = Path.GetFileName(egFile);
						string classification = forecastValue >= ep.LevelClass ? "1" : "0";
						string date = bars.DateTimes[lastIdx].ToString("yyyy-MM-dd");
						reportLines.Add($"│ {number,-3} │ {fileName,-35} │ {forecastValue,7:F4} │ {ep.LevelClass,5:F2} │ {classification,1} │ {benchValue,6:F2} │ {stratValue,6:F1} │ {stratPercent,4:F0} │ {fullValue,8:F1} │ {date,10} │");
						recordNumber++;
					}
				}
				catch (Exception ex) { AuditHistory.Log($"Ошибка Encog {Path.GetFileName(iniFile)}: {ex.Message}", false); }
			}

			double avgForecast = countForecast > 0 ? sumForecast / countForecast : double.NaN;
			double avgLevelClass = countLevelClass > 0 ? sumLevelClass / countLevelClass : double.NaN;
			double avgBench = countBench > 0 ? sumBench / countBench : double.NaN;
			double avgStrat = countStrat > 0 ? sumStrat / countStrat : double.NaN;
			double avgStratPercent = countStratPercent > 0 ? sumStratPercent / countStratPercent : double.NaN;
			double avgFull = countFull > 0 ? sumFull / countFull : double.NaN;
			AuditHistory.Log($"│ИТОГО│ {"",-66} │ {avgForecast,7:F4} │ {avgLevelClass,5:F2} │ {"",1} │ {avgBench,6:F2} │ {avgStrat,6:F1} │ {avgStratPercent,4:F0} │ {avgFull,8:F1} │ {"",10} │", false);
			foreach (string line in reportLines) AuditHistory.Log(line, false);
			AuditHistory.Log("└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘", false);
			AuditHistory.Log($"=== ENCOG {packageLabel}: загружено {count}; TOMORROW {(tomorrowEnsembleMode == 0 ? "FULL" : "SHORT")}: {tomorrowCount} ===", false);
			try
			{
				string outPath = Path.Combine(EncogRootDir, outputFileName);
				using (var w = new StreamWriter(outPath)) for (int i = StartIndex; i < bars.Count; i++) w.WriteLine($"{bars.DateTimes[i]:yyyy-MM-dd},{(count > 0 ? agg[i] / count * 100.0 : 0.0):F2}");
			}
			catch (Exception ex) { AuditHistory.Log($"Ошибка записи Encog ensemble: {ex.Message}", false); }
			return new ProcessResult { Ensemble = agg, EnsembleRAW = aggRaw, EnsemblePlus1 = p1, EnsembleMinus1 = m1, EnsemblePlus2 = p2, EnsembleMinus2 = m2, TomorrowClassifiedCounts = tomorrow, TomorrowHistoryCounts = tomorrowHist, TomorrowModelCount = tomorrowCount, AllModels = models, ModelCount = count, LastModelStartIndex = lastStart };
		}

		#endregion

		#region Process Trading Package

		private ProcessResult ProcessTradingPackage(BarHistory bars, string packagePath, string outputFileName, string packageLabel, bool isJordan)
		{
			var tomorrowPackageTimer = System.Diagnostics.Stopwatch.StartNew();
			if (!Directory.Exists(packagePath))
			{
				AuditHistory.Log($"Директория не найдена: {packagePath}", false);
				return new ProcessResult { Ensemble = null, EnsembleRAW = null, EnsemblePlus1 = null, EnsembleMinus1 = null, EnsemblePlus2 = null, EnsembleMinus2 = null, AllModels = new List<(string, double, TimeSeries)>(), ModelCount = 0, LastModelStartIndex = StartIndex };
			}

			string searchPattern = isJordan ? "*.jbin" : "*.pt";
			string[] modelFiles = Directory.GetFiles(packagePath, searchPattern);
			Array.Sort(modelFiles, StringComparer.OrdinalIgnoreCase);

			if (modelFiles.Length == 0)
			{
				AuditHistory.Log($"В директории {packagePath} не найдено ни одного {searchPattern} файла", false);
				return new ProcessResult { Ensemble = null, EnsembleRAW = null, EnsemblePlus1 = null, EnsembleMinus1 = null, EnsemblePlus2 = null, EnsembleMinus2 = null, AllModels = new List<(string, double, TimeSeries)>(), ModelCount = 0, LastModelStartIndex = StartIndex };
			}

			int successfullyLoadedModelCount = 0;
			int tomorrowModelCount = 0;
			TimeSeries local_aggregated_ensemble = new TimeSeries(bars.DateTimes, 0);
			TimeSeries local_aggregated_ensemble_RAW = new TimeSeries(bars.DateTimes, 0.0);
			TimeSeries localPlus1 = new TimeSeries(bars.DateTimes, 0.0);
			TimeSeries localMinus1 = new TimeSeries(bars.DateTimes, 0.0);
			TimeSeries localPlus2 = new TimeSeries(bars.DateTimes, 0.0);
			TimeSeries localMinus2 = new TimeSeries(bars.DateTimes, 0.0);
			double[] tomorrowCounts = new double[5];
			TimeSeries[] tomorrowHistoryCounts = new TimeSeries[5];
			for (int sc = 0; sc < 5; sc++) tomorrowHistoryCounts[sc] = new TimeSeries(bars.DateTimes, 0.0);

			double sumForecast = 0, sumLevelClass = 0, sumBench = 0, sumStrat = 0, sumStratPercent = 0, sumFull = 0;
			int countForecast = 0, countLevelClass = 0, countBench = 0, countStrat = 0, countStratPercent = 0, countFull = 0;

			var modelsData = new List<(string FileName, double BenchValue, TimeSeries ForecastTS)>();
			int recordNumber = 1;
			int lastModelStartIndex = StartIndex;
			List<string> configFileReportLines = new List<string>();

			AuditHistory.Log("┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
			AuditHistory.Log($"│                                               ОТЧЁТ ПО ЗАГРУЖЕННЫМ МОДЕЛЯМ TORCHSHARP ({packageLabel})                                               │", false);
			AuditHistory.Log("├────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
			AuditHistory.Log("│ №   │ Файл PT                                                             │ Прогноз │Уровень│0/1│  Bench │ Strat  │ Str% │   Full  │   Дата    │", false);

			foreach (string modelFile in modelFiles)
			{
				string modelFileName = Path.GetFileName(modelFile);
				string iniFile = Path.ChangeExtension(modelFile, ".ini");

				// === ЗАГРУЗКА ИНДИВИДУАЛЬНЫХ ПАРАМЕТРОВ ===
				ModelParameters modelPars = LoadIndividualModelParameters(iniFile, modelFileName);

				if (modelPars == null)
				{
					continue;
				}

				successfullyLoadedModelCount++;

				// === ЧТЕНИЕ x1-x15 ИЗ INI ТОЛЬКО ДЛЯ АНАЛИТИКИ ===
				bool[] currentUsage = ReadVariableUsageFromIni(iniFile);
				modelVariableUsage.Add(currentUsage);

				// Short Ensemble: исключаем модели, использующие неизвестные на t+1 внешние признаки.
				// x1=fear/greed, x2=risk, x3=panic/euphoria, x15=jocker; x16=marketBreadth (если присутствует в INI).
				bool useForTomorrow = tomorrowEnsembleMode == 0 || IsEligibleForTomorrowShortEnsemble(iniFile);
				if (useForTomorrow) tomorrowModelCount++;

				// Predict не отображается ни в одном режиме.
				// Per-model пересчет Predict отключен; он не участвует в инференсе ансамбля.

				// Построение featureMap (x1-x15 = 1.0 - как было изначально!)
				Dictionary<string, TimeSeries> featureMap = BuildFeatureMapFromCache(bars, _cachedIndicators, out int modelStartIndex);
				Dictionary<string, TimeSeries> featureMapPlus1 = null;
				Dictionary<string, TimeSeries> featureMapMinus1 = null;
				Dictionary<string, TimeSeries> featureMapPlus2 = null;
				Dictionary<string, TimeSeries> featureMapMinus2 = null;
				if (AssemblerMode != 7)
				{
					featureMapPlus1 = BuildFeatureMapFromCache(bars, _cachedIndicatorsPlus1, out _);
					featureMapMinus1 = BuildFeatureMapFromCache(bars, _cachedIndicatorsMinus1, out _);
					featureMapPlus2 = BuildFeatureMapFromCache(bars, _cachedIndicatorsPlus2, out _);
					featureMapMinus2 = BuildFeatureMapFromCache(bars, _cachedIndicatorsMinus2, out _);
				}
				lastModelStartIndex = modelStartIndex;

				// Базовый инференс
				TimeSeries forecastTS_RAW = isJordan ? RunJordanJbinInMemory(modelFile, bars, featureMap, modelStartIndex, modelPars) : RunTorchSharpInMemory(modelFile, bars, featureMap, modelStartIndex, modelPars);

				if (useForTomorrow)
				{
					// TOMORROW OPTIMIZED:
					// MLP: одна загрузка модели + один batch-forward сразу для всех TomorrowBars × 5 сценариев.
					// Jordan: одна загрузка .jbin + последовательные Forward без повторного чтения файла.
					int histStartT = AssemblerMode == 7
						? bars.Count
						: Math.Max(modelStartIndex, bars.Count - tomorrowBars - 1);
					if (isJordan)
					{
						AccumulateJordanTomorrowOptimized(
							modelFile, modelPars, forecastTS_RAW, histStartT, bars.Count,
							tomorrowHistoryCounts, tomorrowCounts);
					}
					else
					{
						AccumulateTorchTomorrowBatched(
							modelFile, modelPars, histStartT, bars.Count,
							tomorrowHistoryCounts, tomorrowCounts);
					}
				}

				TimeSeries rawPlus1 = null;
				TimeSeries rawMinus1 = null;
				TimeSeries rawPlus2 = null;
				TimeSeries rawMinus2 = null;

				// WHAT-IF полностью пропускаем в режиме 7.
				if (AssemblerMode != 7)
				{
					rawPlus1 = isJordan ? RunJordanWhatIfInMemory(modelFile, bars, featureMapPlus1, modelStartIndex, modelPars, forecastTS_RAW) : RunTorchSharpInMemory(modelFile, bars, featureMapPlus1, modelStartIndex, modelPars);
					rawMinus1 = isJordan ? RunJordanWhatIfInMemory(modelFile, bars, featureMapMinus1, modelStartIndex, modelPars, forecastTS_RAW) : RunTorchSharpInMemory(modelFile, bars, featureMapMinus1, modelStartIndex, modelPars);
					rawPlus2 = isJordan ? RunJordanWhatIfInMemory(modelFile, bars, featureMapPlus2, modelStartIndex, modelPars, forecastTS_RAW) : RunTorchSharpInMemory(modelFile, bars, featureMapPlus2, modelStartIndex, modelPars);
					rawMinus2 = isJordan ? RunJordanWhatIfInMemory(modelFile, bars, featureMapMinus2, modelStartIndex, modelPars, forecastTS_RAW) : RunTorchSharpInMemory(modelFile, bars, featureMapMinus2, modelStartIndex, modelPars);
				}

				TimeSeries forecastTS = new TimeSeries(bars.DateTimes);

				// Классификация с индивидуальным level_class
				for (int i = 0; i < forecastTS_RAW.Count; i++)
				{
					forecastTS[i] = forecastTS_RAW[i] >= modelPars.level_class ? 1 : 0;
					local_aggregated_ensemble[i] += forecastTS[i];
					local_aggregated_ensemble_RAW[i] += forecastTS_RAW[i];
					if (AssemblerMode != 7)
					{
						if (!double.IsNaN(rawPlus1[i])) localPlus1[i] += rawPlus1[i] >= modelPars.level_class ? 1.0 : 0.0;
						if (!double.IsNaN(rawMinus1[i])) localMinus1[i] += rawMinus1[i] >= modelPars.level_class ? 1.0 : 0.0;
						if (!double.IsNaN(rawPlus2[i])) localPlus2[i] += rawPlus2[i] >= modelPars.level_class ? 1.0 : 0.0;
						if (!double.IsNaN(rawMinus2[i])) localMinus2[i] += rawMinus2[i] >= modelPars.level_class ? 1.0 : 0.0;
					}
				}

				double benchFromFile = double.NaN;
				var match = Regex.Match(modelFileName, @"Bench=?(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
				if (match.Success)
					double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out benchFromFile);

				modelsData.Add((modelFileName, benchFromFile, forecastTS));

				// Расчет вектора эквити Full
				TimeSeries equityFullTS = new TimeSeries(bars.DateTimes, 0.0);
				double cumProfit = 0.0;
				if (bars.Count > 0) equityFullTS[0] = 0.0;

				for (int i = 1; i < bars.Count; i++)
				{
					double dp = bars.Close[i] - bars.Close[i - 1];
					if (((forecastTS[i] == 1) && (forecastTS[i - 1] == 1)) || ((forecastTS[i] == 0) && (forecastTS[i - 1] == 1)))
						cumProfit += dp;
					equityFullTS[i] = cumProfit;
				}

				double benchValue = double.NaN, stratValue = double.NaN, stratPercent = double.NaN, fullValue = double.NaN;
				int lastIdx = bars.Count - 1;

				if (lastIdx >= Day_100)
				{
					benchValue = bars.Close[lastIdx] - bars.Close[lastIdx - Day_100];
					stratValue = 0; fullValue = 0;

					int startCalcIdxStrat = Math.Max(lastIdx - Day_100 + 1, modelStartIndex);
					for (int i = startCalcIdxStrat; i <= lastIdx; i++)
					{
						double dailyProfit = bars.Close[i] - bars.Close[i - 1];
						if (((forecastTS[i] == 1) && (forecastTS[i - 1] == 1)) || ((forecastTS[i] == 0) && (forecastTS[i - 1] == 1)))
							stratValue += dailyProfit;
					}

					for (int i = Math.Max(modelStartIndex + 1, 1); i <= lastIdx; i++)
					{
						double dailyProfit = bars.Close[i] - bars.Close[i - 1];
						if (((forecastTS[i] == 1) && (forecastTS[i - 1] == 1)) || ((forecastTS[i] == 0) && (forecastTS[i - 1] == 1)))
							fullValue += dailyProfit;
					}

					if (benchValue != 0) stratPercent = (stratValue / benchValue) * 100;
				}

				double forecastValue = double.NaN;
				if (bars.Count > 0 && forecastTS_RAW != null && forecastTS_RAW.Count > 0)
					forecastValue = forecastTS_RAW[lastIdx];

				if (!double.IsNaN(forecastValue)) { sumForecast += forecastValue; countForecast++; }
				if (!double.IsNaN(modelPars.level_class)) { sumLevelClass += modelPars.level_class; countLevelClass++; }
				if (!double.IsNaN(benchValue)) { sumBench += benchValue; countBench++; }
				if (!double.IsNaN(stratValue)) { sumStrat += stratValue; countStrat++; }
				if (!double.IsNaN(stratPercent)) { sumStratPercent += stratPercent; countStratPercent++; }
				if (!double.IsNaN(fullValue)) { sumFull += fullValue; countFull++; }

				if (!double.IsNaN(forecastValue))
				{
					bool isAbove = forecastValue >= modelPars.level_class;
					string classificationResult = isAbove ? "1" : "0";
					string dateStr = bars.DateTimes[lastIdx].ToString("yyyy-MM-dd");
					string numberStr = recordNumber.ToString("D3");

					string row = $"│ {numberStr,-3} │ {modelFileName,-35} │ {forecastValue,7:F4} │ {modelPars.level_class,5:F2} │ {classificationResult,1} │ {benchValue,6:F2} │ {stratValue,6:F1} │ {stratPercent,4:F0} │ {fullValue,8:F1} │ {dateStr,10} │";
					configFileReportLines.Add(row);
					recordNumber++;
				}
			}

			// Вывод итогов таблицы
			double avgForecast = countForecast > 0 ? sumForecast / countForecast : double.NaN;
			double avgLevelClass = countLevelClass > 0 ? sumLevelClass / countLevelClass : double.NaN;
			double avgBench = countBench > 0 ? sumBench / countBench : double.NaN;
			double avgStrat = countStrat > 0 ? sumStrat / countStrat : double.NaN;
			double avgStratPercent = countStratPercent > 0 ? sumStratPercent / countStratPercent : double.NaN;
			double avgFull = countFull > 0 ? sumFull / countFull : double.NaN;

			string itogRow = $"│ИТОГО│ {"",-66} │ {avgForecast,7:F4} │ {avgLevelClass,5:F2} │ {"",1} │ {avgBench,6:F2} │ {avgStrat,6:F1} │ {avgStratPercent,4:F0} │ {avgFull,8:F1} │ {"",10} │";
			AuditHistory.Log(itogRow, false);
			foreach (string line in configFileReportLines) AuditHistory.Log(line, false);
			AuditHistory.Log("└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘", false);

			// Сохранение ensemblePR_J для пакета
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
				AuditHistory.Log($"Ансамбль {packageLabel} сохранён в файл: {ensemblePRPath} (Успешно загружено моделей: {successfullyLoadedModelCount})", false);
			}
			catch (Exception ex)
			{
				AuditHistory.Log($"Ошибка при записи файла {outputFileName}: {ex.Message}", false);
			}

			tomorrowPackageTimer.Stop();
			AuditHistory.Log($"TOMORROW Optimized Batch | TomorrowBars={tomorrowBars} | Models={tomorrowModelCount} | Package={packageLabel} | Total package time={tomorrowPackageTimer.Elapsed.TotalSeconds:F2} sec", false);

			return new ProcessResult
			{
				Ensemble = local_aggregated_ensemble,
				EnsembleRAW = local_aggregated_ensemble_RAW,
				EnsemblePlus1 = localPlus1,
				EnsembleMinus1 = localMinus1,
				EnsemblePlus2 = localPlus2,
				EnsembleMinus2 = localMinus2,
				TomorrowClassifiedCounts = tomorrowCounts,
				TomorrowHistoryCounts = tomorrowHistoryCounts,
				TomorrowModelCount = tomorrowModelCount,
				AllModels = modelsData,
				ModelCount = successfullyLoadedModelCount,
				LastModelStartIndex = lastModelStartIndex
			};
		}

		#endregion

		#region Jordan JBIN In-Memory Inference

		private sealed class JordanRuntimeModel
		{
			public int BaseInputSize;
			public int ModelInputSize;
			public int HiddenSize;
			public int ExitType;
			public int Mode;
			public int Epoch;
			public float LearningRate;
			public int Seed;
			public long FeatureMaskBits;
			public float JordanThreshold;
			public float InitialContext;

			public float[] W1;
			public float[] B1;
			public float[] W2;
			public float B2;

			public float Forward(float[] input, float prevRaw)
			{
				float prevHard = prevRaw >= JordanThreshold ? 1f : 0f;
				float raw = B2;

				for (int h = 0; h < HiddenSize; h++)
				{
					int wBase = h * ModelInputSize;
					float sum = B1[h];

					for (int i = 0; i < BaseInputSize && i < input.Length; i++)
						sum += W1[wBase + i] * input[i];

					if (BaseInputSize < ModelInputSize)
						sum += W1[wBase + BaseInputSize] * prevRaw;

					if (BaseInputSize + 1 < ModelInputSize)
						sum += W1[wBase + BaseInputSize + 1] * prevHard;

					float hidden = SigmoidFloat(sum);
					raw += W2[h] * hidden;
				}

				return ExitType == 1 ? SigmoidFloat(raw) : raw;
			}
		}

		private JordanRuntimeModel LoadJordanModelBinary(string modelFilePath, ModelParameters modelPars)
		{
			using (var reader = new BinaryReader(File.Open(modelFilePath, FileMode.Open, FileAccess.Read, FileShare.Read)))
			{
				string magic = reader.ReadString();
				if (!string.Equals(magic, "JORDAN_DETACHED_MLP", StringComparison.Ordinal))
					throw new InvalidOperationException($"Неверный формат Jordan-модели: {magic}");

				int version = reader.ReadInt32();
				if (version != 1)
					throw new InvalidOperationException($"Неподдерживаемая версия Jordan-модели: {version}");

				var model = new JordanRuntimeModel();

				model.BaseInputSize = reader.ReadInt32();
				model.ModelInputSize = reader.ReadInt32();
				model.HiddenSize = reader.ReadInt32();

				int outputSize = reader.ReadInt32();
				if (outputSize != 1)
					throw new InvalidOperationException($"Неподдерживаемый outputSize={outputSize}");

				model.ExitType = reader.ReadInt32();
				model.Mode = reader.ReadInt32();
				model.Epoch = reader.ReadInt32();
				model.LearningRate = reader.ReadSingle();
				model.Seed = reader.ReadInt32();
				model.FeatureMaskBits = reader.ReadInt64();
				model.JordanThreshold = reader.ReadSingle();
				model.InitialContext = reader.ReadSingle();

				model.W1 = new float[model.HiddenSize * model.ModelInputSize];
				model.B1 = new float[model.HiddenSize];
				model.W2 = new float[model.HiddenSize];

				for (int i = 0; i < model.W1.Length; i++)
					model.W1[i] = reader.ReadSingle();

				for (int i = 0; i < model.B1.Length; i++)
					model.B1[i] = reader.ReadSingle();

				for (int i = 0; i < model.W2.Length; i++)
					model.W2[i] = reader.ReadSingle();

				model.B2 = reader.ReadSingle();

				modelPars.addLayer = model.HiddenSize;
				modelPars.exit_type = model.ExitType;
				modelPars.jordan_threshold = model.JordanThreshold;
				modelPars.jordan_initial_context = model.InitialContext;
				modelPars.jordan_mode = model.Mode;
				modelPars.base_input_size = model.BaseInputSize;
				modelPars.model_input_size = model.ModelInputSize;

				return model;
			}
		}

		private TimeSeries RunJordanJbinInMemory(string modelFilePath, BarHistory bars, Dictionary<string, TimeSeries> featureMap, int modelStartIndex, ModelParameters modelPars)
		{
			TimeSeries resultSeries = new TimeSeries(bars.DateTimes, double.NaN);

			try
			{
				JordanRuntimeModel model = LoadJordanModelBinary(modelFilePath, modelPars);

				if (ActiveFeatureNames == null || ActiveFeatureNames.Count < model.BaseInputSize)
					throw new InvalidOperationException($"Недостаточно признаков для Jordan-модели: нужно {model.BaseInputSize}, есть {(ActiveFeatureNames == null ? 0 : ActiveFeatureNames.Count)}");

				int start = Math.Max(0, modelStartIndex);
				if (start >= bars.Count)
					return resultSeries;

				float prevRaw = model.InitialContext;
				float[] input = new float[model.BaseInputSize];

				for (int bar = start; bar < bars.Count; bar++)
				{
					for (int j = 0; j < model.BaseInputSize; j++)
					{
						string featureName = ActiveFeatureNames[j];

						double value = 0.0;
						if (featureMap.TryGetValue(featureName, out TimeSeries ts) && bar < ts.Count)
							value = ts[bar];

						input[j] = double.IsNaN(value) || double.IsInfinity(value) ? 0.0f : (float)value;
					}

					float pred = model.Forward(input, prevRaw);
					resultSeries[bar] = pred;
					prevRaw = pred;
				}
			}
			catch (Exception ex)
			{
				AuditHistory.Log($"Ошибка инференса Jordan-модели {Path.GetFileName(modelFilePath)}: {ex.Message}", false);
			}

			return resultSeries;
		}

		private TimeSeries RunJordanWhatIfInMemory(string modelFilePath, BarHistory bars, Dictionary<string, TimeSeries> featureMap, int modelStartIndex, ModelParameters modelPars, TimeSeries baselineRaw)
		{
			TimeSeries resultSeries = new TimeSeries(bars.DateTimes, double.NaN);
			try
			{
				JordanRuntimeModel model = LoadJordanModelBinary(modelFilePath, modelPars);
				int start = Math.Max(0, modelStartIndex);
				int whatIfStart = Math.Max(start, bars.Count - whatIfBars);
				float[] input = new float[model.BaseInputSize];

				for (int bar = whatIfStart; bar < bars.Count; bar++)
				{
					for (int j = 0; j < model.BaseInputSize; j++)
					{
						string featureName = ActiveFeatureNames[j];
						double value = 0.0;
						if (featureMap.TryGetValue(featureName, out TimeSeries ts) && bar < ts.Count)
							value = ts[bar];
						input[j] = double.IsNaN(value) || double.IsInfinity(value) ? 0.0f : (float)value;
					}

					float prevRaw = model.InitialContext;
					if (bar > start && baselineRaw != null && !double.IsNaN(baselineRaw[bar - 1]) && !double.IsInfinity(baselineRaw[bar - 1]))
						prevRaw = (float)baselineRaw[bar - 1];

					resultSeries[bar] = model.Forward(input, prevRaw);
				}
			}
			catch (Exception ex)
			{
				AuditHistory.Log($"Ошибка WHAT-IF инференса Jordan-модели {Path.GetFileName(modelFilePath)}: {ex.Message}", false);
			}
			return resultSeries;
		}

		private static float SigmoidFloat(float x)
		{
			if (x >= 0f)
			{
				float z = (float)Math.Exp(-x);
				return 1f / (1f + z);
			}
			else
			{
				float z = (float)Math.Exp(x);
				return z / (1f + z);
			}
		}

		#endregion

		#region TorchSharp In-Memory Inference

		private TimeSeries RunTorchSharpInMemory(string modelFilePath, BarHistory bars, Dictionary<string, TimeSeries> featureMap, int modelStartIndex, ModelParameters modelPars)
		{
			TimeSeries resultSeries = new TimeSeries(bars.DateTimes, double.NaN);
			try
			{
				int featureCount = ActiveFeatureNames.Count;
				int barsToProcess = bars.Count - modelStartIndex;

				if (barsToProcess <= 0) return resultSeries;

				int hiddenSize = Math.Max(1, modelPars.addLayer);
				int outputSize = 1;
				int exit_type = modelPars.exit_type;

				using var model = new MLPModel(featureCount, hiddenSize, outputSize, exit_type);
				model.load(modelFilePath);
				model.eval();

				float[] inputData = new float[barsToProcess * featureCount];
				int row = 0;
				for (int i = modelStartIndex; i < bars.Count; i++)
				{
					for (int j = 0; j < featureCount; j++)
					{
						double val = featureMap[ActiveFeatureNames[j]][i];
						inputData[row * featureCount + j] = double.IsNaN(val) ? 0.0f : (float)val;
					}
					row++;
				}

				using Tensor inputTensor = torch.tensor(inputData, new long[] { barsToProcess, featureCount }, dtype: ScalarType.Float32, device: torch.CPU);

				using (Tensor prediction = model.forward(inputTensor))
				using (Tensor predictionCpu = prediction.detach().to(torch.CPU))
				using (Tensor flat = predictionCpu.reshape(new long[] { -1 }).contiguous())
				{
					float[] values = flat.data<float>().ToArray();
					for (int i = 0; i < values.Length; i++)
					{
						resultSeries[modelStartIndex + i] = values[i];
					}
				}
			}
			catch (Exception ex)
			{
				AuditHistory.Log($"Ошибка инференса модели {Path.GetFileName(modelFilePath)}: {ex.Message}", false);
			}
			return resultSeries;
		}

		#endregion

		private double RunTorchSharpTomorrowInMemory(string modelFilePath, ModelParameters modelPars, Dictionary<string, double> features)
		{
			try
			{
				int featureCount = ActiveFeatureNames.Count;
				int hiddenSize = Math.Max(1, modelPars.addLayer);
				using var model = new MLPModel(featureCount, hiddenSize, 1, modelPars.exit_type);
				model.load(modelFilePath);
				model.eval();
				float[] input = new float[featureCount];
				for (int j = 0; j < featureCount; j++)
				{
					string featureName = ActiveFeatureNames[j];
					double value = features.TryGetValue(featureName, out double x) ? x : 0.0;
					input[j] = double.IsNaN(value) || double.IsInfinity(value) ? 0.0f : (float)value;
				}
				using Tensor inputTensor = torch.tensor(input, new long[] { 1, featureCount }, dtype: ScalarType.Float32, device: torch.CPU);
				using Tensor prediction = model.forward(inputTensor);
				using Tensor flat = prediction.detach().to(torch.CPU).reshape(new long[] { -1 }).contiguous();
				float[] values = flat.data<float>().ToArray();
				return values.Length > 0 ? values[0] : double.NaN;
			}
			catch (Exception ex)
			{
				AuditHistory.Log($"Ошибка TOMORROW инференса MLP-модели {Path.GetFileName(modelFilePath)}: {ex.Message}", false);
				return double.NaN;
			}
		}


		// ===== TOMORROW: ОПТИМИЗИРОВАННЫЙ BATCH ДЛЯ TORCHSHARP =====
		// Важно: математика прогноза не меняется. Меняется только способ вычисления:
		// вместо загрузки одной и той же модели для каждого бара/сценария модель загружается ОДИН раз,
		// а все входы Tomorrow подаются одним batch-тензором.
		private void AccumulateTorchTomorrowBatched(
			string modelFilePath,
			ModelParameters modelPars,
			int histStartT,
			int barsCount,
			TimeSeries[] tomorrowHistoryCounts,
			double[] tomorrowCounts)
		{
			try
			{
				int featureCount = ActiveFeatureNames.Count;
				int hiddenSize = Math.Max(1, modelPars.addLayer);

				var rows = new List<float[]>();
				var rowT = new List<int>();
				var rowSc = new List<int>();
				var rowIsFuture = new List<bool>();

				for (int t = histStartT; t <= barsCount - 2; t++)
				{
					if (_tomorrowHistoryFeatureValues == null || _tomorrowHistoryFeatureValues[t] == null)
						continue;

					for (int sc = 0; sc < 5; sc++)
					{
						float[] input = new float[featureCount];
						Dictionary<string, double> fv = _tomorrowHistoryFeatureValues[t][sc];
						for (int j = 0; j < featureCount; j++)
						{
							string featureName = ActiveFeatureNames[j];
							double value = fv.TryGetValue(featureName, out double x) ? x : 0.0;
							input[j] = double.IsNaN(value) || double.IsInfinity(value) ? 0.0f : (float)value;
						}
						rows.Add(input);
						rowT.Add(t);
						rowSc.Add(sc);
						rowIsFuture.Add(false);
					}
				}

				if (_tomorrowFeatureValues != null && barsCount > 0)
				{
					for (int sc = 0; sc < 5; sc++)
					{
						float[] input = new float[featureCount];
						Dictionary<string, double> fv = _tomorrowFeatureValues[sc];
						for (int j = 0; j < featureCount; j++)
						{
							string featureName = ActiveFeatureNames[j];
							double value = fv.TryGetValue(featureName, out double x) ? x : 0.0;
							input[j] = double.IsNaN(value) || double.IsInfinity(value) ? 0.0f : (float)value;
						}
						rows.Add(input);
						rowT.Add(-1);
						rowSc.Add(sc);
						rowIsFuture.Add(true);
					}
				}

				if (rows.Count == 0) return;

				float[] flatInput = new float[rows.Count * featureCount];
				for (int r = 0; r < rows.Count; r++)
					Array.Copy(rows[r], 0, flatInput, r * featureCount, featureCount);

				using var model = new MLPModel(featureCount, hiddenSize, 1, modelPars.exit_type);
				model.load(modelFilePath);
				model.eval();

				using Tensor inputTensor = torch.tensor(
					flatInput,
					new long[] { rows.Count, featureCount },
					dtype: ScalarType.Float32,
					device: torch.CPU);

				using Tensor prediction = model.forward(inputTensor);
				using Tensor predictionCpu = prediction.detach().to(torch.CPU);
				using Tensor flat = predictionCpu.reshape(new long[] { -1 }).contiguous();

				float[] values = flat.data<float>().ToArray();
				int n = Math.Min(values.Length, rows.Count);

				for (int r = 0; r < n; r++)
				{
					double raw = values[r];
					if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < modelPars.level_class)
						continue;

					int sc = rowSc[r];
					if (rowIsFuture[r])
						tomorrowCounts[sc] += 1.0;
					else
						tomorrowHistoryCounts[sc][rowT[r] + 1] += 1.0;
				}
			}
			catch (Exception ex)
			{
				AuditHistory.Log($"Ошибка TOMORROW batch-инференса MLP-модели {Path.GetFileName(modelFilePath)}: {ex.Message}", false);
			}
		}

		// ===== TOMORROW: ОДНА ЗАГРУЗКА JORDAN-МОДЕЛИ =====
		private void AccumulateJordanTomorrowOptimized(
			string modelFilePath,
			ModelParameters modelPars,
			TimeSeries forecastTS_RAW,
			int histStartT,
			int barsCount,
			TimeSeries[] tomorrowHistoryCounts,
			double[] tomorrowCounts)
		{
			try
			{
				JordanRuntimeModel model = LoadJordanModelBinary(modelFilePath, modelPars);
				int featureCount = model.BaseInputSize;
				float[] input = new float[featureCount];

				for (int t = histStartT; t <= barsCount - 2; t++)
				{
					if (_tomorrowHistoryFeatureValues == null || _tomorrowHistoryFeatureValues[t] == null)
						continue;

					double prevRaw = forecastTS_RAW[t];
					float prev = (!double.IsNaN(prevRaw) && !double.IsInfinity(prevRaw))
						? (float)prevRaw
						: model.InitialContext;

					for (int sc = 0; sc < 5; sc++)
					{
						Dictionary<string, double> fv = _tomorrowHistoryFeatureValues[t][sc];
						for (int j = 0; j < featureCount; j++)
						{
							string featureName = ActiveFeatureNames[j];
							double value = fv.TryGetValue(featureName, out double x) ? x : 0.0;
							input[j] = double.IsNaN(value) || double.IsInfinity(value) ? 0.0f : (float)value;
						}

						double raw = model.Forward(input, prev);
						if (!double.IsNaN(raw) && raw >= modelPars.level_class)
							tomorrowHistoryCounts[sc][t + 1] += 1.0;
					}
				}

				if (_tomorrowFeatureValues != null && barsCount > 0)
				{
					double prevRaw = forecastTS_RAW[barsCount - 1];
					float prev = (!double.IsNaN(prevRaw) && !double.IsInfinity(prevRaw))
						? (float)prevRaw
						: model.InitialContext;

					for (int sc = 0; sc < 5; sc++)
					{
						Dictionary<string, double> fv = _tomorrowFeatureValues[sc];
						for (int j = 0; j < featureCount; j++)
						{
							string featureName = ActiveFeatureNames[j];
							double value = fv.TryGetValue(featureName, out double x) ? x : 0.0;
							input[j] = double.IsNaN(value) || double.IsInfinity(value) ? 0.0f : (float)value;
						}

						double raw = model.Forward(input, prev);
						if (!double.IsNaN(raw) && raw >= modelPars.level_class)
							tomorrowCounts[sc] += 1.0;
					}
				}
			}
			catch (Exception ex)
			{
				AuditHistory.Log($"Ошибка TOMORROW оптимизированного Jordan-инференса {Path.GetFileName(modelFilePath)}: {ex.Message}", false);
			}
		}


		#region Build Feature Map From Cache (x1-x15 всегда = 1.0 - как было!)

		private Dictionary<string, TimeSeries> BuildFeatureMapFromCache(BarHistory bars, CachedBaseIndicators cache, out int modelStartIndex)
		{
			modelStartIndex = cache.modelStartIndex;
			Dictionary<string, TimeSeries> map = new Dictionary<string, TimeSeries>(StringComparer.OrdinalIgnoreCase);

			// ВСЕ коэффициенты x1-x15 = 1.0 (как было изначально!)
			map["model_cnn_fear_greed"] = 1.0 * cache.model_cnn_fear_greed_base;
			map["risk_measure"] = 1.0 * cache.risk_measure_base;
			map["model_panic_euphoria"] = 1.0 * cache.model_panic_euphoria_base;
			map["myRSI_rep"] = 1.0 * cache.myRSI_rep_base;
			map["trendScore"] = 1.0 * cache.trendScore_base;
			map["aroonUp"] = 1.0 * cache.aroonUp_base;
			map["aroonDown"] = 1.0 * cache.aroonDown_base;
			map["dIMinus"] = 1.0 * cache.dIMinus_base;
			map["dIPlus"] = 1.0 * cache.dIPlus_base;
			map["correlationAngle_rep"] = 1.0 * cache.correlationAngle_rep_base;
			map["correlationTrend"] = 1.0 * cache.correlationTrend_base;
			map["hurst"] = 1.0 * cache.hurst_base;
			map["cmf"] = 1.0 * cache.cmf_base;
			map["decibel"] = 1.0 * cache.decibel_base;
			map["jocker"] = 1.0 * cache.jocker_base;
			map["marketBreadth"] = 1.0 * cache.marketBreadth_base;

			return map;
		}

		#endregion

		#region Precompute Base Indicators

		private CachedBaseIndicators PrecomputeBaseIndicators(BarHistory bars)
		{
			var cached = new CachedBaseIndicators();
			int maxStartIndex = 175;
			List<IndicatorBase> localStartIndexList = new List<IndicatorBase>();

			BarHistory model_cnn_fear_greed_hist = AuditHistory.Get(bars, "MODEL_CNN_FEAR_GREED");
			SMA model_cnn_fear_greedTMP = SMA.Series(model_cnn_fear_greed_hist.Close, 1);
			cached.model_cnn_fear_greed_base = (model_cnn_fear_greedTMP - 50) / 50;

			BarHistory risk_measure_hist = AuditHistory.Get(bars, "RISK_MEASURE");
			SMA risk_measureTMP = SMA.Series(risk_measure_hist.Close, 1);
			cached.risk_measure_base = (risk_measureTMP - 50) / 50;

			BarHistory model_panic_euphoria_hist = AuditHistory.Get(bars, "MODEL_PANIC_EUPHORIA_DAY");
			SMA model_panic_euphoriaTMP = SMA.Series(model_panic_euphoria_hist.Close, 1);
			cached.model_panic_euphoria_base = (model_panic_euphoriaTMP - 0.5) / 1;

			MyRSI_rep myRSI_repTmp = new MyRSI_rep(bars.Close, 14, 8);
			localStartIndexList.Add(myRSI_repTmp);
			cached.myRSI_rep_base = myRSI_repTmp;

			TrendScore trendScoreTmp = new TrendScore(bars);
			localStartIndexList.Add(trendScoreTmp);
			cached.trendScore_base = trendScoreTmp * 0.1;

			AroonUp aroonUpTmp = new AroonUp(bars.Close, 48);
			localStartIndexList.Add(aroonUpTmp);
			cached.aroonUp_base = (-50 + aroonUpTmp) / 50;

			AroonDown aroonDownTmp = new AroonDown(bars.Close, 48);
			localStartIndexList.Add(aroonDownTmp);
			cached.aroonDown_base = (-50 + aroonDownTmp) / 50;

			DIMinus dIMinusTmp = new DIMinus(bars, 7);
			TimeSeries dIMinusW = (-30 + dIMinusTmp) / 30;
			cached.dIMinus_base = AGC(dIMinusW);

			DIPlus dIPlusTmp = new DIPlus(bars, 14);
			TimeSeries dIPlusW = (-25 + dIPlusTmp) / 30;
			cached.dIPlus_base = AGC(dIPlusW);

			CorrelationAngle_rep correlationAngle_repTmp = new CorrelationAngle_rep(bars.Close, 48);
			localStartIndexList.Add(correlationAngle_repTmp);
			cached.correlationAngle_rep_base = (-0 + correlationAngle_repTmp) / 180;

			IndicatorBase correlationTrendTmp = new CorrelationTrend(bars.Close, 35);
			localStartIndexList.Add(correlationTrendTmp);
			cached.correlationTrend_base = AGC(correlationTrendTmp) / 1;

			Hurst hurstTmpBase = new Hurst(bars.Close, 80);
			EhlersInverseFisherTransform_gpt hurstTmp = new EhlersInverseFisherTransform_gpt(hurstTmpBase, 3, 1);
			cached.hurst_base = 7 * (hurstTmp - 0.6);

			CMF cmfBase = new CMF(bars, 80);
			EhlersInverseFisherTransform_gpt cmfTmp = new EhlersInverseFisherTransform_gpt(cmfBase, 3, 10);
			localStartIndexList.Add(cmfTmp);
			cached.cmf_base = (cmfTmp - 0.1);

			Decibel decibelBase = new Decibel(bars.Close, 96);
			EhlersInverseFisherTransform_gpt decibelTmp = new EhlersInverseFisherTransform_gpt(decibelBase, 8, 0.1);
			localStartIndexList.Add(decibelTmp);
			cached.decibel_base = (decibelTmp + 0.55);

			// === ИЗМЕНЕНО: Логика индикатора Jocker перенесена из первой программы (Nasdaq) ===
			BarHistory nasdaqNewHighsBars = AuditHistory.Get(bars, "$NASDAQ_NEWHI");
			BarHistory nasdaqNewLowsBars = AuditHistory.Get(bars, "$NASDAQ_NEWLO");

			if (nasdaqNewHighsBars != null && nasdaqNewLowsBars != null)
			{
				TimeSeries nasdaqNewHighs = nasdaqNewHighsBars.Close;
				TimeSeries nasdaqNewLows = nasdaqNewLowsBars.Close;

				TimeSeries hiloLogic = new TimeSeries(bars.DateTimes, 0);
				for (int i = 0; i < bars.Count; i++)
				{
					double sumHighs = 0, sumLows = 0;
					for (int j = 0; j < 3 && j <= i; j++)
					{
						sumHighs += nasdaqNewHighs[i - j];
						sumLows += nasdaqNewLows[i - j];
					}
					double sum = sumHighs + sumLows;
					hiloLogic[i] = sum > 0 ? (sumHighs - sumLows) / sum : 0;
				}
				cached.jocker_base = EMA.Series(hiloLogic, 20) / 1.0;
			}
			else
			{
				cached.jocker_base = new TimeSeries(bars.DateTimes, 0.0);
			}
			// ================================================================================

			cached.marketBreadth_base = CalculateMarketBreadthBase(bars);

			foreach (IndicatorBase ib in localStartIndexList)
			{
				if (ib.FirstValidIndex > maxStartIndex)
					maxStartIndex = ib.FirstValidIndex;
			}
			cached.modelStartIndex = maxStartIndex;

			return cached;
		}

		private CachedBaseIndicators PrecomputeWhatIfIndicators(BarHistory bars, double priceFactor, int barsToCalculate)
		{
			// Внешние ряды остаются базовыми. Ценозависимые признаки пересчитываются так,
			// будто ТОЛЬКО текущий бар имел цену priceFactor от фактической.
			var c = new CachedBaseIndicators
			{
				model_cnn_fear_greed_base = _cachedIndicators.model_cnn_fear_greed_base,
				risk_measure_base = _cachedIndicators.risk_measure_base,
				model_panic_euphoria_base = _cachedIndicators.model_panic_euphoria_base,
				myRSI_rep_base = new TimeSeries(bars.DateTimes, double.NaN),
				trendScore_base = new TimeSeries(bars.DateTimes, double.NaN),
				aroonUp_base = new TimeSeries(bars.DateTimes, double.NaN),
				aroonDown_base = new TimeSeries(bars.DateTimes, double.NaN),
				dIMinus_base = new TimeSeries(bars.DateTimes, double.NaN),
				dIPlus_base = new TimeSeries(bars.DateTimes, double.NaN),
				correlationAngle_rep_base = new TimeSeries(bars.DateTimes, double.NaN),
				correlationTrend_base = new TimeSeries(bars.DateTimes, double.NaN),
				hurst_base = new TimeSeries(bars.DateTimes, double.NaN),
				cmf_base = new TimeSeries(bars.DateTimes, double.NaN),
				decibel_base = new TimeSeries(bars.DateTimes, double.NaN),
				jocker_base = _cachedIndicators.jocker_base,
				marketBreadth_base = _cachedIndicators.marketBreadth_base,
				modelStartIndex = _cachedIndicators.modelStartIndex
			};

			// До рабочего StartIndex значения не используются моделями; оставляем базовые для аккуратности графа данных.
			for (int i = 0; i < Math.Min(c.modelStartIndex, bars.Count); i++)
			{
				c.myRSI_rep_base[i] = _cachedIndicators.myRSI_rep_base[i];
				c.trendScore_base[i] = _cachedIndicators.trendScore_base[i];
				c.aroonUp_base[i] = _cachedIndicators.aroonUp_base[i];
				c.aroonDown_base[i] = _cachedIndicators.aroonDown_base[i];
				c.dIMinus_base[i] = _cachedIndicators.dIMinus_base[i];
				c.dIPlus_base[i] = _cachedIndicators.dIPlus_base[i];
				c.correlationAngle_rep_base[i] = _cachedIndicators.correlationAngle_rep_base[i];
				c.correlationTrend_base[i] = _cachedIndicators.correlationTrend_base[i];
				c.hurst_base[i] = _cachedIndicators.hurst_base[i];
				c.cmf_base[i] = _cachedIndicators.cmf_base[i];
				c.decibel_base[i] = _cachedIndicators.decibel_base[i];
			}

			int firstWhatIfBar = Math.Max(c.modelStartIndex, bars.Count - Math.Max(1, barsToCalculate));
			for (int bar = firstWhatIfBar; bar < bars.Count; bar++)
			{
				BarHistory wb = BuildWhatIfHistoryToBar(bars, bar, priceFactor);
				int k = wb.Count - 1;

				MyRSI_rep rsi = new MyRSI_rep(wb.Close, 14, 8);
				c.myRSI_rep_base[bar] = rsi[k];

				TrendScore trend = new TrendScore(wb);
				c.trendScore_base[bar] = trend[k] * 0.1;

				AroonUp au = new AroonUp(wb.Close, 48);
				AroonDown ad = new AroonDown(wb.Close, 48);
				c.aroonUp_base[bar] = (-50.0 + au[k]) / 50.0;
				c.aroonDown_base[bar] = (-50.0 + ad[k]) / 50.0;

				DIMinus dm = new DIMinus(wb, 7);
				DIPlus dp = new DIPlus(wb, 14);
				TimeSeries dmW = (-30 + dm) / 30;
				TimeSeries dpW = (-25 + dp) / 30;
				c.dIMinus_base[bar] = AGC(dmW)[k];
				c.dIPlus_base[bar] = AGC(dpW)[k];

				CorrelationAngle_rep ca = new CorrelationAngle_rep(wb.Close, 48);
				c.correlationAngle_rep_base[bar] = ca[k] / 180.0;

				IndicatorBase ct = new CorrelationTrend(wb.Close, 35);
				c.correlationTrend_base[bar] = AGC(ct)[k];

				Hurst hb = new Hurst(wb.Close, 80);
				EhlersInverseFisherTransform_gpt hf = new EhlersInverseFisherTransform_gpt(hb, 3, 1);
				c.hurst_base[bar] = 7.0 * (hf[k] - 0.6);

				CMF cmf = new CMF(wb, 80);
				EhlersInverseFisherTransform_gpt cmff = new EhlersInverseFisherTransform_gpt(cmf, 3, 10);
				c.cmf_base[bar] = cmff[k] - 0.1;

				Decibel db = new Decibel(wb.Close, 96);
				EhlersInverseFisherTransform_gpt dbf = new EhlersInverseFisherTransform_gpt(db, 8, 0.1);
				c.decibel_base[bar] = dbf[k] + 0.55;
			}

			return c;
		}

		private BarHistory BuildWhatIfHistoryToBar(BarHistory source, int lastBar, double priceFactor)
		{
			BarHistory wb = new BarHistory(source);
			for (int i = 0; i <= lastBar; i++)
			{
				double f = (i == lastBar) ? priceFactor : 1.0;
				wb.Add(source.DateTimes[i], source.Open[i] * f, source.High[i] * f, source.Low[i] * f, source.Close[i] * f, source.Volume[i]);
			}
			return wb;
		}


		private Dictionary<string, double> BuildTomorrowFeatureValuesAtBar(BarHistory bars, int lastReal, double priceFactor)
		{
			var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
			if (bars == null || bars.Count == 0 || lastReal < 0 || lastReal >= bars.Count) return v;

			BarHistory wb = new BarHistory(bars);
			for (int i = 0; i <= lastReal; i++) wb.Add(bars.DateTimes[i], bars.Open[i], bars.High[i], bars.Low[i], bars.Close[i], bars.Volume[i]);
			double px = bars.Close[lastReal] * priceFactor;
			DateTime futureDate = bars.DateTimes[lastReal].AddDays(1);
			wb.Add(futureDate, px, px, px, px, bars.Volume[lastReal]);
			int k = wb.Count - 1;

			v["model_cnn_fear_greed"] = _cachedIndicators.model_cnn_fear_greed_base[lastReal];
			v["risk_measure"] = _cachedIndicators.risk_measure_base[lastReal];
			v["model_panic_euphoria"] = _cachedIndicators.model_panic_euphoria_base[lastReal];
			v["myRSI_rep"] = new MyRSI_rep(wb.Close, 14, 8)[k];
			v["trendScore"] = new TrendScore(wb)[k] * 0.1;
			v["aroonUp"] = (-50.0 + new AroonUp(wb.Close, 48)[k]) / 50.0;
			v["aroonDown"] = (-50.0 + new AroonDown(wb.Close, 48)[k]) / 50.0;
			DIMinus dm = new DIMinus(wb, 7); DIPlus dp = new DIPlus(wb, 14);
			v["dIMinus"] = AGC((-30 + dm) / 30)[k];
			v["dIPlus"] = AGC((-25 + dp) / 30)[k];
			v["correlationAngle_rep"] = new CorrelationAngle_rep(wb.Close, 48)[k] / 180.0;
			v["correlationTrend"] = AGC(new CorrelationTrend(wb.Close, 35))[k];
			Hurst hb = new Hurst(wb.Close, 80); EhlersInverseFisherTransform_gpt hf = new EhlersInverseFisherTransform_gpt(hb, 3, 1);
			v["hurst"] = 7.0 * (hf[k] - 0.6);
			CMF cmf = new CMF(wb, 80); EhlersInverseFisherTransform_gpt cmff = new EhlersInverseFisherTransform_gpt(cmf, 3, 10);
			v["cmf"] = cmff[k] - 0.1;
			Decibel db = new Decibel(wb.Close, 96); EhlersInverseFisherTransform_gpt dbf = new EhlersInverseFisherTransform_gpt(db, 8, 0.1);
			v["decibel"] = dbf[k] + 0.55;
			v["jocker"] = _cachedIndicators.jocker_base[lastReal];
			v["marketBreadth"] = _cachedIndicators.marketBreadth_base[lastReal];
			return v;
		}

		private Dictionary<string, double> BuildTomorrowFeatureValues(BarHistory bars, double priceFactor)
		{
			var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
			if (bars == null || bars.Count == 0) return v;

			BarHistory wb = new BarHistory(bars);
			for (int i = 0; i < bars.Count; i++)
				wb.Add(bars.DateTimes[i], bars.Open[i], bars.High[i], bars.Low[i], bars.Close[i], bars.Volume[i]);

			int lastReal = bars.Count - 1;
			double px = bars.Close[lastReal] * priceFactor;
			DateTime futureDate = bars.DateTimes[lastReal].AddDays(1);
			// Для неизвестного будущего OHLC используем нейтральное допущение O=H=L=C=сценарная цена.
			wb.Add(futureDate, px, px, px, px, bars.Volume[lastReal]);
			int k = wb.Count - 1;

			v["model_cnn_fear_greed"] = _cachedIndicators.model_cnn_fear_greed_base[lastReal];
			v["risk_measure"] = _cachedIndicators.risk_measure_base[lastReal];
			v["model_panic_euphoria"] = _cachedIndicators.model_panic_euphoria_base[lastReal];

			v["myRSI_rep"] = new MyRSI_rep(wb.Close, 14, 8)[k];
			v["trendScore"] = new TrendScore(wb)[k] * 0.1;
			v["aroonUp"] = (-50.0 + new AroonUp(wb.Close, 48)[k]) / 50.0;
			v["aroonDown"] = (-50.0 + new AroonDown(wb.Close, 48)[k]) / 50.0;

			DIMinus dm = new DIMinus(wb, 7);
			DIPlus dp = new DIPlus(wb, 14);
			v["dIMinus"] = AGC((-30 + dm) / 30)[k];
			v["dIPlus"] = AGC((-25 + dp) / 30)[k];

			v["correlationAngle_rep"] = new CorrelationAngle_rep(wb.Close, 48)[k] / 180.0;
			v["correlationTrend"] = AGC(new CorrelationTrend(wb.Close, 35))[k];

			Hurst hb = new Hurst(wb.Close, 80);
			EhlersInverseFisherTransform_gpt hf = new EhlersInverseFisherTransform_gpt(hb, 3, 1);
			v["hurst"] = 7.0 * (hf[k] - 0.6);

			CMF cmf = new CMF(wb, 80);
			EhlersInverseFisherTransform_gpt cmff = new EhlersInverseFisherTransform_gpt(cmf, 3, 10);
			v["cmf"] = cmff[k] - 0.1;

			Decibel db = new Decibel(wb.Close, 96);
			EhlersInverseFisherTransform_gpt dbf = new EhlersInverseFisherTransform_gpt(db, 8, 0.1);
			v["decibel"] = dbf[k] + 0.55;

			// Эти два ряда на завтра неизвестны: фиксируем последнее реальное значение.
			v["jocker"] = _cachedIndicators.jocker_base[lastReal];
			v["marketBreadth"] = _cachedIndicators.marketBreadth_base[lastReal];
			return v;
		}

		private double RunJordanTomorrowInMemory(string modelFilePath, ModelParameters modelPars, Dictionary<string, double> features, double previousRaw)
		{
			try
			{
				JordanRuntimeModel model = LoadJordanModelBinary(modelFilePath, modelPars);
				float[] input = new float[model.BaseInputSize];
				for (int j = 0; j < model.BaseInputSize; j++)
				{
					string featureName = ActiveFeatureNames[j];
					double value = features.TryGetValue(featureName, out double x) ? x : 0.0;
					input[j] = double.IsNaN(value) || double.IsInfinity(value) ? 0.0f : (float)value;
				}
				float prev = (!double.IsNaN(previousRaw) && !double.IsInfinity(previousRaw)) ? (float)previousRaw : model.InitialContext;
				return model.Forward(input, prev);
			}
			catch (Exception ex)
			{
				AuditHistory.Log($"Ошибка TOMORROW инференса Jordan-модели {Path.GetFileName(modelFilePath)}: {ex.Message}", false);
				return double.NaN;
			}
		}

		private TimeSeries CalculateMarketBreadthBase(BarHistory bars)
		{
			var tickers = new List<string> { "SPY", "XLV", "XLU", "XLE", "XLP" };
			const int maPeriod = 125;
			const int emaSmoothPeriod = 3;

			var tickerData = new Dictionary<string, (TimeSeries close, TimeSeries vwma)>();
			foreach (string ticker in tickers)
			{
				try
				{
					BarHistory extBars = AuditHistory.Get(bars, ticker);
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

		#region Price Overlay Drawing

		private void DrawZigZagOnPrice(BarHistory bars)
		{
			TimeSeries averages = (bars.High + bars.Low + bars.Close) / 3;
			PeakTroughCalculator ptc = new PeakTroughCalculator(
				averages,
				reversalPercentage,
				PeakTroughReversalType.Percent);

			for (int n = 1; n < ptc.PeakTroughs.Count; n++)
			{
				int x1 = ptc.PeakTroughs[n - 1].PeakTroughIndex;
				double y1 = ptc.PeakTroughs[n - 1].Value;
				int x2 = ptc.PeakTroughs[n].PeakTroughIndex;
				double y2 = ptc.PeakTroughs[n].Value;

				WLColor lineColor = y2 > y1 ? WLColor.Green : WLColor.Red;
				DrawLine(x1, y1, x2, y2, lineColor, 2, LineStyle.Solid);
			}

			// Сохраняем существующую серую пунктирную проекцию последнего плеча ZigZag.
			if (ptc.PeakTroughs.Count > 0)
			{
				PeakTrough lastPoint = ptc.PeakTroughs[ptc.PeakTroughs.Count - 1];
				int lastIndex = bars.Count - 1;
				double maxAverage = 0.0;
				double minAverage = 0.0;

				for (int i = lastPoint.PeakTroughIndex; i <= lastIndex; i++)
				{
					if (maxAverage == 0.0 || averages[i] > maxAverage) maxAverage = averages[i];
					if (minAverage == 0.0 || averages[i] < minAverage) minAverage = averages[i];
				}

				bool isLastPeak = lastPoint.Type == PeakTroughType.Peak;
				double projectedEndValue = isLastPeak ? minAverage : maxAverage;

				DrawLine(
					lastPoint.PeakTroughIndex,
					lastPoint.Value,
					lastIndex,
					projectedEndValue,
					WLColor.Gray,
					2,
					LineStyle.Dashed);
			}
		}

		private void PlotSuperSmoother8OnPrice(BarHistory bars)
		{
			// Фиксированный восьмидневный SuperSmoother, независимо от model/system smoothingPeriod.
			TimeSeries averagePrice = (bars.Close + bars.High + bars.Low) / 3;
			TimeSeries ss8 = new SuperSmoother(averagePrice, 8);

			PlotTimeSeries(
				ss8,
				"SuperSmoother 8",
				"Price",
				WLColor.Orange,
				PlotStyle.Line);
		}

		#endregion

		#region CalculatePredict Methods

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

		private void CalculatePredictWithPeakTrough(BarHistory bars)
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
				DrawLine(lastPoint.PeakTroughIndex, lastPoint.Value, lastIndex, predictedEndValue, forecastLineColor, 2, LineStyle.Dashed);

				for (int i = lastPoint.PeakTroughIndex; i <= lastIndex; i++)
					predict[i] = !isLastPeak ? 1.0 : 0.0;
			}
		}

		#endregion

		#region Build And Plot Deciles

		private void BuildAndPlotDeciles(BarHistory bars, List<(string FileName, double BenchValue, TimeSeries ForecastTS)> modelsData, string packageLabel, int totalModels)
		{
			modelsData.Sort((a, b) => a.BenchValue.CompareTo(b.BenchValue));
			int totalModelsForDeciles = modelsData.Count;

			// Используем глобальный массив this.decileSeries, уже инициализированный в Initialize
			for (int g = 0; g < 10; g++)
			{
				this.decileSeries[g] = new TimeSeries(bars.DateTimes, 0.0);
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

				var model = modelsData[i];
				decileCounts[groupIndex]++;

				if (!double.IsNaN(model.BenchValue))
				{
					if (model.BenchValue < decileMinBench[groupIndex]) decileMinBench[groupIndex] = model.BenchValue;
					if (model.BenchValue > decileMaxBench[groupIndex]) decileMaxBench[groupIndex] = model.BenchValue;
				}

				for (int bar = 0; bar < model.ForecastTS.Count; bar++)
				{
					this.decileSeries[groupIndex][bar] += model.ForecastTS[bar];
				}
			}

			for (int bar = 0; bar < bars.Count; bar++)
			{
				for (int g = 0; g < 10; g++)
				{
					if (decileCounts[g] > 0) this.decileSeries[g][bar] = (this.decileSeries[g][bar] / decileCounts[g]) * 100.0;
				}
			}

			const double NEUTRAL_LEVEL = 50.0;
			for (int g = 0; g < 10; g++)
			{
				int lastBar = this.decileSeries[g].Count - 1;
				decileAvgPercent[g] = !double.IsNaN(this.decileSeries[g][lastBar]) ? this.decileSeries[g][lastBar] : 0.0;

				double sum = 0, madSum = 0;
				int count = 0;
				for (int bar = 0; bar < this.decileSeries[g].Count; bar++)
				{
					if (!double.IsNaN(this.decileSeries[g][bar]) && this.decileSeries[g][bar] != 0.0)
					{
						sum += this.decileSeries[g][bar];
						madSum += Math.Abs(this.decileSeries[g][bar] - NEUTRAL_LEVEL);
						count++;
					}
				}
				decileMeanAll[g] = count > 0 ? sum / count : 0.0;
				decileMAD_50[g] = count > 0 ? madSum / count : 0.0;
				decileAmplitudeRatio_50[g] = (decileMAD_50[g] / NEUTRAL_LEVEL) * 100.0;
			}

			AuditHistory.Log("", false);
			AuditHistory.Log("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", false);
			AuditHistory.Log($"│                                                                     РАСПРЕДЕЛЕНИЕ МОДЕЛЕЙ ПО 10 ГРАДАЦИЯМ (ДЕЦИЛЯМ) BENCH ({packageLabel})                                                                  │", false);
			AuditHistory.Log("──────────┬──────────────────────────────┬──────────────┬────────────────┬──────────────┬──────────────┬──────────────┐", false);
			AuditHistory.Log("│ Градация │ Кол-во моделей │   Min Bench  │   Max Bench  │ Ср. % голосов  │ Средн. за всё│  MAD от 50   │  Ratio к 50  │", false);
			AuditHistory.Log("│          │                │              │              │ (послед. бар)  │   время      │  (площадь)   │     (%)      │", false);
			AuditHistory.Log("├──────────┼────────────────┼──────────────┼──────────────┼──────────────────────────────┼──────────────┼──────────────", false);

			for (int g = 0; g < 10; g++)
			{
				string minB = decileMinBench[g] == double.MaxValue ? "N/A" : decileMinBench[g].ToString("F2");
				string maxB = decileMaxBench[g] == double.MinValue ? "N/A" : decileMaxBench[g].ToString("F2");
				AuditHistory.Log($"│  {(g + 1) * 10,2}     │      {decileCounts[g],4}      │ {minB,12} │ {maxB,12} │ {decileAvgPercent[g],14:F2} │ {decileMeanAll[g],12:F2} │ {decileMAD_50[g],12:F2} │ {decileAmplitudeRatio_50[g],11:F1}% │", false);
			}
			AuditHistory.Log("└──────────┴──────────────────────────────┴──────────────┴────────────────┴──────────────┴────────────────────────────┘", false);

			int bestDecile = 0, worstDecile = 0;
			double maxMAD = double.MinValue, minMAD = double.MaxValue;
			for (int g = 0; g < 10; g++)
			{
				if (decileMAD_50[g] > maxMAD) { maxMAD = decileMAD_50[g]; bestDecile = g; }
				if (decileMAD_50[g] < minMAD && decileMAD_50[g] > 0) { minMAD = decileMAD_50[g]; worstDecile = g; }
			}
			AuditHistory.Log($"  ★ Дециль с максимальной поляризацией ({packageLabel}): #{bestDecile + 1} (MAD от 50 = {maxMAD:F2}, среднее = {decileMeanAll[bestDecile]:F2}%)", false);
			AuditHistory.Log($"  ○ Дециль с минимальной поляризацией ({packageLabel}): #{worstDecile + 1} (MAD от 50 = {minMAD:F2}, среднее = {decileMeanAll[worstDecile]:F2}%)", false);

			WLColor[] colors = { WLColor.Red, WLColor.OrangeRed, WLColor.Orange, WLColor.Gold, WLColor.YellowGreen, WLColor.GreenYellow, WLColor.Lime, WLColor.Green, WLColor.DarkGreen, WLColor.Blue };
			for (int g = 0; g < 10; g++)
			{
				PlotTimeSeries(this.decileSeries[g], $"Decile {g} ({decileCounts[g]} mod)", "Decile", colors[g], PlotStyle.Line);
			}
			SetPaneDrawingOptions("Decile", 30);
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

			var sortedModels = modelsData.OrderBy(m => m.BenchValue).ToList();
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

			AuditHistory.Log("", false);
			AuditHistory.Log("────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────......", false);
			AuditHistory.Log($"│                                                              АНАЛИЗ ВКЛАДА КАЖДОГО ДЕЦИЛЯ В ИТОГОВУЮ ЭКВИТИ (Leave-One-Out) ({packageLabel})                                                              │", false);
			AuditHistory.Log("──────────┬────────────────┬────────────────────────────────────────────────┬────────────────────────────────", false);
			AuditHistory.Log("│ Градация │ Кол-во моделей │ Инд. эквити    │  LOO эквити    │   Δ Вклад      │ Вклад (%)      │   Вердикт      │", false);
			AuditHistory.Log("│          │                │ (сам по себе)  │ (без дециля)   │ (полная - LOO) │ от полной      │                │", false);
			AuditHistory.Log("├──────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────────", false);

			for (int g = 0; g < 10; g++)
			{
				string verdict = decileDelta[g] > 0.01 ? "★ ПОЛЕЗЕН" : (decileDelta[g] < -0.01 ? "✗ ВРЕДИТ" : "○ НЕЙТРАЛ");
				AuditHistory.Log($"│  {(g + 1) * 10,2}     │      {decileCounts[g],4}      │ {decileIndividualEquity[g],14:F2} │ {decileLOOEquity[g],14:F2} │ {decileDelta[g],+14:F2} │ {decileDeltaPercent[g],+12:F1}% │ {verdict,-14} │", false);
			}

			AuditHistory.Log("├──────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────────┼────────────────┤", false);
			AuditHistory.Log($"│   ИТОГО  │     {packageModelCount,4}      │ {"",-14} │ {"",-14} │ {equityFull,+14:F2} │ {"100.0%",13} │ Полная сист. │", false);
			AuditHistory.Log("└──────────┴────────────────┴────────────────┴────────────────────────────────┴────────────────┴────────────────", false);

			var usefulDeciles = new List<int>();
			var harmfulDeciles = new List<int>();
			var neutralDeciles = new List<int>();

			for (int g = 0; g < 10; g++)
			{
				if (decileDelta[g] > 0.01) usefulDeciles.Add(g);
				else if (decileDelta[g] < -0.01) harmfulDeciles.Add(g);
				else neutralDeciles.Add(g);
			}

			AuditHistory.Log($"  ★ Полезных децилей (Δ > 0) ({packageLabel}): {usefulDeciles.Count} — " + string.Join(", ", usefulDeciles.Select(g => $"#{g + 1}(Δ={decileDelta[g]:+F2})")), false);
			AuditHistory.Log($"  ✗ Вредных децилей (Δ < 0) ({packageLabel}): {harmfulDeciles.Count} — " + string.Join(", ", harmfulDeciles.Count > 0 ? harmfulDeciles.Select(g => $"#{g + 1}(Δ={decileDelta[g]:+F2})") : new[] { "—" }), false);
			AuditHistory.Log($"  ○ Нейтральных децилей (Δ ≈ 0) ({packageLabel}): {neutralDeciles.Count}", false);

			if (harmfulDeciles.Count > 0)
			{
				AuditHistory.Log($"  РЕКОМЕНДАЦИЯ ({packageLabel}): следующие децили снижают общую эквити и кандидаты на отключение:", false);
				foreach (int g in harmfulDeciles)
					AuditHistory.Log($"    - Дециль #{g + 1} (моделей: {decileCounts[g]}) → Δ = {decileDelta[g]:+F2} ({decileDeltaPercent[g]:+F1}%)", false);
			}

			if (usefulDeciles.Count > 0)
			{
				AuditHistory.Log($"  НАИБОЛЕЕ ЦЕННЫЕ децили ({packageLabel}) (максимальный положительный вклад):", false);
				var topUseful = usefulDeciles.OrderByDescending(g => decileDelta[g]).Take(3);
				foreach (int g in topUseful)
					AuditHistory.Log($"    + Дециль #{g + 1} (моделей: {decileCounts[g]}, Инд.эквити = {decileIndividualEquity[g]:F2}) → Δ = {decileDelta[g]:+F2} ({decileDeltaPercent[g]:+F1}%)", false);
			}
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
			var sortedModels = modelsData.OrderBy(m => m.BenchValue).ToList();
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
			AuditHistory.Log("", false);
			AuditHistory.Log("┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐", false);
			AuditHistory.Log($"│                                                   АНАЛИЗ ВКЛАДА ДЕЦИЛЕЙ В EQUITY ({packageLabel})                                                   │", false);
			AuditHistory.Log("├──────────┬─────────┬─────────────────┬──────────────────┬──────────────┬────────────┬────────────┤", false);
			AuditHistory.Log("│  Дециль  │ Моделей │  Индив. Equity  │ В ансамбле (сум) │    Вклад     │  % вклада  │   Эффект   │", false);
			AuditHistory.Log("├──────────┼─────────┼─────────────────┼──────────────────┼──────────────┼────────────┼────────────┤", false);

			for (int g = 0; g < 10; g++)
			{
				string effect = decileIndividualEquity[g] > decileInEnsembleEquity[g] ? "✗" : "✓";
				AuditHistory.Log($"│   {(g + 1) * 10,2}   │   {decileCounts[g],4}  │ {decileIndividualEquity[g],15:F2} │ {decileInEnsembleEquity[g],16:F2} │ {decileContribution[g],+12:F2} │ {decileContributionPercent[g],9:F1}% │    {effect}     │", false);
			}

			AuditHistory.Log("├──────────┼─────────┼─────────────────┼──────────────────┼──────────────┼────────────┼────────────┤", false);
			AuditHistory.Log($"│  ИТОГО   │ {packageModelCount,6} │ {"",-15} │ {ensembleEquity,+16:F2} │ {"",-12} │ {"100.0%",10} │ {"",-12} │", false);
			AuditHistory.Log("└──────────┴─────────┴─────────────────┴──────────────────┴──────────────┴────────────┴────────────┘", false);

			// Статистика
			var bestDecile = Enumerable.Range(0, 10).OrderByDescending(g => decileContributionPercent[g]).First();
			var worstDecile = Enumerable.Range(0, 10).OrderBy(g => decileContributionPercent[g]).First();

			AuditHistory.Log("", false);
			AuditHistory.Log($"  ★ Лучший дециль ({packageLabel}): #{bestDecile + 1} (вклад: {decileContributionPercent[bestDecile]:F1}%, индив. equity: {decileIndividualEquity[bestDecile]:F2})", false);
			AuditHistory.Log($"  ○ Худший дециль ({packageLabel}): #{worstDecile + 1} (вклад: {decileContributionPercent[worstDecile]:F1}%, индив. equity: {decileIndividualEquity[worstDecile]:F2})", false);

			// Сколько децилей работают в плюс
			int profitableDeciles = Enumerable.Range(0, 10).Count(g => decileIndividualEquity[g] > 0);
			AuditHistory.Log($"  Прибыльных децилей ({packageLabel}): {profitableDeciles} из 10", false);
		}

		#endregion

		#region Execute Method

		public override void Execute(BarHistory bars, int idx)
		{
			if (idx == 0) return;

			if (aggregated_ensemble == null || aggregated_ensemble_RAW == null || totalModelCount == 0)
			{
				return;
			}

			if (bars.DateTimes[idx] >= startDateTraining && bars.DateTimes[idx] <= endDateTraining)
				SetBackgroundColor(bars, idx, WLColor.FromArgb(20, WLColor.Green), "Price");

			DateTime currentDate = bars.DateTimes[idx];
			double dailyProfit = bars.Close[idx] - bars.Close[idx - 1];

			double rawAvg = aggregated_ensemble_RAW[idx] / totalModelCount;
			int forecastValue = rawAvg >= level_class ? 1 : 0;
			int forecastPrevValue = idx > 0 ? (aggregated_ensemble_RAW[idx - 1] / totalModelCount >= level_class ? 1 : 0) : 0;

			#region Проторговка (Ensemble Logic)

			int currentQty = (int)OpenQuantity;
			int desiredQty = 0;

			if (tradingMode == 0)
			{
				// Старая логика: Пропорционально общему числу моделей
				if (totalModelCount <= 10)
					desiredQty = (int)Math.Round(aggregated_ensemble[idx]);
				else
				{
					double gradationFactor = totalModelCount / 10.0;
					double normalizedQty = aggregated_ensemble[idx] / gradationFactor;
					desiredQty = (int)Math.Round(normalizedQty, MidpointRounding.AwayFromZero);
					desiredQty = Math.Max(0, Math.Min(10, desiredQty));
				}
			}
			else
			{
				// Новая логика: Независимые децили (> порог)
				for (int g = 0; g < 10; g++)
				{
					if (this.decileSeries[g] != null && this.decileSeries[g][idx] >= decileThreshold)
					{
						desiredQty++;
					}
				}
				// Ограничиваем диапазон от 0 до 10 позиций
				desiredQty = Math.Max(0, Math.Min(10, desiredQty));
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
					if (currentDate >= startDateTraining && currentDate <= endDateTraining)
						cumulative_test[idx] = cumulative_test[idx - 1] + testWeightDuringTraining * dailyProfit;
					else
						cumulative_test[idx] = cumulative_test[idx - 1] + dailyProfit;
				}
				else cumulative_test[idx] = cumulative_test[idx - 1];
			}
			else cumulative_test[idx] = cumulative_test[idx - 1];

			if (currentDate >= startDateTraining && currentDate <= endDateTraining)
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
