# Materials & AI Flashcard Generation — Design Plan

Status: **approved direction, not yet built** (planned 2026-07-27). Builds on MVP-0 (review loop, shipped).

## The mindset this feature serves

Not a PDF-to-flashcards converter. The app maintains two models:

1. **A course model** — structure (chapters/lessons in order), the course's own terminology, and an *evidence hierarchy*: exams reveal what gets tested, home assignments reveal what must be practiced now, book/lecture flow reveals dependency order. Importance is derived from evidence, never assumed.
2. **A student model** — the user's position in that structure. "Generate cards" implicitly means *for what I've covered, weighted by what's next* (upcoming assignment, latest lectures).

Properties that follow:
- **Living corpus** — every upload is a delta; the system closes gaps against existing card coverage instead of regenerating.
- **Faithful cards** — course terminology, LaTeX math, images, and every generated card traceable to source (file + page/section).
- **Heterogeneous input is first-class** — printed PDFs, scanned handwriting, screenshots, photos.

## Locked decisions

| Decision | Choice |
|---|---|
| AI backend | User's own Anthropic API key, called server-side from the Blazor Server process |
| Default model | `claude-opus-5` (per-task override in settings if the user chooses) |
| Language | Mixed — per-course language setting, default auto-detect; RTL-capable card rendering (`dir="auto"`) |
| Generated cards | Land in a **review inbox**; user approves/edits/rejects before they enter decks and scheduling |
| Course structure | AI proposes chapter/lesson tree from syllabus/book; user confirms/edits once; position marker is user-held with AI-suggested advancement |

## Domain model extensions (StudyApp.Core)

- **`CourseUnit`** — ordered tree: Chapter → Lesson/Topic. `Id, CourseId, ParentId?, Title, Order, Kind`.
- **`Material`** — `Id, CourseId, UnitId?, Kind (Exam | HomeAssignment | BookChapter | LectureNotes | HandwrittenNotes | Screenshot | Syllabus | Other), Title, FilePath, MimeType, DueDate? (assignments), Status (Uploaded → Ingested → Failed), UploadedAt`. Files stored under `%LOCALAPPDATA%\StudyApp\files\{courseId}\`.
- **`MaterialExtract`** — AI-normalized content per material: markdown+LaTeX text, page/section map, topic list, key-term candidates. The substrate all generation reads from (raw files are never re-sent once extracted, except for image crops).
- **`CourseGlossary`** — per-course term list (term, definition, source ref), grown during ingestion, injected into generation prompts → "correct terminology".
- **`TopicImportance`** — per unit/topic: score + rationale + evidence links (which exam questions / assignment tasks). Rebuilt when new exams/assignments land.
- **`Course.CurrentUnitId`** — the position marker.
- **`CardSuggestion`** — inbox entry: front/back markdown, proposed unit tag, source ref (material + page), importance, rationale, batch id, status (Pending/Accepted/Rejected).
- **Card upgrades** — `SourceMaterialId?`, `SourceReference?` (page/section), `UnitId?`; front/back become **markdown with inline LaTeX**; optional image attachments (stored files, referenced from card markdown).
- **`GenerationJob`** — kind (Ingest / ProposeStructure / GenerateCards / AnalyzeImportance / DeltaUpdate), status, progress, log, token usage.

## AI integration (StudyApp.Web, server-side)

- **SDK:** official Anthropic C# SDK (`dotnet add package Anthropic`), `AnthropicClient` with the key from local settings. Key stored in `%LOCALAPPDATA%\StudyApp\settings.json` (DPAPI-protected), entered on a Settings page — never in the repo, never sent to the browser.
- **Model calls:** `claude-opus-5` default (thinking on by default — omit the `thinking` parameter; effort tuned per task: `high` for importance analysis and structure proposal, `medium` for routine card generation). Streaming for long extractions.
- **PDF/image input:** PDFs as base64 `document` blocks (no beta; ≤32 MB request, ≤600 pages — chapter-split larger books by page range); screenshots/photos as `image` blocks; handwriting handled natively by model vision. Files API (beta) to upload once and reference across multiple ingestion passes on the same file.
- **Structured outputs** (`OutputConfig.Format`, JSON schema): card batches, topic lists, structure proposals, importance profiles all come back as validated JSON — no fragile text parsing.
- **Prompt caching:** stable per-course prefix (glossary + relevant extracts) with `cache_control` so repeated generation runs over the same chapter bill cached-read rates.
- **Batch API** (50% price, ≤24 h turnaround) as an option for full-book ingestion — ingestion is a background job anyway.
- **Job runner:** `BackgroundService` + channel queue in the Blazor Server process; progress surfaced in UI; token usage logged per job for a running spend display.

## Cost expectations (Opus 5: $5/M input, $25/M output — estimates)

| Operation | Approx. cost |
|---|---|
| Ingest an exam or assignment (5–15 pages) | $0.10–0.40 |
| Ingest handwritten lecture notes (5–10 scanned pages) | $0.10–0.30 |
| Ingest a full textbook (~300 pages) | $5–10 (≈ half via Batch API) |
| One generation run (chapter-scoped, cached context) | $0.30–0.90 first run, less on repeats via caching |
| Importance re-analysis after new exam | $0.20–0.60 |

Levers if spend matters later (user's call, not defaults): Batch API for ingestion, per-task model override, chapter-scoped rather than course-scoped runs.

## Pipelines

**Ingestion** (per upload, background): store file → Claude reads it → `MaterialExtract` (markdown+LaTeX, section map, topics, term candidates) → glossary merge → if Exam/Assignment: importance analysis → if Syllabus/Book: structure proposal for user confirmation.

**Generation** (user-triggered, scoped): resolve scope ("this material" / "unit X" / "where I stand" = units ≤ position + next-due assignment topics boosted) → build context (relevant extracts + glossary + existing card fronts in scope for dedup/gap awareness) → structured-output card candidates with source refs, unit tags, importance rationale → **inbox** → approve/edit/reject → accepted cards enter decks as New with full provenance.

**Delta/gap closing** (v0.5): coverage map (topics ↔ cards via provenance) → new material's topics diffed against coverage → generate only uncovered/changed topics; same inbox flow. Course-notes maintenance: AI proposes note-section updates from new material; user approves (same pattern as cards).

## Rendering upgrades

- Cards and notes render **markdown + LaTeX**: Markdig (HTML disabled — XSS) + **KaTeX bundled into wwwroot** (offline, no CDN), `throwOnError: false`, no `trust` extensions.
- `dir="auto"` on card faces and notes for mixed/RTL text; per-course language stored and passed to prompts (preserve source language and terminology).
- Card images served from app storage via a controller endpoint; referenced from card markdown.

## Phasing (each phase ships usable)

| Phase | Scope | AI? |
|---|---|---|
| **v0.2 — Materials & structure foundation** | Upload/organize materials by kind + unit; PDF/image viewing; chapter/lesson structure editor; position marker; assignment due dates (surface on Home); **card rendering upgrade (markdown + LaTeX + RTL)** | No |
| **v0.3 — Ingestion + first generation** | Settings page (API key, model, spend); ingestion pipeline → extracts + glossary; AI structure proposal w/ confirmation; "Generate cards from this material" → inbox → approve into decks; source refs on cards; job progress UI | Yes |
| **v0.4 — Importance + progression** | Exam/assignment analysis → `TopicImportance`; "Generate for where I am" (position + next assignment); automatic unit tagging; per-unit coverage view | Yes |
| **v0.5 — Deltas & gap closing** | Coverage map; new-material diffing ("close gaps"); notes maintenance proposals | Yes |

v0.2 is deliberately AI-free: it is the substrate (organized, tagged, positioned materials) that makes every later AI feature scoped and cheap, and it's independently useful the day it ships.

## Verification per phase

- **v0.2:** upload each kind incl. a scanned handwritten PDF; build a structure tree; tag materials; set position; write a LaTeX card manually and see it render in review (RTL text too); assignment due date shows on Home.
- **v0.3:** paste key in settings; ingest a real lecture PDF; confirm structure proposal flow; generate cards from one material; verify inbox approve/edit/reject; check accepted card carries source ref, unit tag, LaTeX; verify token spend logged.
- **v0.4:** ingest a past exam; importance profile lists tested topics with rationale; "generate for where I am" produces cards biased to exam-relevant topics in covered units.
- **v0.5:** upload new lecture notes for a covered unit; system proposes only-gap cards; approve; coverage view updates.

## Risks

- **Extraction quality on messy handwriting** — mitigated by frontier-model vision + inbox review (bad cards never enter decks silently); worst case, user types the correction in the inbox editor.
- **Cost surprises** — spend log + per-job token usage from day one; batch/model levers documented.
- **Scope gravity** — v0.3 is the largest single step; the inbox and one generation path ("from this material") is the v0.3 cut line; "where I stand" waits for v0.4 even though it's tempting.
- **Schema churn** — Card content becomes markdown; existing plain-text cards are valid markdown, no data migration pain.
