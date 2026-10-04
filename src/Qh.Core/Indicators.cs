namespace Qh.Core;

public static class Indicators
{
    public static IReadOnlyList<IndicatorPoint> Calculate(IReadOnlyList<Bar> bars, int period = 3)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        var result = new List<IndicatorPoint>(bars.Count);
        decimal? ema = null;
        decimal? atr = null;
        decimal smaSum = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            smaSum += bars[i].Close;
            if (i >= period) smaSum -= bars[i - period].Close;
            decimal? sma = i + 1 >= period ? smaSum / period : null;
            if (i + 1 == period) ema = bars.Take(period).Average(x => x.Close);
            else if (i + 1 > period && ema.HasValue) ema += 2m / (period + 1) * (bars[i].Close - ema.Value);

            var tr = i == 0 ? bars[i].High - bars[i].Low : Math.Max(bars[i].High - bars[i].Low, Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close), Math.Abs(bars[i].Low - bars[i - 1].Close)));
            if (i + 1 == period) atr = bars.Take(period).Select((bar, index) => index == 0 ? bar.High - bar.Low : Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - bars[index - 1].Close), Math.Abs(bar.Low - bars[index - 1].Close)))).Average();
            else if (i + 1 > period && atr.HasValue) atr = (atr.Value * (period - 1) + tr) / period;
            result.Add(new IndicatorPoint(bars[i].Timestamp, sma, ema, atr));
        }
        return result;
    }
}
