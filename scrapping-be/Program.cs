using Microsoft.EntityFrameworkCore;
using scrapping_be.BackgroundJobs;
using scrapping_be.Data;
using scrapping_be.DTOs;
using scrapping_be.Endpoints;
using scrapping_be.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=listings.db"));

// ── Crawler services (singleton — own Playwright browser lifetime) ─────────────
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<CrawlerService>();
builder.Services.AddSingleton<ListingService>();

// ── Background job ────────────────────────────────────────────────────────────
builder.Services.AddHostedService<CrawlerBackgroundService>();

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddPolicy("FrontendPolicy", policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()));

// ── OpenAPI 3.1 (built-in, no Swashbuckle) ────────────────────────────────────
builder.Services.AddOpenApi();

// ── ProblemDetails (RFC 9457) ─────────────────────────────────────────────────
builder.Services.AddProblemDetails();

// ── System.Text.Json source generation ───────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Apply pending EF Core migrations on startup ───────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
    app.MapOpenApi(); // Available at /openapi/v1.json

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors("FrontendPolicy");
app.UseHttpsRedirection();

// ── Routes ────────────────────────────────────────────────────────────────────
app.MapCrawlerEndpoints();
app.MapListingsEndpoints();

app.Run();

