using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using scrapping_be.DTOs;
using scrapping_be.Models;

namespace scrapping_be.DTOs;

[JsonSerializable(typeof(LoginRequestDto))]
[JsonSerializable(typeof(SearchRequestDto))]
[JsonSerializable(typeof(ListingDto))]
[JsonSerializable(typeof(List<ListingDto>))]
[JsonSerializable(typeof(IReadOnlyList<ListingDto>))]
[JsonSerializable(typeof(PagedResult<ListingDto>))]
[JsonSerializable(typeof(CrawlerStatusResponse))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(List<CookieData>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
public partial class AppJsonSerializerContext : JsonSerializerContext;
