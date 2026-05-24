using Microsoft.EntityFrameworkCore;
using scrapping_be.Data;
using scrapping_be.DTOs;
using scrapping_be.Models;

namespace scrapping_be.Services;

/// <summary>
/// Handles all SQLite persistence operations for <see cref="Listing"/> entities.
/// Singleton — uses <see cref="IDbContextFactory{AppDbContext}"/> so each
/// operation creates and disposes its own short-lived DbContext.
/// </summary>
public sealed class ListingService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<ListingService> logger)
{
    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<PagedResult<ListingDto>> GetListingsAsync(
        string? city = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        int? bedrooms = null,
        string? propertyType = null,
        int page = 1,
        int pageSize = 20)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var query = db.Listings.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(l => l.Location.Contains(city));

        if (minPrice.HasValue)
            query = query.Where(l => l.Price >= minPrice.Value);

        if (maxPrice.HasValue)
            query = query.Where(l => l.Price <= maxPrice.Value);

        if (bedrooms.HasValue)
            query = query.Where(l => l.Bedrooms == bedrooms.Value);

        if (!string.IsNullOrWhiteSpace(propertyType))
            query = query.Where(l => l.PropertyType == propertyType);

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(l => l.ScrapedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new ListingDto(
                l.Id, l.Title, l.Price, l.Location,
                l.Bedrooms, l.PropertyType, l.AvailableFrom,
                l.ListingUrl, l.ThumbnailUrl, l.ScrapedAt))
            .ToListAsync();

        return new PagedResult<ListingDto>(items, total, page, pageSize);
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists new listings, skipping any whose <c>ListingUrl</c> already exists.
    /// </summary>
    public async Task SaveListingsAsync(IEnumerable<Listing> listings)
    {
        var incoming = listings.ToList();
        if (incoming.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync();

        var incomingUrls = incoming.Select(l => l.ListingUrl).ToList();

        var existingUrls = await db.Listings
            .Where(l => incomingUrls.Contains(l.ListingUrl))
            .Select(l => l.ListingUrl)
            .ToHashSetAsync();

        var newListings = incoming.Where(l => !existingUrls.Contains(l.ListingUrl)).ToList();

        if (newListings.Count == 0) return;

        db.Listings.AddRange(newListings);
        await db.SaveChangesAsync();
        logger.LogInformation("Saved {Count} new listings to the database", newListings.Count);
    }
}
