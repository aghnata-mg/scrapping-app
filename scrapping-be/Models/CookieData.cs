namespace scrapping_be.Models;

/// <summary>Holds persisted cookie fields for session restoration.</summary>
public sealed record CookieData(
    string Name,
    string Value,
    string? Domain,
    string? Path,
    float? Expires,
    bool? HttpOnly,
    bool? Secure
);
