namespace scrapping_be.Models;

public enum CrawlerSessionStatus
{
    Idle,
    LoggedIn,
    Running,
    Failed
}

public record CrawlerStatusResponse(CrawlerSessionStatus Status, string? Message = null);
