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

        AddXiaoYiliMemorialReminder(matches, start, end, 2007, "第二年");
        AddXiaoYiliMemorialReminder(matches, start, end, 2008, "第三年");

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

    private static void AddXiaoYiliMemorialReminder(
        List<FlySkyWeekEvent> matches,
        DateOnly weekStart,
        DateOnly weekEnd,
        int year,
        string gameYear)
    {
        var windowStart = new DateOnly(year, 3, 1);
        var windowEnd = new DateOnly(year, 4, 30);
        if (windowStart > weekEnd || windowEnd < weekStart) return;

        matches.Add(new FlySkyWeekEvent(
            "萧依莉",
            false,
            false,
            false,
            $"30（{gameYear}）",
            "第一轮剧情（必死）",
            "场景",
            "触发过事件18后，于依莉过世周年的三、四月前往公园樱花树下，主角怀念依莉。",
            $"{gameYear}3月至4月 · 前往公园樱花树下",
            71));
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
        var catalog = JsonSerializer.Deserialize<FlySkyCatalog>(stream, JsonOptions.Default)
            ?? throw new InvalidOperationException("FlySky event data could not be parsed.");

        var artists = catalog.Artists.Select(artist => artist.Name == "萧依莉"
            ? artist with
            {
                Events = artist.Events.Select(item => item.Section == "事件"
                    ? item with { Section = "第一轮剧情（必死）" }
                    : item).ToArray()
            }
            : artist).ToArray();
        var specialEvents = catalog.SpecialEvents.Select(special => special.Name == "美丽之星"
            ? ExpandBeautyStarByYear(special)
            : special).ToArray();
        var addedBeautyStarEvents = specialEvents
            .Where(special => special.Name == "美丽之星")
            .Sum(special => special.EventCount) - catalog.SpecialEvents
            .Where(special => special.Name == "美丽之星")
            .Sum(special => special.EventCount);
        return catalog with
        {
            Artists = artists,
            SpecialEvents = specialEvents,
            EventCount = catalog.EventCount + addedBeautyStarEvents
        };
    }

    private static FlySkyArtistEvents ExpandBeautyStarByYear(FlySkyArtistEvents special)
    {
        var editions = new[]
        {
            new BeautyStarEdition(2006, "第一年", "夏威夷", "仪态200、动感200、自信200", "仪态200、动感200、自信200、演技200、体能200", 450),
            new BeautyStarEdition(2007, "第二年", "巴黎", "仪态400、动感300、自信400", "仪态500、动感400、自信500、演技400、体能400", 700),
            new BeautyStarEdition(2008, "第三年", "纽约", "仪态600、动感400、自信600", "仪态800、动感600、自信800、演技600、体能600", 900)
        };
        var events = editions.SelectMany(edition => special.Events.Select(item => item with
        {
            Section = $"{edition.GameYear}美丽之星（{edition.Year}）",
            Text = BeautyStarTextForEdition(item, edition),
            Windows = item.Windows.Where(window =>
                DateOnly.TryParse(window.Start, out var start) && start.Year == edition.Year).ToArray()
        })).ToArray();

        return special with
        {
            SourceTitle = $"{special.SourceTitle}（按年度拆分）",
            EventCount = events.Length,
            Events = events
        };
    }

    private static string BeautyStarTextForEdition(FlySkyEvent item, BeautyStarEdition edition) => item.Id switch
    {
        "1" => $"{edition.Year}年4月自动发生，“谁是亚洲美丽之星？美丽之星即日起开始接受报名”。",
        "4" => $"艺人初选结束后当天，秘书收到比赛结果。\n提醒：{edition.GameYear}初选获胜条件：称号必须是模特系列，且在“名模”以上；{edition.PreliminaryRequirements}。",
        "6" => $"{edition.Year}年7月中旬，“模特界最高荣誉！下个月于{edition.Location}角逐美丽之星年度代表”。",
        "7" => $"旗下有艺人通过{edition.GameYear}初选，7月最后一个周日，秘书提醒下周就是总决选了。",
        "8" => $"旗下艺人参加{edition.GameYear}决选当天，前往{edition.Location}，主角为艺人加油打气。",
        "10" => $"上个事件后，旗下有艺人胜出时，巴黎店长送来600万奖金和告知参加慈善义演的事宜。\n提醒：{edition.GameYear}决选获胜条件：称号必须是模特系列，且在“名模”以上；{edition.FinalRequirements}。",
        "11" => $"事件10后，艺人进行慈善义演后，且名气达到{edition.FameRequirement}时，“国际媒体一致赞扬，XXX形象清新，慈善义演金额创新高”。",
        _ => item.Text
    };

    private sealed record BeautyStarEdition(
        int Year,
        string GameYear,
        string Location,
        string PreliminaryRequirements,
        string FinalRequirements,
        int FameRequirement);

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
