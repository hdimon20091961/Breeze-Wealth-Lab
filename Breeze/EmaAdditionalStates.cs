using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;

namespace BreezeVL;

internal sealed record EmaStateParameters(double AtrpMaxPercent, double[] Breadths, int ConfirmationBars);
internal sealed record EmaStateDefinition(int Number, string Id, string Formula, int Direction);
internal sealed record EmaStatePoint(Dictionary<string, double> Values, Dictionary<string, double> Details);

// Additional display detectors are deliberately separate from the frozen 75-score vector.
internal static class EmaAdditionalStates
{
    public static readonly EmaStateDefinition[] Definitions =
    [
        new(131,"T01B","E20<E50<E100<E200",-1),
        new(132,"T02B","All four one-bar EMA slopes <0",-1),
        new(133,"T03B","Bear order + all slopes falling + C<E20 + configured breadth ceilings + ATRP cap; observed age/k capped at 1",-1),
        new(134,"T03_1","Bull order + all slopes rising + C>E20 + configured breadth floors + ATRP cap; observed age>=k",1),
        new(135,"T03_1B","Bear order + all slopes falling + C<E20 + configured breadth ceilings + ATRP cap; observed age>=k",-1),
        new(136,"T04_1","EMA slopes: -,+,+,+",1),
        new(137,"T04_2","EMA slopes: +,-,+,+",1),
        new(138,"T04_3","EMA slopes: -,-,+,+",1),
        new(139,"T04_4","EMA slopes: +,-,-,-",-1),
        new(140,"T04_5","EMA slopes: -,+,-,-",-1),
        new(141,"T04_6","EMA slopes: +,+,-,-",-1)
    ];
    private static readonly int[][] Patterns =
    [ [-1,1,1,1], [1,-1,1,1], [-1,-1,1,1], [1,-1,-1,-1], [-1,1,-1,-1], [1,1,-1,-1] ];

    public static IReadOnlyList<EmaStatePoint> Calculate(IReadOnlyList<M.Row> rows, IReadOnlyList<BreezeRuleEntry> rules)
    {
        var result = new List<EmaStatePoint>(rows.Count);
        var counters = new int[rules.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            M.Row row = rows[index];
            bool finite = index > 0 && double.IsFinite(row.Get("T01"));
            double[] slopes = index > 0 ? Enumerable.Range(0,4).Select(k => row.E[k]-rows[index-1].E[k]).ToArray() : new double[4];
            var values = new Dictionary<string,double>(StringComparer.Ordinal);
            var details = new Dictionary<string,double>(StringComparer.Ordinal) { ["ATRP1"] = row.Atr };
            for (int k=0;k<4;k++)
            {
                int period = new[] {20,50,100,200}[k];
                details[$"EMA{period}.level"] = row.E[k];
                details[$"EMA{period}.slope"] = slopes[k];
                details[$"Breadth{k}"] = row.B[k];
            }
            for (int j=0;j<rules.Count;j++)
            {
                BreezeRuleEntry rule=rules[j];
                bool condition=false;
                if (finite)
                {
                    if (rule.Id=="T01B") condition=Enumerable.Range(0,3).All(k=>row.E[k]<row.E[k+1]);
                    else if (rule.Id=="T02B") condition=slopes.All(v=>v<0);
                    else if (rule.Id.StartsWith("T04_",StringComparison.Ordinal))
                    {
                        int pattern=int.Parse(rule.Id.AsSpan(4),System.Globalization.CultureInfo.InvariantCulture)-1;
                        condition=Enumerable.Range(0,4).All(k=>slopes[k]*Patterns[pattern][k]>0);
                    }
                    else if (rule.Parameters is EmaStateParameters parameters)
                    {
                        int direction=rule.DisplayDirection;
                        condition=Enumerable.Range(0,3).All(k=>direction*(row.E[k]-row.E[k+1])>0)
                            && slopes.All(v=>direction*v>0) && direction*(row.Close-row.E[0])>0
                            && Enumerable.Range(0,4).All(k=>direction==1 ? row.B[k]>=parameters.Breadths[k] : row.B[k]<=100-parameters.Breadths[k])
                            && row.Atr<=parameters.AtrpMaxPercent;
                    }
                }
                counters[j]=rule.Parameters is not null && condition ? counters[j]+1 : 0;
                double strength=rule.Parameters is EmaStateParameters p ? Math.Min(counters[j]/(double)p.ConfirmationBars,1) : condition ? 1 : 0;
                double value=rule.Id=="T03B" ? strength : rule.Parameters is EmaStateParameters cp
                    ? counters[j]>=cp.ConfirmationBars ? 1 : 0 : condition ? 1 : 0;
                values[rule.Id]=finite ? value : double.NaN;
                if(rule.Parameters is not null)
                {
                    details[rule.Id+".age"]=counters[j];
                    details[rule.Id+".strength"]=strength;
                    details[rule.Id+".condition"]=condition ? 1 : 0;
                }
            }
            result.Add(new(values,details));
        }
        return result;
    }
}
