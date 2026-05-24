using AngleSharp;
using AngleSharp.Html.Dom;
using Microsoft.Playwright;
using scrapping_be.DTOs;
using scrapping_be.Models;

namespace scrapping_be.Services;

/// <summary>
/// Handles headless-browser login and scraped-search operations.
/// Singleton — reuses the shared <see cref="SessionManager"/> browser context.
/// </summary>
public sealed class CrawlerService(
    SessionManager sessionManager,
    ListingService listingService,
    ILogger<CrawlerService> logger)
{
    private const int DefaultTimeoutMs = 30_000;

    // ── Login ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Navigates to <paramref name="request.LoginUrl"/>, fills credentials,
    /// submits, and persists session cookies on success.
    /// </summary>
    /// <returns><c>true</c> on success; <c>false</c> when credentials are wrong or CAPTCHA detected.</returns>
    public async Task<bool> LoginAsync(LoginRequestDto request)
    {
        sessionManager.SetStatus(CrawlerSessionStatus.Running, "Logging in…");

        try
        {
            var context = await sessionManager.GetOrCreateContextAsync();
            var page = await context.NewPageAsync();

            try
            {
                // DOMContentLoaded is reliable; NetworkIdle times out on sites with
                // analytics beacons, websockets, or background polling.
                await page.GotoAsync(request.LoginUrl, new PageGotoOptions
                {
                    Timeout = DefaultTimeoutMs,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });

                // Give JS a moment to render dynamic login forms
                await page.WaitForLoadStateAsync(LoadState.Load,
                    new PageWaitForLoadStateOptions { Timeout = DefaultTimeoutMs });

                await FillLoginFormAsync(page, request.Username, request.Password);

                // After submit, some sites redirect (URL changes); others use AJAX (no URL change).
                // Try URL-change first; fall back to waiting for network to settle.
                string loginPath = new Uri(request.LoginUrl).AbsolutePath;
                try
                {
                    await page.WaitForURLAsync(
                        url => !url.Contains(loginPath, StringComparison.OrdinalIgnoreCase),
                        new PageWaitForURLOptions { Timeout = DefaultTimeoutMs });
                }
                catch (TimeoutException)
                {
                    // No URL change — AJAX-based login. Wait briefly for network to settle.
                    logger.LogDebug("URL did not change after submit; waiting for network idle (AJAX login).");
                    try
                    {
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                            new PageWaitForLoadStateOptions { Timeout = 10_000 });
                    }
                    catch (TimeoutException)
                    {
                        // Still busy — proceed and let DetectLoginFailure determine outcome.
                        logger.LogDebug("Network did not go idle; checking page content directly.");
                    }
                }

                var pageContent = await page.ContentAsync();
                if (DetectLoginFailure(page.Url, pageContent))
                {
                    sessionManager.SetStatus(
                        CrawlerSessionStatus.Failed,
                        "Login failed: invalid credentials or CAPTCHA detected.");
                    return false;
                }

                await sessionManager.PersistCookiesAsync();
                sessionManager.SetStatus(CrawlerSessionStatus.LoggedIn, "Authenticated");
                logger.LogInformation("Logged in to {Url}", request.LoginUrl);
                return true;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (TimeoutException ex)
        {
            sessionManager.SetStatus(CrawlerSessionStatus.Failed, "Timed out during login.");
            logger.LogError(ex, "Timeout during login to {Url}", request.LoginUrl);
            throw;
        }
        catch (PlaywrightException ex)
        {
            sessionManager.SetStatus(CrawlerSessionStatus.Failed, ex.Message);
            logger.LogError(ex, "Playwright error during login to {Url}", request.LoginUrl);
            throw;
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Navigates to the target site with applied filters, scrapes listings,
    /// caches them in SQLite, and returns a paged DTO result.
    /// </summary>
    public async Task<PagedResult<ListingDto>> SearchAsync(SearchRequestDto request)
    {
        if (sessionManager.Status is CrawlerSessionStatus.Idle)
            throw new InvalidOperationException(
                "No active session. Call POST /api/crawler/login first.");

        sessionManager.SetStatus(CrawlerSessionStatus.Running, "Scraping listings…");

        try
        {
            var context = await sessionManager.GetOrCreateContextAsync();
            var page = await context.NewPageAsync();

            try
            {
                var searchUrl = BuildSearchUrl(request);
                await page.GotoAsync(searchUrl, new PageGotoOptions
                {
                    Timeout = DefaultTimeoutMs,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });

                await ApplyPageFiltersAsync(page, request);

                // Wait for at least one listing container
                await page.WaitForSelectorAsync(
                    ListingContainerSelector,
                    new PageWaitForSelectorOptions
                    {
                        Timeout = DefaultTimeoutMs,
                        State = WaitForSelectorState.Attached
                    });

                var html = await page.ContentAsync();
                var listings = await ParseListingsFromHtmlAsync(html);

                await listingService.SaveListingsAsync(listings);

                var page_ = request.Page;
                var pageSize = request.PageSize;
                var paged = listings
                    .Skip((page_ - 1) * pageSize)
                    .Take(pageSize)
                    .Select(ToDto)
                    .ToList();

                sessionManager.SetStatus(
                    CrawlerSessionStatus.LoggedIn,
                    $"Scraped {listings.Count} listings");

                return new PagedResult<ListingDto>(paged, listings.Count, page_, pageSize);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (TimeoutException ex)
        {
            sessionManager.SetStatus(CrawlerSessionStatus.Failed, "Timed out while scraping.");
            logger.LogError(ex, "Timeout during search");
            throw;
        }
        catch (PlaywrightException ex)
        {
            sessionManager.SetStatus(CrawlerSessionStatus.Failed, ex.Message);
            logger.LogError(ex, "Playwright error during search");
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sessionManager.SetStatus(CrawlerSessionStatus.Failed, ex.Message);
            logger.LogError(ex, "Unexpected error during search");
            throw;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    // Common selector tried in order — site-specific ones take priority
    private const string ListingContainerSelector =
        "[data-listing], .listing-item, article.property, .property-card, " +
        ".rental-listing, li.result, .search-result";

    private static async Task FillLoginFormAsync(IPage page, string username, string password)
    {
        string[] usernameSelectors =
        [
            "input[name='username']", "input[name='email']",
            "input[type='email']", "input[id='username']",
            "input[id='email']", "#username", "#email",
            "input[placeholder*='email' i]", "input[placeholder*='username' i]"
        ];

        string[] passwordSelectors =
        [
            "input[name='password']", "input[type='password']",
            "input[id='password']", "#password"
        ];

        string[] submitSelectors =
        [
            "button[type='submit']", "input[type='submit']",
            "button:has-text('Login')", "button:has-text('Sign in')",
            "button:has-text('Log in')", ".login-button",
            "#login-button", "[data-testid='login-button']"
        ];

        foreach (var selector in usernameSelectors)
        {
            var el = await page.QuerySelectorAsync(selector);
            if (el is null) continue;
            await el.FillAsync(username);
            break;
        }

        foreach (var selector in passwordSelectors)
        {
            var el = await page.QuerySelectorAsync(selector);
            if (el is null) continue;
            await el.FillAsync(password);
            break;
        }

        foreach (var selector in submitSelectors)
        {
            var el = await page.QuerySelectorAsync(selector);
            if (el is null) continue;
            await el.ClickAsync();
            return;
        }

        // Fallback: submit via Enter key
        await page.Keyboard.PressAsync("Enter");
    }

    private static bool DetectLoginFailure(string currentUrl, string pageContent)
    {
        if (pageContent.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
            pageContent.Contains("recaptcha", StringComparison.OrdinalIgnoreCase))
            return true;

        string[] errorPhrases =
        [
            "invalid credentials", "wrong password", "incorrect password",
            "login failed", "authentication failed", "invalid username",
            "account not found", "sign in failed", "incorrect email"
        ];

        return errorPhrases.Any(phrase =>
            pageContent.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a generic search URL. In a real integration this would be
    /// site-specific; swap this method for the actual target URL pattern.
    /// </summary>
    private static string BuildSearchUrl(SearchRequestDto req)
    {
        var qs = new List<string>();

        if (!string.IsNullOrWhiteSpace(req.City))
            qs.Add($"city={Uri.EscapeDataString(req.City)}");
        if (req.MinPrice.HasValue) qs.Add($"price_min={req.MinPrice}");
        if (req.MaxPrice.HasValue) qs.Add($"price_max={req.MaxPrice}");
        if (req.Bedrooms.HasValue) qs.Add($"bedrooms={req.Bedrooms}");
        if (!string.IsNullOrWhiteSpace(req.PropertyType))
            qs.Add($"type={Uri.EscapeDataString(req.PropertyType)}");
        if (req.AvailableFrom.HasValue)
            qs.Add($"available_from={req.AvailableFrom.Value:yyyy-MM-dd}");
        qs.Add($"page={req.Page}");

        return "/search?" + string.Join("&", qs);
    }

    private static async Task ApplyPageFiltersAsync(IPage page, SearchRequestDto req)
    {
        if (req.MinPrice.HasValue)
        {
            var el = await page.QuerySelectorAsync(
                "input[name='min_price'], input[id='min-price'], #minPrice");
            if (el is not null)
                await el.FillAsync(req.MinPrice.Value.ToString());
        }

        if (req.MaxPrice.HasValue)
        {
            var el = await page.QuerySelectorAsync(
                "input[name='max_price'], input[id='max-price'], #maxPrice");
            if (el is not null)
                await el.FillAsync(req.MaxPrice.Value.ToString());
        }
    }

    // ── HTML parsing (AngleSharp fallback) ────────────────────────────────────

    private static async Task<List<Listing>> ParseListingsFromHtmlAsync(string html)
    {
        var config = AngleSharp.Configuration.Default;
        var browsingContext = BrowsingContext.New(config);
        var document = await browsingContext.OpenAsync(req => req.Content(html));

        var containers = document.QuerySelectorAll(
            "[data-listing], .listing-item, article.property, .property-card, " +
            ".rental-listing, li.result, .search-result");

        var now = DateTime.UtcNow;
        var listings = new List<Listing>();

        foreach (var el in containers)
        {
            try
            {
                var title =
                    el.QuerySelector(".title, h2, h3, [data-testid='title']")?.TextContent?.Trim()
                    ?? el.QuerySelector("a")?.TextContent?.Trim()
                    ?? "Unknown";

                var priceText =
                    el.QuerySelector(".price, [data-testid='price'], .rent, .amount")?.TextContent?.Trim()
                    ?? "0";
                var price = ParseDecimalFromText(priceText);

                var location =
                    el.QuerySelector(".location, address, [data-testid='location'], .area")?.TextContent?.Trim()
                    ?? string.Empty;

                var bedroomsText =
                    el.QuerySelector(".bedrooms, [data-testid='bedrooms'], .rooms")?.TextContent?.Trim();
                int? bedrooms = null;
                if (bedroomsText is not null)
                {
                    var digits = System.Text.RegularExpressions.Regex.Match(bedroomsText, @"\d+");
                    if (digits.Success && int.TryParse(digits.Value, out var b)) bedrooms = b;
                }

                var propertyType =
                    el.QuerySelector(".property-type, [data-testid='type'], .type-label")?.TextContent?.Trim()
                    ?? "Unknown";

                var linkEl = el.QuerySelector("a[href]") as IHtmlAnchorElement;
                var listingUrl = linkEl?.Href ?? string.Empty;

                var imgEl = el.QuerySelector("img");
                var thumbnail = imgEl?.GetAttribute("src") ?? imgEl?.GetAttribute("data-src");

                listings.Add(new Listing
                {
                    Title = title,
                    Price = price,
                    Location = location,
                    Bedrooms = bedrooms,
                    PropertyType = propertyType,
                    ListingUrl = listingUrl,
                    ThumbnailUrl = thumbnail,
                    ScrapedAt = now
                });
            }
            catch (Exception)
            {
                // Skip malformed listing elements
            }
        }

        return listings;
    }

    private static decimal ParseDecimalFromText(string text)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(text, @"[^\d.]", string.Empty);
        return decimal.TryParse(clean, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    }

    private static ListingDto ToDto(Listing l) => new(
        l.Id, l.Title, l.Price, l.Location,
        l.Bedrooms, l.PropertyType, l.AvailableFrom,
        l.ListingUrl, l.ThumbnailUrl, l.ScrapedAt);
}
