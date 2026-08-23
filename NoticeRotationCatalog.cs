namespace Stardom3Assistant;

internal static class NoticeRotationCatalog
{
    private static readonly DateOnly FirstUpdateSunday = new(2006, 1, 8);

    public static NoticeRotationSnapshot ForDate(DateOnly currentDate)
    {
        var daysSinceSunday = (int)currentDate.DayOfWeek;
        var currentWeek = currentDate.AddDays(-daysSinceSunday);
        return new NoticeRotationSnapshot(
            CreateWeek(currentWeek),
            CreateWeek(currentWeek.AddDays(7)),
            "v8.01事件备忘录 · 日程模板D列");
    }

    private static NoticeRotationWeek CreateWeek(DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(6);
        var weekIndex = (weekStart.DayNumber - FirstUpdateSunday.DayNumber) / 7;
        if (weekStart < FirstUpdateSunday)
            return new NoticeRotationWeek(
                weekStart.ToString("yyyy-MM-dd"),
                weekEnd.ToString("yyyy-MM-dd"),
                "尚未开始",
                "首次轮换于2006-01-08（周日）更新，进入广告周");

        return PositiveModulo(weekIndex, 4) switch
        {
            0 => new NoticeRotationWeek(
                weekStart.ToString("yyyy-MM-dd"), weekEnd.ToString("yyyy-MM-dd"),
                "广告", weekStart == new DateOnly(2006, 6, 11) ? "追梦广告更新：CD-PRO I 老爸篇" : null),
            1 => TelevisionWeek(weekStart, weekEnd, weekIndex),
            2 => new NoticeRotationWeek(
                weekStart.ToString("yyyy-MM-dd"), weekEnd.ToString("yyyy-MM-dd"),
                "广告、电影", null),
            _ => RecordWeek(weekStart, weekEnd, weekIndex)
        };
    }

    private static NoticeRotationWeek TelevisionWeek(DateOnly start, DateOnly end, int weekIndex)
    {
        var occurrence = (weekIndex - 1) / 4;
        var detail = PositiveModulo(occurrence, 4) switch
        {
            0 => "SoSa单元剧 · 永振连续剧",
            1 => "SoSa连续剧 · 永振短剧",
            2 => "SoSa短剧 · 永振连续剧",
            _ => "SoSa连续剧 · 永振单元剧"
        };
        return new NoticeRotationWeek(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), "电视", detail);
    }

    private static NoticeRotationWeek RecordWeek(DateOnly start, DateOnly end, int weekIndex)
    {
        var occurrence = (weekIndex - 3) / 4;
        var detail = PositiveModulo(occurrence, 3) switch
        {
            0 => "日月光团唱",
            1 => "EAMI团唱",
            _ => "京风团唱"
        };
        return new NoticeRotationWeek(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), "唱片", detail);
    }

    private static int PositiveModulo(int value, int divisor) => (value % divisor + divisor) % divisor;
}

internal sealed record NoticeRotationSnapshot(
    NoticeRotationWeek CurrentWeek,
    NoticeRotationWeek NextWeek,
    string Source)
{
    public static NoticeRotationSnapshot Empty => new(
        new(null, null, "—", null),
        new(null, null, "—", null),
        "v8.01事件备忘录 · 日程模板D列");
}

internal sealed record NoticeRotationWeek(
    string? StartDate,
    string? EndDate,
    string Category,
    string? Detail);
