using System.Globalization;

namespace Qh.Core;

public static class HistoryFormSelection
{
    public static string ResolveContract(string? selectedContract, string? manualContract, bool manualMode)
    {
        var manual = manualContract?.Trim() ?? "";
        if (manualMode && manual.Length > 0) return manual;
        return selectedContract?.Trim() ?? "";
    }

    public static bool TryBuildDateRange(
        DateTime? startDate,
        DateTime? endDate,
        string? advancedStart,
        string? advancedEnd,
        out DateTimeOffset start,
        out DateTimeOffset end,
        out string error)
    {
        start = default;
        end = default;
        error = "";
        var preciseStart = advancedStart?.Trim() ?? "";
        var preciseEnd = advancedEnd?.Trim() ?? "";
        if (preciseStart.Length > 0 || preciseEnd.Length > 0)
        {
            if (preciseStart.Length == 0 || preciseEnd.Length == 0 ||
                !DateTimeOffset.TryParse(preciseStart, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start) ||
                !DateTimeOffset.TryParse(preciseEnd, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out end))
            {
                error = "高级精确时间必须同时填写有效的 ISO 8601 起止时间";
                return false;
            }
        }
        else
        {
            if (startDate is not { } selectedStart || endDate is not { } selectedEnd)
            {
                error = "请选择开始日期和结束日期";
                return false;
            }
            if (selectedEnd.Date == DateTime.MaxValue.Date)
            {
                error = "结束日期过大，无法按整天转换；请选择更早的结束日期";
                return false;
            }
            start = ToBeijingDateStart(selectedStart);
            end = ToBeijingDateStart(selectedEnd.Date.AddDays(1));
        }

        if (end <= start)
        {
            error = "结束日期/时间必须晚于开始日期/时间";
            return false;
        }
        return true;
    }

    private static DateTimeOffset ToBeijingDateStart(DateTime date) =>
        new(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), TimeSpan.FromHours(8));
}
