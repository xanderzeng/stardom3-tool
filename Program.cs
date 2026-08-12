using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Stardom3Assistant;

internal static class Program
{
    private const string Url = "http://127.0.0.1:36733/";

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var reader = new GameMemoryReader();
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls(Url);
        var app = builder.Build();

        app.MapGet("/api/state", () => Results.Json(reader.ReadSnapshot(), JsonOptions.Default));
        app.MapGet("/api/events", () => Results.Json(FlySkyEventCatalog.All, JsonOptions.Default));
        app.MapGet("/", () => Results.Content(DashboardHtml.Value, "text/html; charset=utf-8"));
        app.MapGet("/index.html", () => Results.Content(DashboardHtml.Value, "text/html; charset=utf-8"));

        Console.WriteLine("明星志愿3 · 辅助助手（只读模式）");
        Console.WriteLine($"仪表盘：{Url}");
        Console.WriteLine("关闭此窗口即可退出。\n");

        if (!args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
            catch { }
        }

        await app.RunAsync();
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
