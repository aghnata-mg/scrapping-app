namespace scrapping_be.DTOs;

public sealed record ListingDto(
    int Id,
    string Title,
    decimal Price,
    string Location,
    int? Bedrooms,
    string PropertyType,
    DateOnly? AvailableFrom,
    string ListingUrl,
    string? ThumbnailUrl,
    DateTime ScrapedAt
);
