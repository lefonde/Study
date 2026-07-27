# StudyApp

A personal study environment: courses → decks → flashcards, reviewed daily with spaced repetition (SM-2). Blazor Web App (.NET 10, interactive server rendering), SQLite, single local user.

## Run

```bash
dotnet run --project src/StudyApp.Web --urls http://localhost:5170
```

Then open http://localhost:5170. Works fine in a phone browser too (responsive layout).

## Test

```bash
dotnet test
```

## Where your data lives

Everything stateful sits under one directory, so a single backup or volume covers it all:

- Database: `studyapp.db`
- Uploaded materials: `files/`
- Automatic backups: `backups/` — one per day, newest 7 kept, taken at startup *before* migrations run.

That directory defaults to `%LOCALAPPDATA%\StudyApp` and is overridden with the
`StudyApp__DataDirectory` environment variable (the container sets it to `/data`).

## Deploying

The app is a normal ASP.NET container — the included `Dockerfile` runs anywhere (Fly.io,
Railway, Azure Container Apps, a VPS). `fly.toml` is set up for Fly.io specifically:

```bash
fly launch --no-deploy --copy-config
```

```bash
fly volumes create studyapp_data --size 1
```

```bash
fly deploy
```

Two constraints that are easy to get wrong:

- **It must run as exactly one instance.** SQLite lives on a local volume and Blazor Server
  keeps per-user circuit state in memory; a second replica would corrupt the database and
  drop sessions. `fly.toml` pins `min_machines_running = 1` with autoscaling off.
- **The data directory must be a mounted volume.** Without `[mounts]`, every redeploy starts
  from an empty database and loses all uploaded files.

> **No authentication.** Anyone who finds the URL can read, edit and delete everything.
> That's fine for a throwaway/private deployment; add auth before putting anything you'd
> mind losing behind a public address.

## Using it

1. **Courses** → create a course (name + color). Each course has a markdown notes field.
2. Open the course → add a **deck** (e.g. per chapter).
3. Open the deck → **bulk import**: paste one card per line — `front<TAB>back`, `front :: back`, or `front;back`. Preview shows duplicates and skipped lines before committing.
4. **Home** → Start review. Space reveals, 1–4 grades (Again/Hard/Good/Easy), ↩ undoes the last grade. New cards are capped at 20 per session; "Again" cards repeat until you pass them.

## Roadmap (deliberately deferred)

- ✅ v0.2: course structure (chapters/lessons + "you are here"), material uploads, markdown/LaTeX/RTL card rendering
- next: AI generation of cards from materials (Claude API, server-side); importance ranking; delta updates
- Scheduler: SM-2 today behind an `IScheduler` seam; FSRS is a drop-in swap later
- Auth is still unbuilt — required before this holds anything you'd mind a stranger reading
