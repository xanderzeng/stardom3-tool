using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Stardom3Assistant;

internal sealed class GameMemoryReader : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const long DateOffset = 3_506_136;
    private const long RoleListOffset = 3_506_240;
    private const long ActionListOffset = 3_505_964;
    private const long ActionCountOffset = 3_506_004;
    private const long StudioListOffset = 3_506_260;
    private const long ElementListOffset = 3_509_380;
    private const long ItemListOffset = 3_506_152;
    private const long CompanyListOffset = 3_506_248;
    private const long MoneyOffset = 3_518_800;
    private const int RoleSize = 772;
    private const int StudioSize = 48;
    private const int StudioCount = 11;
    private const int MaxActions = 2_000;
    private const int MaxRoles = 256;
    private const int ItemSize = 300;
    private const int MaxItems = 1_024;
    private const int CompanySize = 88;
    private const int MaxCompanies = 64;

    private readonly object _gate = new();
    private Process? _process;
    private nint _handle;
    private long _moduleBase;

    static GameMemoryReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public GameSnapshot ReadSnapshot()
    {
        lock (_gate)
        {
            try
            {
                if (!EnsureConnected())
                    return GameSnapshot.Disconnected("等待 Stardom3.exe 启动并读取存档…");

                var dateValues = ReadInt32Array(_moduleBase + DateOffset, 4);
                if (dateValues is null || !IsValidDate(dateValues))
                    return GameSnapshot.ConnectedWaiting(_process!.Id, "已连接游戏，等待进入存档…");

                var listHeader = ReadBytes(_moduleBase + RoleListOffset, 8);
                if (listHeader is null)
                    return GameSnapshot.ConnectedWaiting(_process!.Id, "艺人表暂不可读，请进入办公室或行程界面。 ");

                var listAddress = BitConverter.ToUInt32(listHeader, 0);
                var count = BitConverter.ToInt32(listHeader, 4);
                if (listAddress == 0 || count <= 0 || count > MaxRoles)
                    return GameSnapshot.ConnectedWaiting(_process!.Id, "艺人数据尚未载入，请先读取存档。 ");

                var roles = new List<ArtistSnapshot>(count);
                for (var i = 0; i < count; i++)
                {
                    var bytes = ReadBytes(listAddress + (long)i * RoleSize, RoleSize);
                    if (bytes is null) continue;
                    var role = ParseRole(bytes);
                    if (role is not null) roles.Add(role);
                }

                var relationships = ReadRelationships();
                for (var i = 0; i < roles.Count; i++)
                {
                    if (relationships.Roles.TryGetValue(roles[i].Id, out var relation))
                        roles[i] = roles[i] with { Favorability = relation.Love, GiftCount = relation.GiftCount };
                }

                roles.Sort((a, b) =>
                {
                    var signedOrder = b.IsSigned.CompareTo(a.IsSigned);
                    return signedOrder != 0 ? signedOrder : a.Id.CompareTo(b.Id);
                });

                var roleNames = roles.ToDictionary(x => x.Id, x => x.Name);
                var signedRoleIds = roles.Where(x => x.IsSigned).Select(x => x.Id).ToHashSet();
                var notices = ReadNotices(roleNames, signedRoleIds, relationships.Producers);
                var currentDate = new DateOnly(dateValues[0], dateValues[1], dateValues[2]);
                var weeklyEvents = FlySkyEventCatalog.ForWeek(currentDate, roles);
                var company = ReadCompanyOverview(roles, relationships.Producers, notices);
                var noticeRotation = NoticeRotationCatalog.ForDate(currentDate);

                return new GameSnapshot(
                    true,
                    true,
                    _process!.Id,
                    null,
                    $"{dateValues[0]:0000}-{dateValues[1]:00}-{dateValues[2]:00}",
                    WeekName(dateValues[3]),
                    DateTimeOffset.Now,
                    roles,
                    relationships.Producers,
                    company,
                    noticeRotation,
                    weeklyEvents,
                    notices.Current,
                    notices.Recruiting);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                Disconnect();
                return GameSnapshot.Disconnected("读取中断，正在重新连接游戏…");
            }
        }
    }

    private bool EnsureConnected()
    {
        if (_process is not null && !_process.HasExited && _handle != 0)
            return true;

        Disconnect();
        var process = Process.GetProcessesByName("Stardom3").FirstOrDefault();
        if (process is null) return false;

        var handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, process.Id);
        if (handle == 0) return false;

        try
        {
            _moduleBase = process.MainModule?.BaseAddress.ToInt64() ?? 0x400000;
            _process = process;
            _handle = handle;
            return true;
        }
        catch
        {
            CloseHandle(handle);
            process.Dispose();
            throw;
        }
    }

    private ArtistSnapshot? ParseRole(byte[] data)
    {
        var id = I32(data, 0);
        var name = DecodeBig5(data.AsSpan(4, 24));
        // 78、79 是自定义艺人模板，100 是游戏内部的“千万不要用”哨兵记录。
        if (id is <= 0 or > 77 || string.IsNullOrWhiteSpace(name)) return null;

        return new ArtistSnapshot(
            id,
            name,
            I32(data, 32) == 0 ? "女" : "男",
            I32(data, 56),
            I32(data, 308),
            ArtistAbility(data, 72),
            ArtistAbility(data, 76),
            ArtistAbility(data, 80),
            ArtistAbility(data, 84),
            ArtistAbility(data, 88),
            ArtistAbility(data, 92),
            ArtistAbility(data, 96),
            ArtistAbility(data, 100),
            ArtistAbility(data, 104),
            I32(data, 108),
            I32(data, 112),
            I32(data, 136),
            I32(data, 140),
            I32(data, 144),
            I32(data, 148),
            I32(data, 152),
            null,
            null);
    }

    // The nine primary abilities are stored with one decimal place of internal
    // precision. The game UI and notice requirements both display whole points.
    private static int ArtistAbility(byte[] data, int offset) => I32(data, offset) / 10;

    private (Dictionary<int, (int Love, int GiftCount)> Roles,
        List<ProducerSnapshot> Producers) ReadRelationships()
    {
        const int elementSize = 284;
        const int maxElements = 1_024;
        var roles = new Dictionary<int, (int Love, int GiftCount)>();
        var producers = new List<ProducerSnapshot>();
        var header = ReadBytes(_moduleBase + ElementListOffset, 16);
        if (header is null) return (roles, producers);

        var listAddress = BitConverter.ToUInt32(header, 0);
        var count = BitConverter.ToInt32(header, 12);
        if (listAddress == 0 || count is <= 0 or > maxElements) return (roles, producers);

        for (var i = 0; i < count; i++)
        {
            var element = ReadBytes(listAddress + (long)i * elementSize, elementSize);
            if (element is null) continue;
            var elementId = I32(element, 0);
            var name = DecodeBig5(element.AsSpan(8, 42));
            var love = I32(element, 148);
            var roleId = I32(element, 160);
            var elementType = DecodeBig5(element.AsSpan(231, 28));
            if (elementId > 0 && !string.IsNullOrWhiteSpace(name) && IsProducerElementType(elementType))
                producers.Add(new ProducerSnapshot(elementId, name, love, elementType));
            if (roleId > 0)
                roles[roleId] = (love, I32(element, 152));
        }
        return (roles, producers);
    }

    private static bool IsProducerElementType(string value) => value is
        "Movie_Wang" or "EAMI_CD" or "SML_CD" or "I_AM_I_AD" or
        "DreamAD" or "LAMovie" or "Tokyo_CD" ||
        value.StartsWith("LCTV_", StringComparison.Ordinal) ||
        value.StartsWith("SoSaTV_", StringComparison.Ordinal);

    private CompanyOverviewSnapshot ReadCompanyOverview(
        IReadOnlyList<ArtistSnapshot> roles,
        IReadOnlyList<ProducerSnapshot> producers,
        (IReadOnlyList<NoticeSnapshot> Current, IReadOnlyList<NoticeSnapshot> Recruiting) notices)
    {
        var moneyBytes = ReadBytes(_moduleBase + MoneyOffset, 8);
        var money = moneyBytes is null ? 0 : BitConverter.ToInt64(moneyBytes, 0);
        return new CompanyOverviewSnapshot(
            "翱翔天际",
            money,
            ReadPlayerCompanyFame(),
            roles.Count(item => item.IsSigned),
            notices.Current.Count,
            notices.Recruiting.Count,
            producers.Count,
            ReadOwnedItems());
    }

    private int ReadPlayerCompanyFame()
    {
        var header = ReadBytes(_moduleBase + CompanyListOffset, 8);
        if (header is null) return 0;
        var listAddress = BitConverter.ToUInt32(header, 0);
        var count = BitConverter.ToInt32(header, 4);
        if (listAddress == 0 || count is <= 0 or > MaxCompanies) return 0;

        for (var i = 0; i < count; i++)
        {
            var data = ReadBytes(listAddress + (long)i * CompanySize, CompanySize);
            if (data is null) continue;
            var name = DecodeBig5(data.AsSpan(4, 24));
            if (name is "翱翔天際" or "翱翔天际") return I32(data, 76);
        }
        return 0;
    }

    private IReadOnlyList<CompanyItemSnapshot> ReadOwnedItems()
    {
        var header = ReadBytes(_moduleBase + ItemListOffset, 8);
        if (header is null) return [];

        var listAddress = BitConverter.ToUInt32(header, 0);
        var count = BitConverter.ToInt32(header, 4);
        if (listAddress == 0 || count is <= 0 or > MaxItems) return [];

        var result = new List<CompanyItemSnapshot>();
        for (var i = 0; i < count; i++)
        {
            var data = ReadBytes(listAddress + (long)i * ItemSize, ItemSize);
            if (data is null) continue;
            var id = I32(data, 0);
            var itemType = I32(data, 4);
            var name = DecodeBig5(data.AsSpan(8, 24));
            var ownedCount = I32(data, 44);
            if (id <= 0 || ownedCount <= 0 || string.IsNullOrWhiteSpace(name)) continue;

            result.Add(new CompanyItemSnapshot(
                id,
                name,
                ItemTypeName(itemType),
                ownedCount,
                DecodeBig5(data.AsSpan(236, 64))));
        }

        result.Sort((left, right) =>
        {
            var type = string.Compare(left.Type, right.Type, StringComparison.CurrentCulture);
            return type != 0 ? type : left.Id.CompareTo(right.Id);
        });
        return result;
    }

    private static string ItemTypeName(int itemType) => itemType switch
    {
        1 => "公司",
        2 => "普通",
        3 => "事件",
        _ => "未知"
    };

    private (IReadOnlyList<NoticeSnapshot> Current, IReadOnlyList<NoticeSnapshot> Recruiting) ReadNotices(
        IReadOnlyDictionary<int, string> roleNames,
        IReadOnlySet<int> signedRoleIds,
        IReadOnlyList<ProducerSnapshot> producers)
    {
        var current = new List<NoticeSnapshot>();
        var recruiting = new List<NoticeSnapshot>();
        var seenCurrent = new HashSet<uint>();
        var seenRecruiting = new HashSet<uint>();

        var studioHeader = ReadBytes(_moduleBase + StudioListOffset, 4);
        if (studioHeader is not null)
        {
            var studioList = BitConverter.ToUInt32(studioHeader, 0);
            for (var i = 0; studioList != 0 && i < StudioCount; i++)
            {
                var studio = ReadBytes(studioList + (long)i * StudioSize, StudioSize);
                if (studio is null) continue;
                var studioName = DecodeBig5(studio.AsSpan(4, 32));
                var studioType = I32(studio, 36);
                var actionAddress = BitConverter.ToUInt32(studio, 40);
                if (actionAddress == 0 || !seenRecruiting.Add(actionAddress)) continue;
                var notice = ReadNotice(actionAddress, studioName, studioType, roleNames, producers);
                if (notice is not null && notice.Roles.Any(x => x.AssignedRoleId == 0))
                    recruiting.Add(notice with { State = "招募中" });
            }
        }

        var countBytes = ReadBytes(_moduleBase + ActionCountOffset, 4);
        var count = countBytes is null ? 0 : BitConverter.ToInt32(countBytes, 0);
        if (count is > 0 and <= MaxActions)
        {
            for (var i = 0; i < count; i++)
            {
                var actionAddress = ReadActionPointer(i);
                if (actionAddress == 0 || !seenCurrent.Add(actionAddress)) continue;
                var notice = ReadNotice(actionAddress, null, null, roleNames, producers);
                if (notice is not null && IsCurrentNoticeState(notice.RawState) &&
                    notice.Roles.Any(x => signedRoleIds.Contains(x.AssignedRoleId)))
                    current.Add(notice);
            }
        }

        current.Sort((a, b) => CompareNoticeDates(a, b));
        recruiting.Sort((a, b) => string.Compare(a.Studio, b.Studio, StringComparison.CurrentCulture));
        return (current, recruiting);
    }

    private uint ReadActionPointer(int index)
    {
        var list = ReadBytes(_moduleBase + ActionListOffset, 16);
        if (list is null) return 0;

        var first = BitConverter.ToUInt32(list, 0);
        var cursor = BitConverter.ToUInt32(list, 8) + (uint)(index * 4);
        var map = BitConverter.ToUInt32(list, 12);
        var relative = ((long)cursor - first) / 4;
        var block = relative < 0 ? -((1023 - relative) >> 10) : relative >> 10;
        long slotAddress;
        if (block == 0)
        {
            slotAddress = cursor;
        }
        else
        {
            var blockBytes = ReadBytes(map + block * 4, 4);
            if (blockBytes is null) return 0;
            slotAddress = BitConverter.ToUInt32(blockBytes, 0) + (relative - (block << 10)) * 4;
        }

        var pointer = ReadBytes(slotAddress, 4);
        return pointer is null ? 0 : BitConverter.ToUInt32(pointer, 0);
    }

    private NoticeSnapshot? ReadNotice(
        uint address,
        string? studioName,
        int? studioType,
        IReadOnlyDictionary<int, string> roleNames,
        IReadOnlyList<ProducerSnapshot> producers)
    {
        const int actionReadSize = 1_160;
        var data = ReadBytes(address, actionReadSize);
        if (data is null) return null;

        var id = DecodeBig5(data.AsSpan(0, 10));
        var name = DecodeBig5(data.AsSpan(30, 40));
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;

        var actionType = I32(data, 868);
        var rawState = I32(data, 872);
        var producerId = I32(data, 92);
        var category = CategoryName(studioType ?? actionType);
        var resolvedStudio = string.IsNullOrWhiteSpace(studioName)
            ? DecodeBig5(data.AsSpan(1132, 28))
            : studioName;
        var producer = ResolveProducer(resolvedStudio, category, producers);
        var requests = new List<NoticeRoleSnapshot>(4);
        for (var i = 0; i < 4; i++)
        {
            var requestOffset = 104 + i * 192;
            var roleName = DecodeBig5(data.AsSpan(requestOffset + 10, 10));
            var difficulty = I32(data, requestOffset + 20);
            if (difficulty == 0 || string.IsNullOrWhiteSpace(roleName) || roleName == "無") continue;

            var assignedRoleId = I32(data, 936 + i * 24);
            var requirements = ReadRequirements(data, requestOffset);
            requests.Add(new NoticeRoleSnapshot(
                i + 1,
                roleName,
                difficulty,
                assignedRoleId,
                assignedRoleId > 0 && roleNames.TryGetValue(assignedRoleId, out var assignedName) ? assignedName : null,
                requirements,
                ReadPointerText(data, requestOffset + 184)));
        }

        return new NoticeSnapshot(
            id,
            name,
            ReadPointerText(data, 96),
            category,
            DecodeBig5(data.AsSpan(70, 22)),
            actionType is 1 or 5 ? null : DecodeBig5(data.AsSpan(10, 20)),
            resolvedStudio,
            producerId,
            producer?.Name,
            producer?.Favorability,
            rawState,
            StateName(rawState),
            ReadDate(data, 1028),
            ReadDate(data, 1044),
            ReadDate(data, 1060),
            requests);
    }

    private string? ReadPointerText(byte[] owner, int pointerOffset)
    {
        var address = BitConverter.ToUInt32(owner, pointerOffset);
        if (address == 0) return null;
        var data = ReadBytes(address, 2_048);
        if (data is null) return null;
        var text = DecodeBig5(data);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static ProducerSnapshot? ResolveProducer(
        string? studio,
        string category,
        IReadOnlyList<ProducerSnapshot> producers)
    {
        var elementType = studio switch
        {
            "全球製片" => "Movie_Wang",
            "永振電視" => category == "主持" ? "LCTV_Show" : "LCTV_Drama",
            "SoSa電視" => category == "主持" ? "SoSaTV_Show" : "SoSaTV_Drama",
            "EAMI音樂" => "EAMI_CD",
            "日月光音樂" => "SML_CD",
            "創意廣告" => "I_AM_I_AD",
            "追夢廣告" => "DreamAD",
            "京風唱片" => "Tokyo_CD",
            "大宇宙影業" => "LAMovie",
            _ => null
        };
        return elementType is null
            ? null
            : producers.FirstOrDefault(x => x.ElementType == elementType);
    }

    private static IReadOnlyList<string> ReadRequirements(byte[] data, int requestOffset)
    {
        string[] names = ["演技", "口才", "歌艺", "名气", "仪态", "动感", "体能", "才智", "自信"];
        var result = new List<string>();
        for (var i = 0; i < names.Length; i++)
        {
            var value = I32(data, requestOffset + 28 + i * 4);
            if (value > 0) result.Add($"{names[i]}≥{value}");
        }

        string[] secondary = ["叛逆", "性感", "亲和", "人气", "曝光"];
        for (var i = 0; i < secondary.Length; i++)
        {
            var value = I32(data, requestOffset + 64 + i * 4);
            if (value > 0) result.Add($"{secondary[i]}≥{value}");
        }
        return result;
    }

    private static string? ReadDate(byte[] data, int offset)
    {
        var year = I32(data, offset);
        var month = I32(data, offset + 4);
        var day = I32(data, offset + 8);
        return year is >= 2006 and <= 2008 && month is >= 1 and <= 12 && day is >= 1 and <= 31
            ? $"{year:0000}-{month:00}-{day:00}"
            : null;
    }

    private static bool IsCurrentNoticeState(int state) => state is
        1 or 2 or 3 or 4 or 11 or 13 or 14 or 21 or 22 or 23 or 24 or 30 or 31 or 51 or 52 or 53 or 54 or 61;

    private static int CompareNoticeDates(NoticeSnapshot a, NoticeSnapshot b) =>
        string.Compare(a.ProgressDate ?? a.StartDate ?? "9999", b.ProgressDate ?? b.StartDate ?? "9999", StringComparison.Ordinal);

    private static string StateName(int state) => state switch
    {
        0 => "招募中",
        1 or 30 => "已接洽",
        2 or 11 or 21 or 31 or 51 or 61 => "进行中",
        3 or 4 or 13 or 14 or 22 or 23 or 24 or 52 or 53 or 54 => "待结算",
        5 or 15 or 25 or 32 or 55 or 65 => "已完成",
        _ => $"状态 {state}"
    };

    private static string CategoryName(int type) => type switch
    {
        2 => "电视剧", 3 => "电影", 4 => "唱片", 5 => "主持",
        6 => "舞台剧", 7 => "走秀", _ => "广告"
    };

    private byte[]? ReadBytes(long address, int length)
    {
        var buffer = new byte[length];
        return ReadProcessMemory(_handle, (nint)address, buffer, length, out var read) && read.ToInt64() == length
            ? buffer
            : null;
    }

    private int[]? ReadInt32Array(long address, int count)
    {
        var bytes = ReadBytes(address, count * 4);
        if (bytes is null) return null;
        var values = new int[count];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static int I32(byte[] data, int offset) => BitConverter.ToInt32(data, offset);

    private static string DecodeBig5(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        return Encoding.GetEncoding(950).GetString(bytes).Trim();
    }

    private static bool IsValidDate(int[] value) =>
        value.Length == 4 && value[0] is >= 2006 and <= 2008 && value[1] is >= 1 and <= 12 &&
        value[2] is >= 1 and <= 31 && value[3] is >= 1 and <= 7;

    private static string WeekName(int week) => week switch
    {
        1 => "星期一", 2 => "星期二", 3 => "星期三", 4 => "星期四",
        5 => "星期五", 6 => "星期六", 7 => "星期日", _ => ""
    };

    private void Disconnect()
    {
        if (_handle != 0) CloseHandle(_handle);
        _handle = 0;
        _moduleBase = 0;
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        lock (_gate) Disconnect();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, int size, out nint bytesRead);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed record GameSnapshot(
    bool Connected,
    bool Loaded,
    int? ProcessId,
    string? Message,
    string? Date,
    string? Weekday,
    DateTimeOffset ReadAt,
    IReadOnlyList<ArtistSnapshot> Artists,
    IReadOnlyList<ProducerSnapshot> Producers,
    CompanyOverviewSnapshot Company,
    NoticeRotationSnapshot NoticeRotation,
    FlySkyWeekSnapshot WeeklyEvents,
    IReadOnlyList<NoticeSnapshot> CurrentNotices,
    IReadOnlyList<NoticeSnapshot> RecruitingNotices)
{
    public static GameSnapshot Disconnected(string message) =>
        new(false, false, null, message, null, null, DateTimeOffset.Now, [], [], CompanyOverviewSnapshot.Empty, NoticeRotationSnapshot.Empty, FlySkyWeekSnapshot.Empty, [], []);

    public static GameSnapshot ConnectedWaiting(int processId, string message) =>
        new(true, false, processId, message, null, null, DateTimeOffset.Now, [], [], CompanyOverviewSnapshot.Empty, NoticeRotationSnapshot.Empty, FlySkyWeekSnapshot.Empty, [], []);
}

internal sealed record ProducerSnapshot(int Id, string Name, int Favorability, string ElementType);

internal sealed record CompanyOverviewSnapshot(
    string Name,
    long Money,
    int Fame,
    int SignedArtistCount,
    int CurrentNoticeCount,
    int RecruitingNoticeCount,
    int ProducerCount,
    IReadOnlyList<CompanyItemSnapshot> Items)
{
    public static CompanyOverviewSnapshot Empty => new("翱翔天际", 0, 0, 0, 0, 0, 0, []);
}

internal sealed record CompanyItemSnapshot(int Id, string Name, string Type, int Count, string? Effect);

internal sealed record ArtistSnapshot(
    int Id, string Name, string Sex, int Company, int Success,
    int Acting, int Eloquence, int Singing, int Fame, int Demeanor,
    int Dynamics, int Stamina, int Intelligence, int Confidence,
    int Stress, int Fatigue, int Rebellious, int Sexy, int Affinity,
    int Popularity, int Exposure, int? Favorability, int? GiftCount)
{
    public bool IsSigned => Company == 1;
}

internal sealed record NoticeSnapshot(
    string Id,
    string Name,
    string? Description,
    string Category,
    string Subtype,
    string? ClassName,
    string? Studio,
    int Producer,
    string? ProducerName,
    int? ProducerFavorability,
    int RawState,
    string State,
    string? StartDate,
    string? ProgressDate,
    string? EndDate,
    IReadOnlyList<NoticeRoleSnapshot> Roles);

internal sealed record NoticeRoleSnapshot(
    int Slot,
    string Role,
    int Difficulty,
    int AssignedRoleId,
    string? AssignedArtist,
    IReadOnlyList<string> Requirements,
    string? Description);
