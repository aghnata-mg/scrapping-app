using Microsoft.AspNetCore.Http.HttpResults;
using scrapping_be.DTOs;
using scrapping_be.Services;

namespace scrapping_be.Endpoints;

public static class ListingsEndpoints
{
    public static IEndpointRouteBuilder MapListingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/listings")
            .WithTags("Listings");

        // GET /api/listings
        group.MapGet("/",
            async Task<Ok<PagedResult<ListingDto>>>(
                ListingService listingService,
                string? city = null,
                decimal? minPrice = null,
                decimal? maxPrice = null,
                int? bedrooms = null,
                string? propertyType = null,
                int page = 1,
                int pageSize = 20) =>
            {
                var result = await listingService.GetListingsAsync(
                    city, minPrice, maxPrice, bedrooms, propertyType, page, pageSize);

                return TypedResults.Ok(result);
            })
            .WithName("GetListings")
            .WithSummary("Return cached listings from the SQLite database with optional filters")
            .Produces<PagedResult<ListingDto>>(StatusCodes.Status200OK);

        return routes;
    }
}
