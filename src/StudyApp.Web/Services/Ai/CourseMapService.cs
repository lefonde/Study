using System.Text;
using Microsoft.EntityFrameworkCore;
using StudyApp.Core.Entities;
using StudyApp.Core.Planning;
using StudyApp.Web.Data;

namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Stage 2b: derives and revises a course's topic map from its stored extracts.
///
/// Reads extracts, never original files — the rule that keeps re-mapping cheap enough to run
/// every time material is added, which is the entire point of a map that tracks the course
/// rather than freezing it at first upload.
///
/// Output is always a <i>pending</i> <see cref="CourseMapRevision"/>. Nothing here changes the
/// map directly: uploading a past exam can legitimately reshuffle what a course is "about", and
/// a silent reshuffle is indistinguishable from the model quietly getting it wrong.
/// </summary>
public class CourseMapService(
    IDbContextFactory<StudyDbContext> factory,
    ClaudeService claude,
    TimeProvider timeProvider)
{
    /// <summary>
    /// How much extracted text may be sent at full fidelity. Everything else contributes a
    /// digest. Bounded so a course with a whole textbook in it stays re-mappable rather than
    /// becoming too expensive to refresh.
    /// </summary>
    private const int FullTextBudgetChars = 300_000;

    private const int MaxTermsPerMaterial = 40;
    private const int MaxSectionTitlesPerMaterial = 60;

    public async Task RunAsync(GenerationJob job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == job.CourseId, ct)
            ?? throw new InvalidOperationException("The course was deleted before mapping ran.");

        var materials = await db.Materials.AsNoTracking()
            .Include(m => m.Extract)
            .Where(m => m.CourseId == job.CourseId && m.Status == MaterialStatus.Ingested)
            .ToListAsync(ct);

        if (materials.Count == 0)
            throw new InvalidOperationException(
                "No ingested materials to map. Upload material and run ingestion first.");

        var topics = await db.CourseTopics.AsNoTracking()
            .Include(t => t.Sources)
            .Include(t => t.Prerequisites)
            .Where(t => t.CourseId == job.CourseId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var weights = AssessmentWeight.Complete(course.AssessmentWeights);
        var weightByKind = weights.ToDictionary(w => w.Kind, w => w.Weight);

        // Refs, not GUIDs: a model echoing "M3" back is reliable, whereas echoing a 36-character
        // GUID invites a single flipped character that silently detaches a proposal from its
        // evidence. Unknown refs are dropped when mapping back.
        var materialRefs = materials
            .OrderByDescending(m => weightByKind.GetValueOrDefault(m.Kind))
            .ThenBy(m => m.Title)
            .Select((m, i) => (Material: m, Ref: $"M{i + 1}"))
            .ToList();

        var topicRefs = topics.Select((t, i) => (Topic: t, Ref: $"T{i + 1}")).ToList();

        var result = await claude.MapCourseAsync(
            course.Name,
            BuildWeightProfile(weights),
            BuildCurrentTopics(topicRefs),
            BuildMaterialBundle(materialRefs, weightByKind),
            ct);

        var revision = BuildRevision(job, result.Value, materialRefs, topicRefs);

        db.CourseMapRevisions.Add(revision);
        job.InputTokens = result.InputTokens;
        job.OutputTokens = result.OutputTokens;
        job.Message = revision.Proposals.Count == 0
            ? "No changes proposed — the map already reflects this material."
            : $"{revision.Proposals.Count} proposed change(s) waiting for review.";

        db.GenerationJobs.Attach(job);
        db.Entry(job).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    private static string BuildWeightProfile(List<AssessmentWeight> weights) =>
        string.Join("\n", weights
            .OrderByDescending(w => w.Weight)
            .Select(w => $"- {w.Kind}: {w.Weight}/100"));

    private static string BuildCurrentTopics(List<(CourseTopic Topic, string Ref)> topicRefs)
    {
        if (topicRefs.Count == 0)
            return "(empty — this is the first mapping of this course)";

        var refById = topicRefs.ToDictionary(x => x.Topic.Id, x => x.Ref);

        var sb = new StringBuilder();
        foreach (var (topic, reference) in topicRefs)
        {
            // Pinned and dismissed states are stated so the model doesn't waste proposals on
            // changes that MapRevisionPolicy would refuse anyway.
            var flags = topic switch
            {
                { IsDismissed: true } => " [DISMISSED by the student — do not propose changes]",
                { ImportancePinned: true } => " [PINNED by the student — do not propose reweighting]",
                _ => "",
            };

            // Dependencies already recorded are echoed back, or every re-map would propose the
            // same links again and the review queue would fill with changes that change nothing.
            var requires = topic.Prerequisites
                .Select(p => refById.GetValueOrDefault(p.PrerequisiteTopicId))
                .Where(r => r is not null)
                .ToList();
            var requiresText = requires.Count > 0 ? $" [requires: {string.Join(", ", requires)}]" : "";

            sb.AppendLine($"- [{reference}] {topic.Name} — {topic.Importance.ToString().ToLowerInvariant()}{flags}{requiresText}");
            if (!string.IsNullOrWhiteSpace(topic.Summary))
                sb.AppendLine($"    {topic.Summary}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every material contributes a digest; the highest-weighted get their full text until the
    /// budget runs out.
    ///
    /// Weight decides who gets fidelity because importance is judged from what the course
    /// assesses: an exam's exact wording is the evidence, while a textbook chapter mainly
    /// establishes that a topic exists and what it is called — which a digest carries perfectly
    /// well.
    /// </summary>
    private static string BuildMaterialBundle(
        List<(Material Material, string Ref)> materialRefs,
        Dictionary<MaterialKind, int> weightByKind)
    {
        var remaining = FullTextBudgetChars;
        var sb = new StringBuilder();

        foreach (var (material, reference) in materialRefs)
        {
            var weight = weightByKind.GetValueOrDefault(material.Kind);
            sb.AppendLine($"### [{reference}] {material.Title}");
            sb.AppendLine($"kind: {material.Kind} (weight {weight}/100)");
            if (material.DueDate is { } due)
                sb.AppendLine($"due: {due:yyyy-MM-dd}");

            var extract = material.Extract;
            if (extract is null)
            {
                sb.AppendLine("(not ingested)");
                sb.AppendLine();
                continue;
            }

            if (extract.Topics.Count > 0)
                sb.AppendLine($"topics: {string.Join("; ", extract.Topics)}");

            if (extract.Terms.Count > 0)
                sb.AppendLine("terms: " + string.Join("; ", extract.Terms
                    .Take(MaxTermsPerMaterial)
                    .Select(t => $"{t.Term} — {t.Definition}")));

            var fullText = extract.ToMarkdown();
            if (fullText.Length <= remaining)
            {
                sb.AppendLine("full text:");
                sb.AppendLine(fullText);
                remaining -= fullText.Length;
            }
            else
            {
                sb.AppendLine("sections: " + string.Join("; ", extract.Sections
                    .Take(MaxSectionTitlesPerMaterial)
                    .Select(s => $"p.{s.Page} {s.Title}".Trim())));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Turns the model's ref-based output into proposals, discarding anything that points at a
    /// ref which wasn't in the input rather than trusting it.
    /// </summary>
    private CourseMapRevision BuildRevision(
        GenerationJob job,
        MapResult result,
        List<(Material Material, string Ref)> materialRefs,
        List<(CourseTopic Topic, string Ref)> topicRefs)
    {
        var materialByRef = materialRefs.ToDictionary(x => x.Ref, x => x.Material.Id, StringComparer.OrdinalIgnoreCase);
        var topicByRef = topicRefs.ToDictionary(x => x.Ref, x => x.Topic.Id, StringComparer.OrdinalIgnoreCase);

        var revision = new CourseMapRevision
        {
            CourseId = job.CourseId,
            JobId = job.Id,
            Status = RevisionStatus.Pending,
            Summary = result.Summary?.Trim() ?? "",
        };

        foreach (var proposed in result.Proposals ?? [])
        {
            var kind = ParseKind(proposed.Kind);
            if (kind is null || string.IsNullOrWhiteSpace(proposed.Name))
                continue;

            Guid? topicId = null;
            if (!string.IsNullOrWhiteSpace(proposed.TopicRef)
                && topicByRef.TryGetValue(proposed.TopicRef.Trim(), out var resolved))
            {
                topicId = resolved;
            }

            // Anything but an Add must name a topic that actually exists; a change aimed at a
            // ref we never sent is not something to guess at.
            if (kind != TopicChangeKind.Add && topicId is null)
                continue;

            Guid? mergeInto = null;
            if (!string.IsNullOrWhiteSpace(proposed.MergeIntoRef)
                && topicByRef.TryGetValue(proposed.MergeIntoRef.Trim(), out var survivor))
            {
                mergeInto = survivor;
            }
            if (kind == TopicChangeKind.Merge && mergeInto is null)
                continue;

            revision.Proposals.Add(new TopicProposal
            {
                Kind = kind.Value,
                CourseTopicId = topicId,
                ProposedName = ClaudeService.NormalizeMarkdown(proposed.Name).Trim(),
                ProposedSummary = ClaudeService.NormalizeMarkdown(proposed.Summary).Trim(),
                ProposedImportance = ParseImportance(proposed.Importance),
                // Normalised like the name and summary: a reason cites formulas as often as it
                // cites page numbers, and it is rendered as markdown on the map.
                Reason = ClaudeService.NormalizeMarkdown(proposed.Reason).Trim(),
                MergeIntoTopicId = mergeInto,
                Status = ProposalStatus.Pending,
                Sources = (proposed.Sources ?? [])
                    .Where(s => !string.IsNullOrWhiteSpace(s.MaterialRef)
                                && materialByRef.ContainsKey(s.MaterialRef.Trim()))
                    .Select(s => new ProposedSource
                    {
                        MaterialId = materialByRef[s.MaterialRef.Trim()],
                        SourceReference = string.IsNullOrWhiteSpace(s.SourceReference) ? null : s.SourceReference.Trim(),
                        Mention = ParseMention(s.Mention),
                    })
                    .DistinctBy(s => (s.MaterialId, s.SourceReference, s.Mention))
                    .ToList(),
                Prerequisites = ResolvePrerequisiteRefs(proposed, topicByRef),
            });
        }

        return revision;
    }

    /// <summary>
    /// Pins each prerequisite reference to whatever it can be pinned to now: an existing topic
    /// becomes an id, and anything else is kept as a name for <see cref="ResolveEdgesAsync"/> to
    /// match once the sibling proposal that creates it has been accepted.
    ///
    /// Names are kept verbatim rather than normalised, because they are matched against
    /// <c>ProposedName</c> later and both sides have to agree.
    /// </summary>
    private static List<ProposedPrerequisite> ResolvePrerequisiteRefs(
        MappedProposal proposed, Dictionary<string, Guid> topicByRef) =>
        [.. (proposed.PrerequisiteRefs ?? [])
            .Select(r => r?.Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => topicByRef.TryGetValue(r!, out var id)
                ? new ProposedPrerequisite { TopicId = id }
                : new ProposedPrerequisite { Name = r })
            .DistinctBy(p => p.TopicId?.ToString() ?? p.Name!.ToLowerInvariant())];

    private static TopicChangeKind? ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "add" => TopicChangeKind.Add,
        "reweight" => TopicChangeKind.Reweight,
        "merge" => TopicChangeKind.Merge,
        "retire" => TopicChangeKind.Retire,
        "relink" => TopicChangeKind.Relink,
        _ => null,
    };

    private static TopicImportance ParseImportance(string? importance) =>
        importance?.Trim().ToLowerInvariant() switch
        {
            "core" => TopicImportance.Core,
            "peripheral" => TopicImportance.Peripheral,
            _ => TopicImportance.Supporting,
        };

    private static TopicMention ParseMention(string? mention) => mention?.Trim().ToLowerInvariant() switch
    {
        "assessed" => TopicMention.Assessed,
        "defined" => TopicMention.Defined,
        _ => TopicMention.Referenced,
    };

    // --- Reading and applying revisions ---

    public async Task<CourseMapRevision?> GetPendingRevisionAsync(Guid courseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.CourseMapRevisions.AsNoTracking()
            .Include(r => r.Proposals.OrderBy(p => p.Kind).ThenBy(p => p.ProposedName))
            .ThenInclude(p => p.Topic)
            .Where(r => r.CourseId == courseId && r.Status == RevisionStatus.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<List<CourseTopic>> GetTopicsAsync(Guid courseId, bool includeDismissed = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.CourseTopics.AsNoTracking()
            .Include(t => t.Sources).ThenInclude(s => s.Material)
            .Include(t => t.Prerequisites)
            .Include(t => t.Unit)
            .Where(t => t.CourseId == courseId && (includeDismissed || !t.IsDismissed))
            .OrderByDescending(t => t.Importance)
            .ThenBy(t => t.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Applies one accepted proposal, or records why it can't be.
    ///
    /// Re-checks <see cref="MapRevisionPolicy"/> at apply time rather than trusting the check
    /// made when the revision was generated: a revision can sit for days while the user pins,
    /// dismisses or deletes the very topics it refers to.
    /// </summary>
    public async Task<string?> AcceptProposalAsync(Guid proposalId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var proposal = await db.TopicProposals
            .Include(p => p.Revision)
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct);
        if (proposal is null || proposal.Status != ProposalStatus.Pending)
            return null;

        var courseId = proposal.Revision!.CourseId;
        var topics = await db.CourseTopics
            .Include(t => t.Sources)
            .Include(t => t.Prerequisites)
            .Where(t => t.CourseId == courseId)
            .ToListAsync(ct);

        var verdict = MapRevisionPolicy.Evaluate(proposal, topics.ToDictionary(t => t.Id));
        if (!verdict.IsApplicable)
        {
            proposal.Status = ProposalStatus.Rejected;
            await db.SaveChangesAsync(ct);
            return verdict.BlockedReason;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        switch (proposal.Kind)
        {
            case TopicChangeKind.Add:
                var created = new CourseTopic
                {
                    CourseId = courseId,
                    Name = proposal.ProposedName,
                    Summary = proposal.ProposedSummary,
                    Importance = proposal.ProposedImportance,
                    ImportanceReason = proposal.Reason,
                    UnitId = proposal.ProposedUnitId,
                    LastChangedAt = now,
                    Sources = ToSources(proposal.Sources),
                };
                db.CourseTopics.Add(created);
                topics.Add(created);
                // Record what this proposal produced. Edges are resolved in a second pass over the
                // whole revision, and that pass needs a way to find the topic an Add created —
                // including for a sibling proposal accepted before this one.
                proposal.CourseTopicId = created.Id;
                break;

            case TopicChangeKind.Reweight:
                var target = topics.First(t => t.Id == proposal.CourseTopicId);
                if (target.Importance != proposal.ProposedImportance)
                    target.PreviousImportance = target.Importance;
                target.Importance = proposal.ProposedImportance;
                target.ImportanceReason = proposal.Reason;
                target.Summary = string.IsNullOrWhiteSpace(proposal.ProposedSummary)
                    ? target.Summary
                    : proposal.ProposedSummary;
                target.LastChangedAt = now;
                db.TopicSources.AddRange(NewSourcesFor(target, proposal.Sources));
                break;

            case TopicChangeKind.Merge:
                var absorbed = topics.First(t => t.Id == proposal.CourseTopicId);
                var survivor = topics.First(t => t.Id == proposal.MergeIntoTopicId);
                // Sources move rather than being dropped: the survivor should end up evidenced by
                // everything that evidenced either name.
                var carriedOver = absorbed.Sources
                    .Select(s => new ProposedSource
                    {
                        MaterialId = s.MaterialId,
                        SourceReference = s.SourceReference,
                        Mention = s.Mention,
                    })
                    .Concat(proposal.Sources);
                db.TopicSources.AddRange(NewSourcesFor(survivor, carriedOver));
                RewireEdgesForMerge(db, topics, absorbed.Id, survivor.Id);
                survivor.LastChangedAt = now;
                absorbed.IsDeleted = true;
                break;

            case TopicChangeKind.Retire:
                var retired = topics.First(t => t.Id == proposal.CourseTopicId);
                // Dismissed, not deleted: a retired topic that later regains support should be
                // something the user can bring back, and its history is worth keeping.
                retired.IsDismissed = true;
                retired.LastChangedAt = now;
                retired.ImportanceReason = proposal.Reason;
                break;

            case TopicChangeKind.Relink:
                // Nothing to change on the topic itself — the dependencies are written by the
                // resolution pass below, which every kind goes through.
                topics.First(t => t.Id == proposal.CourseTopicId).LastChangedAt = now;
                break;
        }

        proposal.Status = ProposalStatus.Accepted;
        await ResolveEdgesAsync(db, proposal.RevisionId, topics, ct);
        await db.SaveChangesAsync(ct);
        await CloseRevisionIfSettledAsync(db, proposal.RevisionId, ct);
        return null;
    }

    /// <summary>
    /// Writes every dependency the revision's accepted proposals ask for that can be resolved
    /// right now, and can safely be run again.
    ///
    /// It has to be a pass over the whole revision rather than work done inside each accept,
    /// because "Accept all" walks proposals ordered by kind and name: a topic's prerequisite is
    /// routinely accepted <i>after</i> the topic that needs it, and a single forward pass would
    /// silently drop that edge. Re-running after each accept picks up whatever has since become
    /// resolvable, and adds nothing twice.
    /// </summary>
    private static async Task ResolveEdgesAsync(
        StudyDbContext db, Guid revisionId, List<CourseTopic> topics, CancellationToken ct)
    {
        var accepted = await db.TopicProposals
            .Where(p => p.RevisionId == revisionId && p.Status == ProposalStatus.Accepted)
            .ToListAsync(ct);

        if (accepted.TrueForAll(p => p.Prerequisites.Count == 0))
            return;

        var live = topics.Where(t => !t.IsDeleted).ToList();
        var byId = live.Select(t => t.Id).ToHashSet();
        var byName = live
            .GroupBy(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var edges = topics
            .SelectMany(t => t.Prerequisites)
            .Select(p => new TopicEdge(p.CourseTopicId, p.PrerequisiteTopicId))
            .ToList();

        foreach (var proposal in accepted)
        {
            // Merge folds its dependencies onto the survivor; a retired topic gets none.
            var dependent = proposal.Kind switch
            {
                TopicChangeKind.Merge => proposal.MergeIntoTopicId,
                TopicChangeKind.Retire => null,
                _ => proposal.CourseTopicId,
            };

            if (dependent is not { } dependentId || !byId.Contains(dependentId))
                continue;

            foreach (var wanted in proposal.Prerequisites)
            {
                // A name resolves only once the sibling proposal that creates that topic has been
                // accepted; until then the edge is simply skipped and picked up on a later pass.
                var target = wanted.TopicId is { } id && byId.Contains(id)
                    ? id
                    : wanted.Name is { } name && byName.TryGetValue(name.Trim(), out var found)
                        ? found
                        : (Guid?)null;

                if (target is { } prerequisiteId)
                    AddEdgeIfSafe(db, edges, dependentId, prerequisiteId);
            }
        }
    }

    /// <summary>
    /// Records a dependency unless it already exists, points at itself, or would close a loop.
    /// Returns whether anything was added.
    ///
    /// Rows go in through the DbSet, never onto a tracked topic's collection — the same rule
    /// <see cref="NewSourcesFor"/> documents. <paramref name="edges"/> is updated in step so the
    /// cycle check sees edges added earlier in the same pass.
    /// </summary>
    private static bool AddEdgeIfSafe(
        StudyDbContext db, List<TopicEdge> edges, Guid dependentId, Guid prerequisiteId)
    {
        if (dependentId == prerequisiteId)
            return false;
        if (edges.Contains(new TopicEdge(dependentId, prerequisiteId)))
            return false;
        if (PrerequisiteGraph.WouldCycle(edges, dependentId, prerequisiteId))
            return false;

        db.TopicPrerequisites.Add(new TopicPrerequisite
        {
            CourseTopicId = dependentId,
            PrerequisiteTopicId = prerequisiteId,
        });
        edges.Add(new TopicEdge(dependentId, prerequisiteId));
        return true;
    }

    /// <summary>
    /// Moves an absorbed topic's dependencies onto the survivor, in both directions.
    ///
    /// Without this, merging two names for one concept silently severs every path through it: what
    /// the absorbed topic rested on, and everything that rested on it, would both lose their link.
    /// The absorbed topic's own rows are left alone — it is soft-deleted, and the query filter
    /// already stops them counting.
    /// </summary>
    private static void RewireEdgesForMerge(
        StudyDbContext db, List<CourseTopic> topics, Guid absorbedId, Guid survivorId)
    {
        var edges = topics
            .SelectMany(t => t.Prerequisites)
            .Select(p => new TopicEdge(p.CourseTopicId, p.PrerequisiteTopicId))
            .ToList();

        // What the absorbed topic needed, the survivor now needs.
        foreach (var prerequisiteId in edges.Where(e => e.TopicId == absorbedId).Select(e => e.PrerequisiteId).ToList())
            AddEdgeIfSafe(db, edges, survivorId, prerequisiteId);

        // What needed the absorbed topic now needs the survivor.
        foreach (var dependentId in edges.Where(e => e.PrerequisiteId == absorbedId).Select(e => e.TopicId).ToList())
            AddEdgeIfSafe(db, edges, dependentId, survivorId);
    }

    public async Task RejectProposalAsync(Guid proposalId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var proposal = await db.TopicProposals.FirstOrDefaultAsync(p => p.Id == proposalId, ct);
        if (proposal is null || proposal.Status != ProposalStatus.Pending)
            return;

        proposal.Status = ProposalStatus.Rejected;
        await db.SaveChangesAsync(ct);
        await CloseRevisionIfSettledAsync(db, proposal.RevisionId, ct);
    }

    /// <summary>Accepts every still-pending proposal — the first run, where all of them are adds.</summary>
    public async Task<List<string>> AcceptAllAsync(Guid revisionId, CancellationToken ct = default)
    {
        List<Guid> pending;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            pending = await db.TopicProposals
                .Where(p => p.RevisionId == revisionId && p.Status == ProposalStatus.Pending)
                .OrderBy(p => p.Kind)
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        // Sequentially, each in its own transaction: a proposal can depend on one applied just
        // before it (a merge into a topic an earlier proposal added), and one blocked proposal
        // must not abort the rest of the revision.
        var blocked = new List<string>();
        foreach (var id in pending)
        {
            if (await AcceptProposalAsync(id, ct) is { } reason)
                blocked.Add(reason);
        }
        return blocked;
    }

    public async Task DiscardRevisionAsync(Guid revisionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var revision = await db.CourseMapRevisions
            .Include(r => r.Proposals)
            .FirstOrDefaultAsync(r => r.Id == revisionId, ct);
        if (revision is null)
            return;

        foreach (var proposal in revision.Proposals.Where(p => p.Status == ProposalStatus.Pending))
            proposal.Status = ProposalStatus.Rejected;
        revision.Status = RevisionStatus.Discarded;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Records a dependency by hand. Returns null on success, or why it was refused.
    ///
    /// Editable for the same reason importance is: a wrong edge would otherwise cost an API call
    /// to correct, and one bad link distorts the whole learning path below it.
    /// </summary>
    public async Task<string?> AddPrerequisiteAsync(
        Guid topicId, Guid prerequisiteId, CancellationToken ct = default)
    {
        if (topicId == prerequisiteId)
            return "A topic cannot be its own prerequisite.";

        await using var db = await factory.CreateDbContextAsync(ct);

        var topic = await db.CourseTopics.FirstOrDefaultAsync(t => t.Id == topicId, ct);
        var prerequisite = await db.CourseTopics.FirstOrDefaultAsync(t => t.Id == prerequisiteId, ct);
        if (topic is null || prerequisite is null)
            return "That topic no longer exists.";

        var edges = await db.TopicPrerequisites.AsNoTracking()
            .Where(p => p.Topic!.CourseId == topic.CourseId)
            .Select(p => new TopicEdge(p.CourseTopicId, p.PrerequisiteTopicId))
            .ToListAsync(ct);

        if (edges.Contains(new TopicEdge(topicId, prerequisiteId)))
            return null;

        if (PrerequisiteGraph.WouldCycle(edges, topicId, prerequisiteId))
            return $"That would make “{topic.Name}” and “{prerequisite.Name}” each depend on the other.";

        db.TopicPrerequisites.Add(new TopicPrerequisite
        {
            CourseTopicId = topicId,
            PrerequisiteTopicId = prerequisiteId,
        });
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task RemovePrerequisiteAsync(
        Guid topicId, Guid prerequisiteId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var edge = await db.TopicPrerequisites.FirstOrDefaultAsync(
            p => p.CourseTopicId == topicId && p.PrerequisiteTopicId == prerequisiteId, ct);
        if (edge is null)
            return;

        // A hard delete, unlike everything else here: the row carries no information of its own,
        // so there is nothing to preserve, and a soft-deleted edge would block re-adding the same
        // dependency later — the composite key is the whole row.
        db.TopicPrerequisites.Remove(edge);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPinnedAsync(Guid topicId, bool pinned, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var topic = await db.CourseTopics.FirstAsync(t => t.Id == topicId, ct);
        topic.ImportancePinned = pinned;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetImportanceAsync(Guid topicId, TopicImportance importance, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var topic = await db.CourseTopics.FirstAsync(t => t.Id == topicId, ct);
        if (topic.Importance != importance)
            topic.PreviousImportance = topic.Importance;
        topic.Importance = importance;
        // Setting it by hand pins it: otherwise the next re-map would quietly undo the correction.
        topic.ImportancePinned = true;
        topic.ImportanceReason = "Set by you.";
        topic.LastChangedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetDismissedAsync(Guid topicId, bool dismissed, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var topic = await db.CourseTopics.FirstAsync(t => t.Id == topicId, ct);
        topic.IsDismissed = dismissed;
        await db.SaveChangesAsync(ct);
    }

    private static async Task CloseRevisionIfSettledAsync(StudyDbContext db, Guid revisionId, CancellationToken ct)
    {
        var stillPending = await db.TopicProposals
            .AnyAsync(p => p.RevisionId == revisionId && p.Status == ProposalStatus.Pending, ct);
        if (stillPending)
            return;

        var revision = await db.CourseMapRevisions.FirstOrDefaultAsync(r => r.Id == revisionId, ct);
        if (revision is null || revision.Status != RevisionStatus.Pending)
            return;

        revision.Status = RevisionStatus.Applied;
        revision.AppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static List<TopicSource> ToSources(IEnumerable<ProposedSource> proposed) =>
        proposed.Select(s => new TopicSource
        {
            MaterialId = s.MaterialId,
            SourceReference = s.SourceReference,
            Mention = s.Mention,
        }).ToList();

    /// <summary>
    /// The sources <paramref name="topic"/> doesn't already have, so repeated runs over the same
    /// material don't pile up duplicate evidence.
    ///
    /// Returns them rather than attaching them to <c>topic.Sources</c>: <see cref="TopicSource"/>
    /// generates its key client-side, so an entity added to an already-tracked parent's
    /// collection arrives with both a populated primary key and a populated foreign key — which
    /// EF's change detection reads as an existing row, issuing an UPDATE that matches nothing
    /// and throwing DbUpdateConcurrencyException. Registering them through the DbSet is what
    /// makes them unambiguously new.
    /// </summary>
    private static List<TopicSource> NewSourcesFor(CourseTopic topic, IEnumerable<ProposedSource> proposed)
    {
        var added = new List<TopicSource>();

        foreach (var source in proposed)
        {
            bool Matches(Guid materialId, string? reference, TopicMention mention) =>
                materialId == source.MaterialId
                && mention == source.Mention
                && string.Equals(reference, source.SourceReference, StringComparison.OrdinalIgnoreCase);

            // Checked against what's already added in this pass too, since a merge folds two
            // topics' sources into one survivor and they may overlap.
            if (topic.Sources.Any(s => Matches(s.MaterialId, s.SourceReference, s.Mention))
                || added.Any(s => Matches(s.MaterialId, s.SourceReference, s.Mention)))
            {
                continue;
            }

            added.Add(new TopicSource
            {
                CourseTopicId = topic.Id,
                MaterialId = source.MaterialId,
                SourceReference = source.SourceReference,
                Mention = source.Mention,
            });
        }

        return added;
    }
}
