# StudyApp — project conventions

Personal study environment (flashcards + spaced repetition first; materials, calendar, AI generation later). Single local user, no auth. The user (Alex) reviews and steers; see README for product roadmap.

## Stack & layout

- .NET 10, C#. Blazor Web App with **global interactive server** rendering — no API layer; components call services directly.
- `src/StudyApp.Core` — pure domain (entities, SM-2 scheduler, due policy, session queue, import parser). **No web or EF dependencies. Keep it that way.**
- `src/StudyApp.Web` — Blazor UI, EF Core (SQLite) in `Data/`, thin services in `Services/`.
- `tests/StudyApp.Core.Tests` — xUnit. All scheduling/parsing logic must be covered here.

## Conventions

- Data access: `IDbContextFactory<StudyDbContext>` per operation; never inject `StudyDbContext` directly (Blazor Server).
- Time: always through injected `TimeProvider` (never `DateTime.Now/UtcNow` in domain logic). "Due" semantics live only in `DuePolicy` — study day rolls over at 04:00 local.
- Soft deletes (`IsDeleted` + global query filters) everywhere; `CreatedAt/UpdatedAt` maintained by `TimestampInterceptor` — never set manually.
- Scheduling changes go through `IScheduler`. FSRS should replace `Sm2Scheduler` behind that interface, never inline in UI/services.
- Schema changes: EF migrations only (`dotnet dotnet-ef migrations add <Name> --project src/StudyApp.Web`). DB file is real user data — backups run at startup, keep it working.
- Markdown rendering: `MarkdownRenderer` only (raw HTML disabled — XSS). LaTeX in card/notes markdown is authored as `$...$` / `$$...$$` — Markdig's math extension (bundled in `UseAdvancedExtensions`) converts that to literal `\(...\)`/`\[...\]` text before KaTeX ever sees it, so `wwwroot/js/mathRender.js`'s auto-render delimiters target `\(\)`/`\[\]`, not `$`. KaTeX is vendored offline under `wwwroot/lib/katex` (pulled via `npm pack katex`, not a CDN).
- File uploads: `Program.cs` sets `AddSignalR(o => o.MaximumReceiveMessageSize = 64MB)` — Blazor Server's `InputFile` rides the SignalR circuit, whose default 32 KB cap otherwise silently blocks anything but tiny files.
- Any `IJSObjectReference.InvokeVoidAsync` called from `OnAfterRenderAsync` against an `@ref` element **must** be wrapped in `try { } catch (Exception ex) when (ex is JSDisconnectedException or JSException) { }`. A rapid re-render (e.g. fast card grading) can make the `ElementReference` go stale between the guard check and the awaited call resolving; letting that exception bubble terminates the whole Blazor circuit, not just the one render — confirmed by reproducing it under a rapid-fire synthetic input stress test.
- Routed pages that map multiple route templates to one component (e.g. `Review.razor`'s `/review`, `/review/course/{id}`, `/review/deck/{id}`) must build their state from `OnParametersSetAsync`, not `OnInitializedAsync` — Blazor reuses the component instance across same-component navigations, so `OnInitializedAsync` only runs once ever. Building session/query state there leaves it frozen on whatever scope loaded first. Guard against rebuilding on every render by comparing the incoming parameters against what was last built.
- Uploaded material files are served inline only for an explicit MIME allowlist (`Program.cs`, the `/materials/{id}/file` endpoint) — `text/html` and `image/svg+xml` are deliberately excluded since both can carry same-origin executable script, and `MimeType` is client-supplied at upload time so this allowlist is the actual security boundary. Anything outside it is forced to download via `Content-Disposition: attachment` instead.

## Commands

- Run: `dotnet run --project src/StudyApp.Web --urls http://localhost:5170`
- Test: `dotnet test`
- Browser preview: `.claude/launch.json` config `studyapp`
