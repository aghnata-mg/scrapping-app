namespace scrapping_be.Models;

public sealed class Listing
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public decimal Price { get; set; }
    public required string Location { get; set; }
    public int? Bedrooms { get; set; }
    public required string PropertyType { get; set; }
    public DateOnly? AvailableFrom { get; set; }
    public required string ListingUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTime ScrapedAt { get; set; }
}
