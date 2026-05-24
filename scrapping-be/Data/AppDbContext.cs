using Microsoft.EntityFrameworkCore;
using scrapping_be.Models;

namespace scrapping_be.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Listing> Listings => Set<Listing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Listing>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Title)
                  .IsRequired()
                  .HasMaxLength(500);

            entity.Property(e => e.Location)
                  .IsRequired()
                  .HasMaxLength(300);

            entity.Property(e => e.PropertyType)
                  .IsRequired()
                  .HasMaxLength(100);

            entity.Property(e => e.ListingUrl)
                  .IsRequired()
                  .HasMaxLength(2000);

            entity.Property(e => e.ThumbnailUrl)
                  .HasMaxLength(2000);

            entity.Property(e => e.Price)
                  .HasColumnType("TEXT"); // SQLite stores decimal as TEXT

            entity.HasIndex(e => e.ListingUrl)
                  .IsUnique();

            entity.HasIndex(e => e.ScrapedAt);
        });
    }
}
