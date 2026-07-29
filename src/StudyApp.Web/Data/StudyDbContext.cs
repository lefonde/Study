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
    public DbSet<ProgressSnapshot> ProgressSnapshots => Set<ProgressSnapshot>();
    public DbSet<CourseTopic> CourseTopics => Set<CourseTopic>();
    public DbSet<TopicSource> TopicSources => Set<TopicSource>();
    public DbSet<CourseMapRevision> CourseMapRevisions => Set<CourseMapRevision>();
    public DbSet<TopicProposal> TopicProposals => Set<TopicProposal>();
    public DbSet<CardTopic> CardTopics => Set<CardTopic>();
    public DbSet<TopicPrerequisite> TopicPrerequisites => Set<TopicPrerequisite>();
    public DbSet<SubmissionReview> SubmissionReviews => Set<SubmissionReview>();

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
        modelBuilder.Entity<CourseTopic>().HasQueryFilter(t => !t.IsDeleted);
        // A source is a claim that some material evidences a topic; if either end is soft-deleted
        // the claim no longer holds, so the row stops counting rather than lingering as evidence
        // from a file the user removed.
        modelBuilder.Entity<TopicSource>()
            .HasQueryFilter(s => !s.Topic!.IsDeleted && !s.Material!.IsDeleted);
        modelBuilder.Entity<CourseMapRevision>().HasQueryFilter(r => !r.IsDeleted);
        modelBuilder.Entity<TopicProposal>().HasQueryFilter(p => !p.IsDeleted);
        modelBuilder.Entity<SubmissionReview>()
            .HasQueryFilter(r => !r.IsDeleted && !r.Material!.IsDeleted);

        modelBuilder.Entity<Deck>()
            .HasOne(d => d.Course)
            .WithMany(c => c.Decks)
            .HasForeignKey(d => d.CourseId);

        // SetNull, matching Card.UnitId/Material.UnitId: deleting a unit must not delete the
        // deck that was filed under it, only stop counting it toward that unit's rollup.
        modelBuilder.Entity<Deck>()
            .HasOne(d => d.Unit)
            .WithMany()
            .HasForeignKey(d => d.UnitId)
            .OnDelete(DeleteBehavior.SetNull);

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

        // Read and rewritten as a whole profile, never queried per-kind, so JSON keeps it beside
        // the course — same reasoning as MaterialExtract's sections and terms below.
        modelBuilder.Entity<Course>().OwnsMany(c => c.AssessmentWeights, b => b.ToJson());

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

        // Historical record like ReviewLog — kept even if the deck is later soft-deleted, so
        // no query filter and Cascade only follows a genuine hard delete (never used today).
        modelBuilder.Entity<ProgressSnapshot>()
            .HasOne(s => s.Deck)
            .WithMany()
            .HasForeignKey(s => s.DeckId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProgressSnapshot>()
            .HasOne(s => s.Course)
            .WithMany()
            .HasForeignKey(s => s.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        // --- Course map ---

        modelBuilder.Entity<CourseTopic>()
            .HasOne(t => t.Course)
            .WithMany()
            .HasForeignKey(t => t.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        // SetNull like every other UnitId: deleting a chapter must not delete the topics filed
        // under it, only stop attributing them to it.
        modelBuilder.Entity<CourseTopic>()
            .HasOne(t => t.Unit)
            .WithMany()
            .HasForeignKey(t => t.UnitId)
            .OnDelete(DeleteBehavior.SetNull);

        // A source is meaningless without its topic, so it goes with it.
        modelBuilder.Entity<TopicSource>()
            .HasOne(s => s.Topic)
            .WithMany(t => t.Sources)
            .HasForeignKey(s => s.CourseTopicId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting a material removes the claim that it evidences a topic — but not the topic,
        // which other materials may still support.
        modelBuilder.Entity<TopicSource>()
            .HasOne(s => s.Material)
            .WithMany()
            .HasForeignKey(s => s.MaterialId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CourseMapRevision>()
            .HasOne(r => r.Course)
            .WithMany()
            .HasForeignKey(r => r.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TopicProposal>()
            .HasOne(p => p.Revision)
            .WithMany(r => r.Proposals)
            .HasForeignKey(p => p.RevisionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Provenance, not ownership: a proposal to change a topic must survive long enough to be
        // reviewed even if that topic is removed meanwhile — MapRevisionPolicy then blocks it
        // with a reason rather than the row vanishing unexplained.
        modelBuilder.Entity<TopicProposal>()
            .HasOne(p => p.Topic)
            .WithMany()
            .HasForeignKey(p => p.CourseTopicId)
            .OnDelete(DeleteBehavior.SetNull);

        // Written with the proposal, read once when applying, never queried alone — unlike the
        // TopicSource rows it becomes.
        modelBuilder.Entity<TopicProposal>().OwnsMany(p => p.Sources, b => b.ToJson());
        modelBuilder.Entity<TopicProposal>().OwnsMany(p => p.Prerequisites, b => b.ToJson());

        // Composite key, like CardTopic: an edge has no identity beyond the pair it joins, and
        // the key is what stops the same dependency being recorded twice.
        modelBuilder.Entity<TopicPrerequisite>()
            .HasKey(p => new { p.CourseTopicId, p.PrerequisiteTopicId });

        modelBuilder.Entity<TopicPrerequisite>()
            .HasOne(p => p.Topic)
            .WithMany(t => t.Prerequisites)
            .HasForeignKey(p => p.CourseTopicId)
            .OnDelete(DeleteBehavior.Cascade);

        // NoAction on this side only: both ends point at CourseTopic, and two cascade paths into
        // one table is ambiguous. Topics are never hard-deleted anyway — a merge soft-deletes the
        // absorbed topic after rewiring its edges onto the survivor — so nothing relies on it.
        modelBuilder.Entity<TopicPrerequisite>()
            .HasOne(p => p.Prerequisite)
            .WithMany()
            .HasForeignKey(p => p.PrerequisiteTopicId)
            .OnDelete(DeleteBehavior.NoAction);

        // An edge to or from a removed topic is not a dependency: it would place topics in stages
        // behind something that no longer exists.
        modelBuilder.Entity<TopicPrerequisite>()
            .HasQueryFilter(p => !p.Topic!.IsDeleted && !p.Prerequisite!.IsDeleted);

        // --- Solution reviews ---

        // A submission points at the assignment it answers. SetNull rather than Cascade: deleting
        // an assignment shouldn't take the student's own work down with it.
        modelBuilder.Entity<Material>()
            .HasOne(m => m.SubmissionFor)
            .WithMany()
            .HasForeignKey(m => m.SubmissionForId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<SubmissionReview>()
            .HasOne(r => r.Material)
            .WithMany()
            .HasForeignKey(r => r.MaterialId)
            .OnDelete(DeleteBehavior.Cascade);

        // Owned JSON, like ProposedSource: findings are written once with the review and read
        // together with it, never queried on their own.
        modelBuilder.Entity<SubmissionReview>().OwnsMany(r => r.Findings, b => b.ToJson());

        // Composite key: the pair IS the fact, so a card can never be linked to one topic twice.
        modelBuilder.Entity<CardTopic>().HasKey(ct => new { ct.CardId, ct.CourseTopicId });

        modelBuilder.Entity<CardTopic>()
            .HasOne(ct => ct.Card)
            .WithMany()
            .HasForeignKey(ct => ct.CardId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CardTopic>()
            .HasOne(ct => ct.Topic)
            .WithMany()
            .HasForeignKey(ct => ct.CourseTopicId)
            .OnDelete(DeleteBehavior.Cascade);

        // Both ends are soft-deleted in practice, and a link to a deleted card or topic is not
        // coverage — it would inflate the numbers with cards the user has already thrown away.
        modelBuilder.Entity<CardTopic>()
            .HasQueryFilter(ct => !ct.Card!.IsDeleted && !ct.Topic!.IsDeleted);

        // Provenance, like the suggestion's unit and source material: losing the topic must not
        // delete a suggestion still waiting in the inbox.
        modelBuilder.Entity<CardSuggestion>()
            .HasOne(s => s.Topic)
            .WithMany()
            .HasForeignKey(s => s.CourseTopicId)
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
        modelBuilder.Entity<Deck>().HasIndex(d => d.UnitId);
        // One capture per deck per day — the idempotency guarantee the capture job relies on.
        modelBuilder.Entity<ProgressSnapshot>().HasIndex(s => new { s.DeckId, s.CapturedOn }).IsUnique();
        modelBuilder.Entity<ProgressSnapshot>().HasIndex(s => new { s.CourseId, s.CapturedOn });
        modelBuilder.Entity<CourseTopic>().HasIndex(t => new { t.CourseId, t.Importance });
        modelBuilder.Entity<CourseTopic>().HasIndex(t => t.UnitId);
        // The reverse lookup the study plan is built on: which topics does this assignment assess?
        modelBuilder.Entity<TopicSource>().HasIndex(s => new { s.MaterialId, s.Mention });
        modelBuilder.Entity<TopicSource>().HasIndex(s => s.CourseTopicId);
        // The Map tab's first question on every load: is a revision waiting for review?
        modelBuilder.Entity<CourseMapRevision>().HasIndex(r => new { r.CourseId, r.Status });
        // Coverage counts cards per topic; the composite PK already covers the other direction.
        modelBuilder.Entity<CardTopic>().HasIndex(ct => ct.CourseTopicId);
        // "What does this topic unlock?" — the reverse of the composite PK, used when a merge
        // rewires edges and when the map draws outgoing arrows.
        modelBuilder.Entity<TopicPrerequisite>().HasIndex(p => p.PrerequisiteTopicId);
        modelBuilder.Entity<GenerationJob>().PrimitiveCollection(j => j.TargetTopicIds);
    }
}
