using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;

namespace StudyApp.Web.Data;

public class StudyDbContext(DbContextOptions<StudyDbContext> options) : DbContext(options)
{
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<ReviewLog> ReviewLogs => Set<ReviewLog>();
    public DbSet<CourseUnit> CourseUnits => Set<CourseUnit>();
    public DbSet<Material> Materials => Set<Material>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Course>().HasQueryFilter(c => !c.IsDeleted);
        modelBuilder.Entity<Deck>().HasQueryFilter(d => !d.IsDeleted);
        modelBuilder.Entity<Card>().HasQueryFilter(c => !c.IsDeleted);
        modelBuilder.Entity<CourseUnit>().HasQueryFilter(u => !u.IsDeleted);
        modelBuilder.Entity<Material>().HasQueryFilter(m => !m.IsDeleted);

        modelBuilder.Entity<Deck>()
            .HasOne(d => d.Course)
            .WithMany(c => c.Decks)
            .HasForeignKey(d => d.CourseId);

        modelBuilder.Entity<Card>()
            .HasOne(c => c.Deck)
            .WithMany(d => d.Cards)
            .HasForeignKey(c => c.DeckId);

        // ReviewLogs are kept even for soft-deleted cards (history), hence no query filter.
        modelBuilder.Entity<ReviewLog>()
            .HasOne(r => r.Card)
            .WithMany()
            .HasForeignKey(r => r.CardId);

        modelBuilder.Entity<CourseUnit>()
            .HasOne(u => u.Course)
            .WithMany(c => c.Units)
            .HasForeignKey(u => u.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-referencing tree; Restrict avoids ambiguous multi-path cascade deletes
        // (units are soft-deleted in practice, never hard-removed as a subtree).
        modelBuilder.Entity<CourseUnit>()
            .HasOne(u => u.Parent)
            .WithMany(u => u.Children)
            .HasForeignKey(u => u.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Course>()
            .HasOne(c => c.CurrentUnit)
            .WithMany()
            .HasForeignKey(c => c.CurrentUnitId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Material>()
            .HasOne(m => m.Course)
            .WithMany(c => c.Materials)
            .HasForeignKey(m => m.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        // SetNull (not Cascade): deleting a unit/material must not delete the card/material
        // that referenced it — it's provenance, not ownership. Ownership is via Deck/Course.
        modelBuilder.Entity<Material>()
            .HasOne(m => m.Unit)
            .WithMany()
            .HasForeignKey(m => m.UnitId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Card>()
            .HasOne(c => c.Unit)
            .WithMany()
            .HasForeignKey(c => c.UnitId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Card>()
            .HasOne(c => c.SourceMaterial)
            .WithMany()
            .HasForeignKey(c => c.SourceMaterialId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Card>().HasIndex(c => new { c.DeckId, c.Due });
        modelBuilder.Entity<ReviewLog>().HasIndex(r => r.ReviewedAt);
        modelBuilder.Entity<CourseUnit>().HasIndex(u => new { u.CourseId, u.ParentId, u.Order });
        modelBuilder.Entity<Material>().HasIndex(m => new { m.CourseId, m.Kind });
        modelBuilder.Entity<Material>().HasIndex(m => m.DueDate);
    }
}
