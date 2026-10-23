namespace BreezeVL;

/// <summary>
/// User-facing wording for the compact research model.  Internal event IDs and
/// English research names remain available in CSV exports and Help, but the
/// operational screen deliberately speaks in ordinary Russian.
/// </summary>
internal static class HumanLanguage
{
    public static string Profile(string profile) => profile switch
    {
        "Soft" => "Soft",
        "Standard" => "Standard",
        "Hard" or "Strong" => "Hard",
        "Legacy" => "Legacy",
        _ => profile
    };

    public static string ProfileMode(string profile) => profile switch
    {
        "Soft" => "SOFT",
        "Standard" => "STANDARD",
        "Hard" or "Strong" => "HARD",
        "Legacy" => "LEGACY",
        _ => profile.ToUpperInvariant()
    };

    public static string PositionDecision(double value) =>
        value >= ModelPositionSizing.FixedLevel ? "позиция открыта" : "позиция закрыта";

    public static string CurveState(VixCurveState state) => state switch
    {
        VixCurveState.StrongContango => "выраженное контанго",
        VixCurveState.Contango => "контанго",
        VixCurveState.Mixed => "смешанная кривая с частичной инверсией",
        VixCurveState.Backwardation => "бэквордация",
        VixCurveState.StrongBackwardation => "широкая бэквордация",
        _ => "данных нет"
    };

    public static string MarketZone(MarketZone zone) => zone switch
    {
        BreezeVL.MarketZone.Buy => "покупка",
        BreezeVL.MarketZone.PrepareBuy => "подготовка к покупке",
        BreezeVL.MarketZone.Neutral => "нейтральное состояние",
        BreezeVL.MarketZone.Caution => "осторожность",
        BreezeVL.MarketZone.PrepareSell => "подготовка к сокращению позиции",
        BreezeVL.MarketZone.ReduceRisk => "сокращение риска",
        _ => "состояние не определено"
    };

    public static string MarketRegime(MarketZone zone) => zone switch
    {
        BreezeVL.MarketZone.Buy => "ПОКУПКА",
        BreezeVL.MarketZone.PrepareBuy => "ПОДГОТОВКА К ПОКУПКЕ",
        BreezeVL.MarketZone.Neutral => "НЕЙТРАЛЬНЫЙ",
        BreezeVL.MarketZone.Caution => "ОСТОРОЖНОСТЬ",
        BreezeVL.MarketZone.PrepareSell => "ПОДГОТОВКА К СОКРАЩЕНИЮ ПОЗИЦИИ",
        BreezeVL.MarketZone.ReduceRisk => "СОКРАЩЕНИЕ РИСКА",
        _ => "НЕ ОПРЕДЕЛЁН"
    };

    public static string Event(string id) => id switch
    {
        "T03" => "устойчивый рост рынка",
        "T05" => "расстояние между ценой и правильно расположенными скользящими средними",
        "T06" => "сжатые средние перед возможным движением",
        "T07" => "цена ниже всех четырёх средних",
        "T08" => "текущая глубина снижения от годового максимума",
        "T09" => "несколько снижений цены после промежуточных отскоков",
        "E01" => "пробой средних сверху вниз",
        "E02" => "поддержка цены на средних внутри дня",
        "E05" => "возврат цены выше одной из скользящих средних",
        "E06" => "пересечения между средними",
        "R01" => "оба индикатора MyRSI в нижней зоне",
        "R03" => "выход MyRSI из нижней зоны",
        "B01" => "текущий уровень широты рынка",
        "B02" => "крайне низкая широта рынка",
        "B03" => "крайне низкая доля акций выше 200-дневной средней",
        "B07" => "ослабление участия акций у вершины рынка",
        "B08" => "бычье расхождение широты и цены",
        "B09" => "разворот широты после формирования дна",
        "P02" => "зелёная свеча с длинной нижней тенью",
        "P03" => "повышенный дневной диапазон цены",
        "P05" => "двух–трёхдневное движение цены",
        "V01" => "ближайшие фьючерсы VIX находятся в контанго",
        "V03" => "частично инвертированная кривая VIX",
        "V04" => "первый день бэквордации",
        "V07" => "до экспирации ближайшего фьючерса VIX осталось мало времени",
        "V08" => "снижение VIX после стрессового уровня",
        "Z02" => "плотность импульса широты Цвейга",
        "Z03" => "одновременное действие двух режимов Цвейга",
        "I01" => "начало сигнала «Титаник»",
        "C01" => "сильный MyRSI при растущем тренде",
        "C05" => "возобновление неглубокого роста",
        "C08" => "двойной разворот MyRSI с сильным красным пробоем",
        "U03" => "месячная экспирация SPX",
        _ => "наблюдаемое рыночное событие"
    };

    public static string Contribution(string id, double value)
    {
        return $"{Event(id)}: {value:+0.0;-0.0;0.0} балла к оценке модели";
    }
}
