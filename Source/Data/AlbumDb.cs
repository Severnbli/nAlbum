/*
    nAlbum – telegram bot for sharing photos and videos in albums
    Copyright (C) 2026 Usevalad Buben usevaladbuben@gmail.com

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
    or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using Microsoft.EntityFrameworkCore;

namespace nAlbum.Data;

public class AlbumDb : DbContext
{
    public AlbumDb(DbContextOptions<AlbumDb> options) : base(options)
    {
    }

    public DbSet<Album> Albums => Set<Album>();
    public DbSet<MediaItem> Media => Set<MediaItem>();
    public DbSet<AlbumAccess> Access => Set<AlbumAccess>();
    public DbSet<InviteLink> InviteLinks => Set<InviteLink>();
    public DbSet<BotUser> BotUsers => Set<BotUser>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<AlbumViewStat> AlbumViewStats => Set<AlbumViewStat>();
    public DbSet<Counter> Counters => Set<Counter>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Album>(e =>
        {
            e.Property(x => x.Title).IsRequired().HasMaxLength(100);
            e.Property(x => x.NextNumber).HasDefaultValue(1);
            e.HasIndex(x => new { x.OwnerId, x.Id });
        });

        b.Entity<MediaItem>(e =>
        {
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.FileId).IsRequired();
            e.Property(x => x.FileUniqueId).IsRequired();
            e.Property(x => x.Number).HasDefaultValue(0);
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AlbumId, x.FileUniqueId }).IsUnique(); // no duplicates inside an album
            e.HasIndex(x => new { x.AlbumId, x.Id });
            e.HasIndex(x => new { x.AlbumId, x.Number }).IsUnique();
        });

        b.Entity<BotUser>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
        });

        b.Entity<UserPreference>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
            e.Property(x => x.LanguageCode).HasMaxLength(16);
        });

        b.Entity<AlbumViewStat>(e =>
        {
            e.HasKey(x => new { x.AlbumId, x.UserId });
            e.Property(x => x.Views).HasDefaultValue(0);
            e.Property(x => x.RandomViews).HasDefaultValue(0);
            e.Property(x => x.Pages).HasDefaultValue(0);
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Counter>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Value).HasDefaultValue(0);
        });

        b.Entity<AlbumAccess>(e =>
        {
            e.HasKey(x => new { x.AlbumId, x.UserId });
            e.Property(x => x.Role).HasConversion<int>().HasDefaultValue(AlbumRole.Member);
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<InviteLink>(e =>
        {
            e.Property(x => x.Code).IsRequired();
            e.Property(x => x.Uses).HasDefaultValue(0);
            e.HasOne<Album>().WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.AlbumId);
        });
    }
}
