namespace scrapping_be.DTOs;

public sealed record SearchRequestDto(
    string? City = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    int? Bedrooms = null,
    string? PropertyType = null,
    DateOnly? AvailableFrom = null,
    int Page = 1,
    int PageSize = 20
);
