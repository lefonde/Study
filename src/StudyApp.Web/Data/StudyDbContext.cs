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
    public DbSet<MaterialExtract> MaterialExtracts => Set<MaterialExtract>();
    public DbSet<CardSuggestion> CardSuggestions => Set<CardSuggestion>();
    public DbSet<GenerationJob> GenerationJobs => Set<GenerationJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Course>().HasQueryFilter(c => !c.IsDeleted);
        modelBuilder.Entity<Deck>().HasQueryFilter(d => !d.IsDeleted);
        modelBuilder.Entity<Card>().HasQueryFilter(c => !c.IsDeleted);
        modelBuilder.Entity<CourseUnit>().HasQueryFilter(u => !u.IsDeleted);
        modelBuilder.Entity<Material>().HasQueryFilter(m => !m.IsDeleted);
        modelBuilder.Entity<MaterialExtract>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CardSuggestion>().HasQueryFilter(s => !s.IsDeleted);
        modelBuilder.Entity<GenerationJob>().HasQueryFilter(j => !j.IsDeleted);

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

        // One extract per material, deleted with it — an extract without its source file is
        // meaningless, and re-ingesting is the way to get a new one.
        modelBuilder.Entity<MaterialExtract>()
            .HasOne(e => e.Material)
            .WithOne(m => m.Extract)
            .HasForeignKey<MaterialExtract>(e => e.MaterialId)
            .OnDelete(DeleteBehavior.Cascade);

        // Sections/terms/topics are read and rewritten as a whole, never queried field-by-field,
        // so JSON columns keep them together instead of spreading them over side tables.
        modelBuilder.Entity<MaterialExtract>().OwnsMany(e => e.Sections, b => b.ToJson());
        modelBuilder.Entity<MaterialExtract>().OwnsMany(e => e.Terms, b => b.ToJson());
        modelBuilder.Entity<MaterialExtract>().PrimitiveCollection(e => e.Topics);

        modelBuilder.Entity<CardSuggestion>()
            .HasOne(s => s.Course)
            .WithMany()
            .HasForeignKey(s => s.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        // Provenance, not ownership — losing the unit or the source file must not delete a
        // pending suggestion the user is still reviewing.
        modelBuilder.Entity<CardSuggestion>()
            .HasOne(s => s.Unit)
            .WithMany()
            .HasForeignKey(s => s.UnitId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CardSuggestion>()
            .HasOne(s => s.SourceMaterial)
            .WithMany()
            .HasForeignKey(s => s.SourceMaterialId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<GenerationJob>()
            .HasOne(j => j.Course)
            .WithMany()
            .HasForeignKey(j => j.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GenerationJob>()
            .HasOne(j => j.Material)
            .WithMany()
            .HasForeignKey(j => j.MaterialId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Card>().HasIndex(c => new { c.DeckId, c.Due });
        modelBuilder.Entity<ReviewLog>().HasIndex(r => r.ReviewedAt);
        modelBuilder.Entity<CourseUnit>().HasIndex(u => new { u.CourseId, u.ParentId, u.Order });
        modelBuilder.Entity<Material>().HasIndex(m => new { m.CourseId, m.Kind });
        modelBuilder.Entity<Material>().HasIndex(m => m.DueDate);
        // The inbox always reads "pending for this course", and batches are acted on together.
        modelBuilder.Entity<CardSuggestion>().HasIndex(s => new { s.CourseId, s.Status });
        modelBuilder.Entity<CardSuggestion>().HasIndex(s => s.BatchId);
        modelBuilder.Entity<GenerationJob>().HasIndex(j => new { j.CourseId, j.Status });
    }
}
