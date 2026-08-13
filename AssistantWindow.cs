using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Stardom3Assistant;

internal sealed class AssistantWindow : Form
{
    private readonly string _url;
    private WebView2? _webView;
    private readonly Panel _loadingPanel;
    private readonly Label _loadingText;
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;

    public AssistantWindow(string url)
    {
        _url = url;
        _settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stardom3Assistant");
        _settingsPath = Path.Combine(_settingsDirectory, "window.json");

        Text = "明星志愿3 · 辅助助手";
        BackColor = Color.FromArgb(11, 13, 18);
        ForeColor = Color.FromArgb(238, 240, 246);
        StartPosition = FormStartPosition.CenterScreen;
        _loadingText = new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular),
            ForeColor = Color.FromArgb(184, 190, 208),
            Text = "正在启动明星志愿3辅助助手…"
        };
        _loadingPanel = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
        _loadingPanel.Controls.Add(_loadingText);

        Controls.Add(_loadingPanel);
        _loadingPanel.BringToFront();

        Shown += async (_, _) =>
        {
            MinimumSize = new Size(980, 680);
            ClientSize = new Size(1440, 900);
            RestoreWindowSettings();
            await InitializeWebViewAsync();
        };
        FormClosing += (_, _) => SaveWindowSettings();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        CenterLoadingText();
    }

    private async Task InitializeWebViewAsync()
    {
        CenterLoadingText();
        try
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                BackColor = BackColor,
                Visible = false
            };
            Controls.Add(_webView);
            _loadingPanel.BringToFront();

            var userDataFolder = Path.Combine(_settingsDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;

            _webView.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            _webView.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var message = JsonDocument.Parse(e.WebMessageAsJson);
                    if (message.RootElement.TryGetProperty("action", out var action) && action.GetString() == "toggleTopmost")
                    {
                        TopMost = !TopMost;
                        SendTopMostState();
                    }
                }
                catch { }
            };
            _webView.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    _loadingPanel.Visible = false;
                    _webView.Visible = true;
                    _webView.Focus();
                    SendTopMostState();
                }
                else
                {
                    ShowStartupError("界面载入失败，请关闭程序后重试。");
                }
            };
            _webView.CoreWebView2.Navigate(_url);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowStartupError("系统缺少 Microsoft Edge WebView2 Runtime。请安装后重新启动本工具。");
        }
        catch (Exception ex)
        {
            ShowStartupError($"程序窗口初始化失败：{ex.Message}");
        }
    }

    private void SendTopMostState()
    {
        _webView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { action = "topmostChanged", value = TopMost }));
    }

    private void ShowStartupError(string message)
    {
        if (_webView is not null) _webView.Visible = false;
        _loadingText.Text = message;
        _loadingText.ForeColor = Color.FromArgb(239, 127, 163);
        _loadingPanel.Visible = true;
        _loadingPanel.BringToFront();
        CenterLoadingText();
    }

    private void CenterLoadingText()
    {
        _loadingText.Location = new Point(
            Math.Max(24, (_loadingPanel.ClientSize.Width - _loadingText.Width) / 2),
            Math.Max(24, (_loadingPanel.ClientSize.Height - _loadingText.Height) / 2));
    }

    private void RestoreWindowSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(_settingsPath));
            if (settings is null || settings.Width < MinimumSize.Width || settings.Height < MinimumSize.Height) return;
            var bounds = new Rectangle(settings.X, settings.Y, settings.Width, settings.Height);
            if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = settings.TopMost;
            if (settings.Maximized) WindowState = FormWindowState.Maximized;
        }
        catch { }
    }

    private void SaveWindowSettings()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var settings = new WindowSettings(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized, TopMost);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
        }
        catch { }
    }

    private sealed record WindowSettings(int X, int Y, int Width, int Height, bool Maximized, bool TopMost = false);
}
