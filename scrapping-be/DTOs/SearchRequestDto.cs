namespace scrapping_be.DTOs;

public sealed record SearchRequestDto(
    /// <summary>
    /// ikwilhuren.nu location identifier, e.g. "wpl-a59063a2f1466c7f0dce9e0f0420e477".
    /// Obtain from the site's location picker.
    /// </summary>
    string? LocationId = null,
    string? City = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    int? Bedrooms = null,
    string? PropertyType = null,
    DateOnly? AvailableFrom = null,
    int Page = 1,
    int PageSize = 20
);
