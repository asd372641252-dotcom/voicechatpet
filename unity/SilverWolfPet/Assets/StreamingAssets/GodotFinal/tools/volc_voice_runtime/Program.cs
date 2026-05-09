using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VolcVoiceRuntime;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new VoiceRuntimeForm(ParseOptions(args)));
    }

    private static RuntimeOptions ParseOptions(string[] args)
    {
        RuntimeOptions options = new RuntimeOptions();
        int index = Array.IndexOf(args, "--url");
        if (index >= 0 && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            options.Url = args[index + 1];
        }

        options.Hidden = Array.IndexOf(args, "--hidden") >= 0 && Array.IndexOf(args, "--visible") < 0;
        return options;
    }
}

internal sealed class RuntimeOptions
{
    public string Url { get; set; } = "http://127.0.0.1:17862/?autostart=1";
    public bool Hidden { get; set; }
}

internal sealed class VoiceRuntimeForm : Form
{
    private const string AdditionalBrowserArguments =
        "--autoplay-policy=no-user-gesture-required " +
        "--use-fake-ui-for-media-stream " +
        "--enable-usermedia-screen-capturing " +
        "--allow-http-screen-capture " +
        "--auto-select-desktop-capture-source=\"Entire screen\" " +
        "--video-capture-use-gpu-memory-buffer";

    private readonly string _url;
    private readonly bool _hidden;
    private readonly WebView2 _webView;
    private readonly Label _title;
    private readonly Label _status;
    private readonly string _logPath;
    private bool _closingAfterStop;

    protected override bool ShowWithoutActivation => _hidden || base.ShowWithoutActivation;

    public VoiceRuntimeForm(RuntimeOptions options)
    {
        _url = options.Url;
        _hidden = options.Hidden;
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "voicechatpet",
            "voice_runtime.log");
        Text = "voicechatpet Voice";
        StartPosition = _hidden ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        Location = _hidden ? new Point(-10000, -10000) : Point.Empty;
        Size = new Size(640, 620);
        MinimumSize = new Size(520, 420);
        BackColor = Color.FromArgb(32, 22, 38);
        ForeColor = Color.FromArgb(248, 232, 243);
        ShowInTaskbar = false;
        Opacity = _hidden ? 0.01 : 1.0;
        FormBorderStyle = _hidden ? FormBorderStyle.FixedToolWindow : FormBorderStyle.Sizable;

        _title = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Text = "voicechatpet Voice Runtime",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 183, 223),
        };
        Controls.Add(_title);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Text = "Loading RTC runtime...",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 9),
            ForeColor = Color.FromArgb(235, 212, 228),
        };
        Controls.Add(_status);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = true,
        };
        Controls.Add(_webView);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        WriteLog("shown hidden=" + _hidden.ToString() + " url=" + _url);
        if (!_hidden)
        {
            WindowState = FormWindowState.Normal;
            TopMost = true;
            Activate();
            BringToFront();
            TopMost = false;
        }
        await InitializeWebViewAsync();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closingAfterStop)
        {
            e.Cancel = true;
            _status.Text = "Stopping voice session...";
        }
        try
        {
            if (_webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync("window.silverWolfVoiceRuntimeStop && window.silverWolfVoiceRuntimeStop();");
            }
        }
        catch
        {
            // Shutdown should never block the pet.
        }
        if (!_closingAfterStop)
        {
            _closingAfterStop = true;
            BeginInvoke(new Action(Close));
            return;
        }
        base.OnFormClosing(e);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = !_hidden;
            _webView.CoreWebView2.PermissionRequested += OnPermissionRequested;
            _webView.CoreWebView2.ScreenCaptureStarting += OnScreenCaptureStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.ProcessFailed += OnProcessFailed;
            WriteLog("webview initialized");
            _webView.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            _status.Text = "WebView2 failed: " + ex.Message;
            WriteLog("webview failed: " + ex);
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (
            e.PermissionKind == CoreWebView2PermissionKind.Microphone ||
            e.PermissionKind == CoreWebView2PermissionKind.Camera ||
            e.PermissionKind == CoreWebView2PermissionKind.Autoplay
        )
        {
            e.State = CoreWebView2PermissionState.Allow;
            e.SavesInProfile = false;
            _status.Text = $"{e.PermissionKind} permission allowed.";
            WriteLog("permission allowed: " + e.PermissionKind.ToString());
            return;
        }
        e.State = CoreWebView2PermissionState.Default;
        WriteLog("permission default: " + e.PermissionKind.ToString());
    }

    private void OnScreenCaptureStarting(object? sender, CoreWebView2ScreenCaptureStartingEventArgs e)
    {
        e.Cancel = false;
        _status.Text = "Screen picker opened. Choose a window or screen.";
        WriteLog("screen capture starting");
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        WriteLog("navigation completed success=" + e.IsSuccess.ToString() + " status=" + e.WebErrorStatus.ToString());
        if (!e.IsSuccess)
        {
            _status.Text = "Voice page failed: " + e.WebErrorStatus;
            return;
        }

        _status.Text = "Voice page loaded. Starting...";
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync("window.silverWolfVoiceRuntimeStart && window.silverWolfVoiceRuntimeStart();");
            WriteLog("runtime start script invoked");
        }
        catch (Exception ex)
        {
            _status.Text = "Auto start failed: " + ex.Message;
            WriteLog("auto start failed: " + ex);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string message = e.TryGetWebMessageAsString();
            _status.Text = message;
            WriteLog("web: " + message);
        }
        catch
        {
            _status.Text = "Voice runtime message received.";
            WriteLog("web message received");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        WriteLog("webview process failed: " + e.ProcessFailedKind.ToString() + " reason=" + e.Reason.ToString());
    }

    private void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath) ?? ".");
            File.AppendAllText(
                _logPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must not interrupt the runtime window.
        }
    }
}
