using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Stardom3Assistant;

internal static class Program
{
    private const string Url = "http://127.0.0.1:36733/";

    [STAThread]
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
        {
            RunHeadless().GetAwaiter().GetResult();
            return;
        }

        using var reader = new GameMemoryReader();
        using var app = CreateApp(reader);

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"程序服务启动失败。\n\n{ex.Message}", "明星志愿3 · 辅助助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            using var window = new AssistantWindow(Url);
            Application.Run(window);
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static async Task RunHeadless()
    {
        using var reader = new GameMemoryReader();
        await using var app = CreateApp(reader);
        await app.RunAsync();
    }

    private static WebApplication CreateApp(GameMemoryReader reader)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(Url);
        var app = builder.Build();

        app.MapGet("/api/state", () => Results.Json(reader.ReadSnapshot(), JsonOptions.Default));
        app.MapGet("/api/events", () => Results.Json(FlySkyEventCatalog.All, JsonOptions.Default));
        app.MapGet("/", () => Results.Content(DashboardHtml.Value, "text/html; charset=utf-8"));
        app.MapGet("/index.html", () => Results.Content(DashboardHtml.Value, "text/html; charset=utf-8"));

        return app;
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
