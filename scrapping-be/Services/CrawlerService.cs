using System.Globalization;
using System.Text.Json;
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
                sessionManager.BaseUrl = new Uri(request.LoginUrl).GetLeftPart(UriPartial.Authority);
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

        if (string.IsNullOrWhiteSpace(request.LocationId))
            throw new InvalidOperationException(
                "LocationId is required. Provide the ikwilhuren.nu location identifier, " +
                "e.g. \"wpl-a59063a2f1466c7f0dce9e0f0420e477\".");

        sessionManager.SetStatus(CrawlerSessionStatus.Running, "Fetching listings…");

        try
        {
            var context = await sessionManager.GetOrCreateContextAsync();
            var page = await context.NewPageAsync();

            try
            {
                // Navigate to the listing page first so the fetch has the correct origin + cookies
                await page.GotoAsync("https://ikwilhuren.nu/aanbod/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = DefaultTimeoutMs
                });

                // Build application/x-www-form-urlencoded body
                var formParts = new List<string>
                {
                    $"locatieid={Uri.EscapeDataString(request.LocationId)}"
                };
                if (request.MinPrice.HasValue)
                    formParts.Add($"huurprijs_van={request.MinPrice.Value.ToString(CultureInfo.InvariantCulture)}");
                if (request.MaxPrice.HasValue)
                    formParts.Add($"huurprijs_tot={request.MaxPrice.Value.ToString(CultureInfo.InvariantCulture)}");
                if (request.Bedrooms.HasValue)
                    formParts.Add($"slaapkamers={request.Bedrooms.Value}");
                if (!string.IsNullOrWhiteSpace(request.Adres))
                    formParts.Add($"selAdres={Uri.EscapeDataString(request.Adres)}");
                if (request.Afstand.HasValue)
                    formParts.Add($"selAfstand={request.Afstand.Value}");
                formParts.Add($"pagina={request.Page}");

                var formBody = string.Join("&", formParts);

                // Run the POST from within the page — cookies are included automatically
                var jsonText = await page.EvaluateAsync<string>(
                    """
                    async ([url, body]) => {
                        const res = await fetch(url, {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                            body: body
                        });
                        return await res.text();
                    }
                    """,
                    new[] { "https://ikwilhuren.nu/aanbod/geo/adres/", formBody });

                if (string.IsNullOrWhiteSpace(jsonText))
                    throw new InvalidOperationException("Search API returned an empty response.");

                logger.LogDebug("Search API raw response ({Len} chars): {Preview}",
                    jsonText.Length, jsonText[..Math.Min(500, jsonText.Length)]);

                var listings = ParseApiListings(jsonText);
                await listingService.SaveListingsAsync(listings);

                var paged = listings
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .Select(ToDto)
                    .ToList();

                sessionManager.SetStatus(
                    CrawlerSessionStatus.LoggedIn,
                    $"Found {listings.Count} listings");

                return new PagedResult<ListingDto>(paged, listings.Count, request.Page, request.PageSize);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (TimeoutException ex)
        {
            sessionManager.SetStatus(CrawlerSessionStatus.Failed, "Timed out while fetching listings.");
            logger.LogError(ex, "Timeout during search API call");
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

    // CSS selectors retained for the HTML fallback parser (ParseListingsFromHtmlAsync)
    private const string ListingContainerSelector =
        ".object-list .object, .woning-card, article, " +
        "[class*='woning'], [class*='property'], [class*='listing']";

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

    // ── API response parser ───────────────────────────────────────────────────

    private List<Listing> ParseApiListings(string jsonText)
    {
        var listings = new List<Listing>();
        var now = DateTime.UtcNow;

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            // Response may be a root array or an object wrapping one
            IEnumerable<JsonElement> items;
            if (root.ValueKind == JsonValueKind.Array)
            {
                items = root.EnumerateArray().ToList();
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                string[] wrapperKeys = ["results", "items", "aanbod", "woningen", "data", "objects"];
                JsonElement inner = default;
                foreach (var key in wrapperKeys)
                    if (root.TryGetProperty(key, out inner) && inner.ValueKind == JsonValueKind.Array)
                        break;

                items = inner.ValueKind == JsonValueKind.Array
                    ? inner.EnumerateArray().ToList()
                    : [];
            }
            else
            {
                logger.LogWarning("Unexpected API response root type: {Kind}", root.ValueKind);
                return listings;
            }

            foreach (var el in items)
            {
                try
                {
                    listings.Add(new Listing
                    {
                        Title       = JStr(el, "straat", "adres", "title", "naam") ?? "Unknown",
                        Price       = JDecimal(el, "huurprijs", "prijs", "price"),
                        Location    = JStr(el, "woonplaats", "plaats", "location", "city") ?? string.Empty,
                        Bedrooms    = JInt(el, "slaapkamers", "kamers", "bedrooms"),
                        PropertyType = JStr(el, "soort", "type", "woningtype", "propertyType") ?? "Unknown",
                        ListingUrl  = JStr(el, "url", "detailUrl", "href", "link") ?? string.Empty,
                        ThumbnailUrl = JStr(el, "foto", "image", "thumbnail", "afbeelding"),
                        ScrapedAt   = now
                    });
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Skipping unparseable listing element");
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse search API JSON response");
        }

        logger.LogInformation("Parsed {Count} listings from API response", listings.Count);
        return listings;
    }

    private static string? JStr(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        return null;
    }

    private static decimal JDecimal(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!el.TryGetProperty(k, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
            if (p.ValueKind == JsonValueKind.String) return ParseDecimalFromText(p.GetString() ?? "0");
        }
        return 0m;
    }

    private static int? JInt(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!el.TryGetProperty(k, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var si)) return si;
        }
        return null;
    }

    // ── HTML parsing (AngleSharp fallback) ────────────────────────────────────

    private static async Task<List<Listing>> ParseListingsFromHtmlAsync(string html)
    {
        var config = AngleSharp.Configuration.Default;
        var browsingContext = BrowsingContext.New(config);
        var document = await browsingContext.OpenAsync(req => req.Content(html));

        var containers = document.QuerySelectorAll(ListingContainerSelector);

        var now = DateTime.UtcNow;
        var listings = new List<Listing>();

        foreach (var el in containers)
        {
            try
            {
                var title =
                    el.QuerySelector(".object-title, .title, h2, h3, [data-testid='title']")?.TextContent?.Trim()
                    ?? el.QuerySelector("a")?.TextContent?.Trim()
                    ?? "Unknown";

                var priceText =
                    el.QuerySelector(".object-price, .price, [data-testid='price'], .rent, .amount")?.TextContent?.Trim()
                    ?? "0";
                var price = ParseDecimalFromText(priceText);

                var location =
                    el.QuerySelector(".object-location, .location, address, [data-testid='location'], .area")?.TextContent?.Trim()
                    ?? string.Empty;

                var bedroomsText =
                    el.QuerySelector(".object-bedrooms, .bedrooms, [data-testid='bedrooms'], .rooms, [class*='slaapkamer']")?.TextContent?.Trim();
                int? bedrooms = null;
                if (bedroomsText is not null)
                {
                    var digits = System.Text.RegularExpressions.Regex.Match(bedroomsText, @"\d+");
                    if (digits.Success && int.TryParse(digits.Value, out var b)) bedrooms = b;
                }

                var propertyType =
                    el.QuerySelector(".object-type, .property-type, [data-testid='type'], .type-label")?.TextContent?.Trim()
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
