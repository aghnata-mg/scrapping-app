using System.Text.Json;
using Microsoft.Playwright;
using scrapping_be.DTOs;
using scrapping_be.Models;

namespace scrapping_be.Services;

/// <summary>
/// Singleton that owns the Playwright browser lifetime and session state.
/// Thread-safe via an async semaphore around context creation/teardown.
/// </summary>
public sealed class SessionManager(
    ILogger<SessionManager> logger,
    IConfiguration configuration) : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _browserContext;

    private CrawlerSessionStatus _status = CrawlerSessionStatus.Idle;
    private string? _statusMessage;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _cookiesPath =
        configuration["Crawler:CookiesPath"] ?? "session_cookies.json";

    // ── Public state ─────────────────────────────────────────────────────────

    public CrawlerSessionStatus Status => _status;
    public string? StatusMessage => _statusMessage;

    public void SetStatus(CrawlerSessionStatus status, string? message = null)
    {
        _status = status;
        _statusMessage = message;
    }

    // ── Context access ────────────────────────────────────────────────────────

    /// <summary>Returns the existing context or creates a new one.</summary>
    public async Task<IBrowserContext> GetOrCreateContextAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_browserContext is not null)
                return _browserContext;

            // Install Chromium on first run (no-op if already installed)
            Microsoft.Playwright.Program.Main(["install", "chromium"]);

            _playwright ??= await Playwright.CreateAsync();
            _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-setuid-sandbox"]
            });

            _browserContext = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                            "AppleWebKit/537.36 (KHTML, like Gecko) " +
                            "Chrome/124.0.0.0 Safari/537.36"
            });

            await TryRestoreCookiesAsync(_browserContext);
            return _browserContext;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Cookie persistence ────────────────────────────────────────────────────

    public async Task PersistCookiesAsync()
    {
        if (_browserContext is null) return;

        try
        {
            var cookies = await _browserContext.CookiesAsync();
            var cookieData = cookies
                .Select(c => new CookieData(c.Name, c.Value, c.Domain, c.Path, c.Expires, c.HttpOnly, c.Secure))
                .ToList();

            var json = JsonSerializer.Serialize(cookieData,
                AppJsonSerializerContext.Default.ListCookieData);

            await File.WriteAllTextAsync(_cookiesPath, json);
            logger.LogInformation("Persisted {Count} cookies to {Path}", cookieData.Count, _cookiesPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist cookies");
        }
    }

    private async Task TryRestoreCookiesAsync(IBrowserContext context)
    {
        if (!File.Exists(_cookiesPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(_cookiesPath);
            var cookieData = JsonSerializer.Deserialize(json,
                AppJsonSerializerContext.Default.ListCookieData);

            if (cookieData is not { Count: > 0 }) return;

            var cookies = cookieData
                .Where(c => c.Domain is not null)
                .Select(c => new Cookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path ?? "/",
                    Expires = c.Expires,
                    HttpOnly = c.HttpOnly,
                    Secure = c.Secure
                })
                .ToList();

            await context.AddCookiesAsync(cookies);
            _status = CrawlerSessionStatus.LoggedIn;
            logger.LogInformation("Restored {Count} cookies — session marked LoggedIn", cookies.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore cookies from {Path}", _cookiesPath);
        }
    }

    // ── Session teardown ──────────────────────────────────────────────────────

    public async Task ClearSessionAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_browserContext is not null)
            {
                await _browserContext.ClearCookiesAsync();
                await _browserContext.DisposeAsync();
                _browserContext = null;
            }

            if (File.Exists(_cookiesPath))
                File.Delete(_cookiesPath);

            _status = CrawlerSessionStatus.Idle;
            _statusMessage = null;
            logger.LogInformation("Browser session cleared");
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_browserContext is not null)
            await _browserContext.DisposeAsync();

        if (_browser is not null)
            await _browser.DisposeAsync();

        _playwright?.Dispose();
        _lock.Dispose();
    }
}
