using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace ReVueJudge;

public sealed class ReVueJudgeMainForm : Form
{
    private const string AppTitle = "ReVue Judge";
    private const string SettingsVirtualHost = "revue-judge.local";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly WebView2 _webView;
    private readonly HttpClient _apiClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly ReplayMediaCacheServer _mediaCacheServer;

    public ReVueJudgeMainForm()
    {
        Text = AppTitle;
        Icon = AppWindowIcon.Extract() ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(900, 650);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Black
        };

        Controls.Add(_webView);
        _mediaCacheServer = new ReplayMediaCacheServer(_apiClient, ReVueJudgeConfigStore.Load);
        Load += async (_, _) => await InitializeWebViewAsync();
        FormClosed += (_, _) => _mediaCacheServer.Dispose();
    }

    private static string BuildReVueJudgeUrl(ReVueJudgeConfig config)
    {
        config = ReVueJudgeConfigStore.Normalize(config);
        var version = Uri.EscapeDataString(GetReVueJudgeVersion());
        var url = $"https://{SettingsVirtualHost}/ReVue-Judge.html?0&reVueJudge=true&version={version}";
        return IsTimerStopwatchVisible(config) ? url + "&timer=true" : url + "&timer=false";
    }

    private static string GetReVueJudgeVersion()
    {
        var assembly = typeof(ReVueJudgeMainForm).Assembly;
        var informationalVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(attribute => attribute.InformationalVersion)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        var version = assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }

    private static bool IsTimerStopwatchVisible(ReVueJudgeConfig config)
    {
        var roleUi = string.Equals(config.Role, ReVueJudgeConfigStore.RefereeRole, StringComparison.OrdinalIgnoreCase)
            ? config.RefereeUI
            : config.JudgeUI;
        return IsUiFlagEnabled(roleUi?.DisplayTimerStopwatch, true);
    }

    private static bool IsUiFlagEnabled(object? value, bool defaultValue)
    {
        if (value is bool enabled)
        {
            return enabled;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => IsUiFlagEnabled(element.GetString(), defaultValue),
                _ => defaultValue
            };
        }

        return value?.ToString()?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" or "on" => true,
            "false" or "0" or "no" or "n" or "off" => false,
            _ => defaultValue
        };
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await WebViewEnvironmentProvider.GetAsync();
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                SettingsVirtualHost,
                Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ApplyZoomFromConfig();
            _webView.ZoomFactorChanged += (_, _) => SaveCurrentZoomToConfig();
            await NavigateReVueJudgeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "WebView2 could not be initialized.\r\n\r\n" + ex.Message,
                AppTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task NavigateReVueJudgeAsync()
    {
        if (_webView.CoreWebView2 == null) return;
        var config = ReVueJudgeConfigStore.Load();
        ApplyZoomFromConfig(config);
        _webView.CoreWebView2.Navigate(BuildReVueJudgeUrl(config));
    }

    private void ApplyZoomFromConfig(ReVueJudgeConfig? config = null)
    {
        config = ReVueJudgeConfigStore.Normalize(config ?? ReVueJudgeConfigStore.Load());
        var nextZoom = config.UiZoomPercent / 100.0;
        if (Math.Abs(_webView.ZoomFactor - nextZoom) > 0.001)
        {
            _webView.ZoomFactor = nextZoom;
        }
    }

    private void SaveCurrentZoomToConfig()
    {
        try
        {
            var config = ReVueJudgeConfigStore.Load();
            config.UiZoomPercent = Math.Clamp(
                (int)Math.Round(_webView.ZoomFactor * 100.0),
                ReVueJudgeConfigStore.MinUiZoomPercent,
                ReVueJudgeConfigStore.MaxUiZoomPercent);
            ReVueJudgeConfigStore.Save(config);
        }
        catch
        {
        }
    }

    private static Uri BuildBackendUri(ReVueJudgeConfig config, string pathAndQuery)
    {
        if (!pathAndQuery.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only /api requests can be proxied.");
        }

        var separatorIndex = pathAndQuery.IndexOf('?');
        var path = separatorIndex >= 0 ? pathAndQuery[..separatorIndex] : pathAndQuery;
        var query = separatorIndex >= 0 ? pathAndQuery[(separatorIndex + 1)..] : "";

        return new UriBuilder("http", config.ServerIp, 5050)
        {
            Path = path,
            Query = query
        }.Uri;
    }

    private async Task<JsonElement> GetApiJsonAsync(string pathAndQuery)
    {
        var targetUri = BuildBackendUri(ReVueJudgeConfigStore.Load(), pathAndQuery);
        using var response = await _apiClient.GetAsync(targetUri);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private object CacheReplayMedia(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Missing replay media token.");
        }

        var safeToken = string.Concat(token.Where(char.IsLetterOrDigit));
        if (safeToken.Length == 0)
        {
            throw new InvalidOperationException("Invalid replay media token.");
        }

        return _mediaCacheServer.GetMediaInfo(safeToken);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        int id = 0;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            id = root.TryGetProperty("id", out var idProperty) ? idProperty.GetInt32() : 0;
            var action = root.TryGetProperty("action", out var actionProperty)
                ? actionProperty.GetString()
                : "";

            if (action == "loadConfig")
            {
                PostSuccess(id, ReVueJudgeConfigStore.Load());
                return;
            }

            if (action == "apiGet")
            {
                var payload = root.GetProperty("payload");
                var path = payload.TryGetProperty("path", out var pathProperty)
                    ? pathProperty.GetString()
                    : "";
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("Missing API path.");
                }

                PostSuccess(id, await GetApiJsonAsync(path));
                return;
            }

            if (action == "cacheMedia")
            {
                var payload = root.GetProperty("payload");
                var token = payload.TryGetProperty("token", out var tokenProperty)
                    ? tokenProperty.GetString()
                    : "";
                PostSuccess(id, CacheReplayMedia(token ?? ""));
                return;
            }

            if (action == "cleanupMediaCache")
            {
                PostSuccess(id, _mediaCacheServer.CleanupOldMedia(TimeSpan.FromHours(3)));
                return;
            }

            if (action == "saveConfig" || action == "saveConfigAndReload")
            {
                var payload = root.GetProperty("payload").GetRawText();
                var incoming = JsonSerializer.Deserialize<ReVueJudgeConfig>(payload, JsonOptions);
                var saved = ReVueJudgeConfigStore.Save(incoming);
                ApplyZoomFromConfig(saved);
                PostSuccess(id, saved);
                return;
            }

            PostError(id, "Unknown ReVue Judge action.");
        }
        catch (Exception ex)
        {
            PostError(id, ex.Message);
        }
    }

    private void PostSuccess(int id, object? data)
    {
        PostMessage(new { id, ok = true, data });
    }

    private void PostError(int id, string error)
    {
        PostMessage(new { id, ok = false, error });
    }

    private void PostMessage(object message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => PostMessage(message)));
            return;
        }

        var json = JsonSerializer.Serialize(message, JsonOptions);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }
}

