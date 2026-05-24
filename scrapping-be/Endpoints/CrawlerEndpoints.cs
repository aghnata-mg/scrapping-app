using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using scrapping_be.DTOs;
using scrapping_be.Models;
using scrapping_be.Services;

namespace scrapping_be.Endpoints;

public static class CrawlerEndpoints
{
    public static IEndpointRouteBuilder MapCrawlerEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/crawler")
            .WithTags("Crawler");

        // POST /api/crawler/login
        group.MapPost("/login",
            async Task<Results<Ok<MessageResponse>, ProblemHttpResult>>(
                LoginRequestDto request,
                CrawlerService crawlerService,
                ILogger<CrawlerService> logger) =>
            {
                try
                {
                    var success = await crawlerService.LoginAsync(request);

                    return success
                        ? TypedResults.Ok(new MessageResponse("Login successful"))
                        : TypedResults.Problem(new ProblemDetails
                        {
                            Title = "Authentication Failed",
                            Detail = "Login failed. Verify credentials or handle CAPTCHA.",
                            Status = StatusCodes.Status401Unauthorized
                        });
                }
                catch (TimeoutException ex)
                {
                    logger.LogError(ex, "Timeout during login");
                    return TypedResults.Problem(new ProblemDetails
                    {
                        Title = "Network Timeout",
                        Detail = "The target site did not respond in time.",
                        Status = StatusCodes.Status504GatewayTimeout
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Login endpoint error");
                    return TypedResults.Problem(new ProblemDetails
                    {
                        Title = "Crawler Error",
                        Detail = ex.Message,
                        Status = StatusCodes.Status500InternalServerError
                    });
                }
            })
            .WithName("Login")
            .WithSummary("Trigger a headless-browser login on the target site")
            .Produces<MessageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        // POST /api/crawler/search
        group.MapPost("/search",
            async Task<Results<Ok<PagedResult<ListingDto>>, ProblemHttpResult>>(
                SearchRequestDto request,
                CrawlerService crawlerService,
                ILogger<CrawlerService> logger) =>
            {
                try
                {
                    var result = await crawlerService.SearchAsync(request);
                    return TypedResults.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return TypedResults.Problem(new ProblemDetails
                    {
                        Title = "Session Error",
                        Detail = ex.Message,
                        Status = StatusCodes.Status400BadRequest
                    });
                }
                catch (TimeoutException ex)
                {
                    logger.LogError(ex, "Timeout during search");
                    return TypedResults.Problem(new ProblemDetails
                    {
                        Title = "Network Timeout",
                        Detail = "The target site did not respond in time.",
                        Status = StatusCodes.Status504GatewayTimeout
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Search endpoint error");
                    return TypedResults.Problem(new ProblemDetails
                    {
                        Title = "Crawler Error",
                        Detail = ex.Message,
                        Status = StatusCodes.Status500InternalServerError
                    });
                }
            })
            .WithName("Search")
            .WithSummary("Run a filtered search on the target site and return scraped listings")
            .Produces<PagedResult<ListingDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        // GET /api/crawler/status
        group.MapGet("/status",
            (SessionManager sessionManager) =>
                TypedResults.Ok(new CrawlerStatusResponse(
                    sessionManager.Status,
                    sessionManager.StatusMessage)))
            .WithName("GetStatus")
            .WithSummary("Return the current crawler session status")
            .Produces<CrawlerStatusResponse>(StatusCodes.Status200OK);

        // DELETE /api/crawler/session
        group.MapDelete("/session",
            async Task<Ok<MessageResponse>>(SessionManager sessionManager) =>
            {
                await sessionManager.ClearSessionAsync();
                return TypedResults.Ok(new MessageResponse("Session cleared"));
            })
            .WithName("ClearSession")
            .WithSummary("Terminate and clear the current browser session")
            .Produces<MessageResponse>(StatusCodes.Status200OK);

        return routes;
    }
}
