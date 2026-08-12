using System.Reflection;
using System.Text.Json;

namespace Stardom3Assistant;

internal static class FlySkyEventCatalog
{
    private static readonly Lazy<FlySkyCatalog> Catalog = new(LoadCatalog);
    private static readonly IReadOnlyDictionary<string, (int Month, int Day)> ArtistBirthdays =
        new Dictionary<string, (int Month, int Day)>(StringComparer.Ordinal)
        {
            ["天晴"] = (2, 7),
            ["萧依莉"] = (2, 20),
            ["卫亚"] = (2, 29),
            ["克烈斯"] = (3, 18),
            ["路敏"] = (3, 21),
            ["原少纬"] = (4, 9),
            ["路风"] = (5, 19),
            ["新名纱雪"] = (5, 22),
            ["关古威"] = (6, 11),
            ["苏嫚君"] = (7, 13),
            ["聆香"] = (8, 12),
            ["陈奕夫"] = (8, 22),
            ["桑禾蓓"] = (8, 29),
            ["欧怡青"] = (10, 7),
            ["姚子莹"] = (11, 11),
            ["林芬芬"] = (11, 12),
            ["纪翔"] = (12, 1),
            ["姚子奇"] = (12, 28)
        };

    public static FlySkyCatalog All => Catalog.Value;

    public static FlySkyWeekSnapshot ForWeek(DateOnly currentDate, IReadOnlyList<ArtistSnapshot> roles)
    {
        var daysSinceMonday = ((int)currentDate.DayOfWeek + 6) % 7;
        var start = currentDate.AddDays(-daysSinceMonday);
        var end = start.AddDays(6);
        var signedArtists = roles.Where(x => x.IsSigned).Select(x => NormalizeName(x.Name)).ToHashSet(StringComparer.Ordinal);
        var matches = new List<FlySkyWeekEvent>();

        foreach (var artist in All.Artists)
        {
            var isSigned = signedArtists.Contains(artist.Name);
            var signingEvents = artist.Events.Where(IsSigningEvent).ToArray();
            var hasOpenSigningWindow = !isSigned && IsSigningAvailable(artist.Name, currentDate, signingEvents);

            foreach (var item in artist.Events)
            {
                FlySkyDateWindow? window;
                var isRecruitment = false;
                if (isSigned)
                {
                    window = item.Windows.FirstOrDefault(value =>
                        DateOnly.TryParse(value.Start, out var windowStart) &&
                        DateOnly.TryParse(value.End, out var windowEnd) &&
                        windowStart <= end && windowEnd >= start);
                    if (window is null) continue;
                }
                else
                {
                    if (!hasOpenSigningWindow || !IsSigningEvent(item)) continue;
                    isRecruitment = true;
                    window = item.Windows.FirstOrDefault(value =>
                        DateOnly.TryParse(value.Start, out var windowStart) &&
                        DateOnly.TryParse(value.End, out var windowEnd) &&
                        windowStart <= end && windowEnd >= start);
                    // 提醒栏只收录有明确日历限制、且与本周相交的签约步骤。
                    if (window is null) continue;
                }

                matches.Add(new FlySkyWeekEvent(
                    artist.Name,
                    isSigned,
                    isRecruitment,
                    false,
                    item.Id,
                    item.Section,
                    item.Kind,
                    item.Text,
                    window?.Label ?? "签约流程（请核对前置）",
                    item.SourceLine));
            }
        }

        foreach (var special in All.SpecialEvents)
        {
            foreach (var item in special.Events)
            {
                // 父亲节采用下方统一的年度提醒，避免第一年与固定提醒重复显示。
                if (special.Name == "金父与莉铃" &&
                    item.Section.StartsWith("父亲节", StringComparison.Ordinal) &&
                    item.Id == "11")
                    continue;

                var window = item.Windows.FirstOrDefault(value =>
                    DateOnly.TryParse(value.Start, out var windowStart) &&
                    DateOnly.TryParse(value.End, out var windowEnd) &&
                    windowStart <= end && windowEnd >= start);
                if (window is null) continue;

                matches.Add(new FlySkyWeekEvent(
                    special.Name,
                    false,
                    false,
                    true,
                    item.Id,
                    item.Section,
                    item.Kind,
                    item.Text,
                    window.Label,
                    item.SourceLine));
            }
        }

        for (var year = 2006; year <= 2008; year++)
        {
            var fathersDay = new DateOnly(year, 8, 8);
            AddAdvanceReminder(
                matches,
                currentDate,
                fathersDay,
                "金父与莉铃",
                "父亲节（重点必做）",
                "父亲节",
                "8月8日是父亲节，请前往公园看望金父。第一、二年都必须探望，关系到飞天白玉同心结与金父后续剧情。",
                isSignedArtist: false,
                leadDays: 7);
        }

        for (var year = 2006; year <= 2008; year++)
        {
            AddAdvanceReminder(
                matches,
                currentDate,
                new DateOnly(year, 2, 14),
                "年度节日",
                "情人节",
                "情人节提醒",
                "2月14日是情人节，请提前安排旗下艺人的行程，并确认可能触发的约会、沟通与场景事件。",
                isSignedArtist: false);
            AddAdvanceReminder(
                matches,
                currentDate,
                new DateOnly(year, 12, 25),
                "年度节日",
                "圣诞节",
                "圣诞节提醒",
                "12月25日是圣诞节，请提前安排旗下艺人的行程，并确认可能触发的聚会、约会与场景事件。",
                isSignedArtist: false);
        }

        foreach (var role in roles.Where(item => item.IsSigned))
        {
            var artistName = NormalizeName(role.Name);
            if (!ArtistBirthdays.TryGetValue(artistName, out var birthday)) continue;
            if (birthday.Day > DateTime.DaysInMonth(currentDate.Year, birthday.Month)) continue;

            var birthdayDate = new DateOnly(currentDate.Year, birthday.Month, birthday.Day);
            AddAdvanceReminder(
                matches,
                currentDate,
                birthdayDate,
                artistName,
                "生日提醒",
                "生日",
                $"{artistName}将在{birthday.Month}月{birthday.Day}日过生日，请提前准备礼物并预留行程。",
                isSignedArtist: true);
        }

        matches.Sort((left, right) =>
        {
            var leftType = left.IsSignedArtist ? 0 : left.IsRecruitment ? 1 : 2;
            var rightType = right.IsSignedArtist ? 0 : right.IsRecruitment ? 1 : 2;
            var type = leftType.CompareTo(rightType);
            if (type != 0) return type;
            var artist = string.Compare(left.Artist, right.Artist, StringComparison.CurrentCulture);
            return artist != 0 ? artist : left.SourceLine.CompareTo(right.SourceLine);
        });

        return new FlySkyWeekSnapshot(
            start.ToString("yyyy-MM-dd"),
            end.ToString("yyyy-MM-dd"),
            matches.Count,
            matches);
    }

    private static void AddAdvanceReminder(
        List<FlySkyWeekEvent> matches,
        DateOnly currentDate,
        DateOnly targetDate,
        string subject,
        string section,
        string id,
        string text,
        bool isSignedArtist,
        int leadDays = 14)
    {
        var reminderStart = targetDate.AddDays(-leadDays);
        if (currentDate < reminderStart || currentDate > targetDate) return;

        var daysRemaining = targetDate.DayNumber - currentDate.DayNumber;
        var countdown = daysRemaining switch
        {
            0 => "今天",
            1 => "明天",
            _ => $"还有{daysRemaining}天"
        };
        matches.Add(new FlySkyWeekEvent(
            subject,
            isSignedArtist,
            false,
            !isSignedArtist,
            id,
            section,
            "提前提醒",
            text,
            $"{targetDate:yyyy-MM-dd} · {countdown}",
            0));
    }

    private static FlySkyCatalog LoadCatalog()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("Data.flysky-events.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("FlySky event data resource is unavailable.");
        return JsonSerializer.Deserialize<FlySkyCatalog>(stream, JsonOptions.Default)
            ?? throw new InvalidOperationException("FlySky event data could not be parsed.");
    }

    private static bool IsSigningEvent(FlySkyEvent item) =>
        item.Section == "签约方法" || item.Section is "加入A" or "加入B";

    private static bool IsSigningAvailable(
        string artistName,
        DateOnly currentDate,
        IReadOnlyList<FlySkyEvent> signingEvents)
    {
        if (signingEvents.Any(item => item.Windows.Any(value =>
                DateOnly.TryParse(value.Start, out var start) &&
                DateOnly.TryParse(value.End, out var end) &&
                start <= currentDate && end >= currentDate)))
            return true;

        // 攻略明确记载的无固定日历入口或延长签约期限。
        return artistName switch
        {
            "关古威" => currentDate >= new DateOnly(2006, 6, 30) && currentDate <= new DateOnly(2008, 12, 31),
            "纪翔" or "天晴" or "卫亚" => currentDate <= new DateOnly(2008, 12, 31),
            _ => false
        };
    }

    private static string NormalizeName(string value) => value switch
    {
        "歐怡青" => "欧怡青",
        "新名紗雪" => "新名纱雪",
        "衛亞" => "卫亚",
        "關古威" => "关古威",
        "紀翔" => "纪翔",
        "蘇嫚君" => "苏嫚君",
        "蕭依莉" => "萧依莉",
        "陳奕夫" => "陈奕夫",
        "姚子瑩" => "姚子莹",
        "原少緯" => "原少纬",
        "路風" => "路风",
        _ => value
    };
}

internal sealed record FlySkyCatalog(
    string Source,
    string SourceVersion,
    IReadOnlyList<FlySkyArtistEvents> Artists,
    IReadOnlyList<FlySkyArtistEvents> SpecialEvents,
    int ArtistCount,
    int SpecialCount,
    int EventCount);

internal sealed record FlySkyArtistEvents(
    string Name,
    string SourceTitle,
    int EventCount,
    IReadOnlyList<FlySkyEvent> Events);

internal sealed record FlySkyEvent(
    string Id,
    string Section,
    string Kind,
    string Text,
    IReadOnlyList<FlySkyDateWindow> Windows,
    int SourceLine);

internal sealed record FlySkyDateWindow(string Start, string End, string Label);

internal sealed record FlySkyWeekSnapshot(
    string? StartDate,
    string? EndDate,
    int EventCount,
    IReadOnlyList<FlySkyWeekEvent> Events)
{
    public static FlySkyWeekSnapshot Empty => new(null, null, 0, []);
}

internal sealed record FlySkyWeekEvent(
    string Artist,
    bool IsSignedArtist,
    bool IsRecruitment,
    bool IsSpecialEvent,
    string Id,
    string Section,
    string Kind,
    string Text,
    string TimeLabel,
    int SourceLine);
