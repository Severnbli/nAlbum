using Microsoft.EntityFrameworkCore;

namespace nAlbum.Source;

public class AlbumDb : DbContext
{
    public AlbumDb(DbContextOptions<AlbumDb> options) : base(options)
    {
    }

    public DbSet<Album> Albums => Set<Album>();
    public DbSet<MediaItem> Media => Set<MediaItem>();
    public DbSet<AlbumAccess> Access => Set<AlbumAccess>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Album>(e =>
        {
            e.Property(x => x.Title).IsRequired().HasMaxLength(100);
            e.HasIndex(x => new { x.OwnerId, x.Id });
            e.HasIndex(x => x.AccessCode).IsUnique(); // SQLite allows many NULLs in a unique index
        });

        b.Entity<MediaItem>(e =>
        {
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.FileId).IsRequired();
            e.Property(x => x.FileUniqueId).IsRequired();
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AlbumId, x.FileUniqueId }).IsUnique(); // no duplicates inside an album
            e.HasIndex(x => new { x.AlbumId, x.Id });
        });

        b.Entity<AlbumAccess>(e =>
        {
            e.HasKey(x => new { x.AlbumId, x.UserId });
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });
    }
}