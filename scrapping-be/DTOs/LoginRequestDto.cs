namespace scrapping_be.DTOs;

public sealed record LoginRequestDto(
    string LoginUrl,
    string Username,
    string Password
);
