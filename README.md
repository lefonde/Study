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
Railway, Azure Container Apps, a VPS). `fly.toml` is set up for Fly.io specifically.

Install the CLI once (PowerShell):

```bash
iwr https://fly.io/install.ps1 -useb | iex
```

Then, from the repo root:

```bash
fly auth login
```

```bash
fly apps create studyapp-lefonde
```

```bash
fly volumes create studyapp_data --size 1 --region fra
```

```bash
fly deploy
```

The volume's region must match `primary_region` in `fly.toml`. Subsequent deploys are
just `fly deploy`; the volume and its data survive.

Two constraints that are easy to get wrong:

- **It must run as exactly one instance.** SQLite lives on a local volume and Blazor Server
  keeps per-user circuit state in memory; a second replica would corrupt the database and
  drop sessions. `fly.toml` pins `min_machines_running = 1` with autoscaling off.
- **The data directory must be a mounted volume.** Without `[mounts]`, every redeploy starts
  from an empty database and loses all uploaded files.

Set a password and an API key on the deployment:

```bash
fly secrets set StudyApp__Password=... StudyApp__Anthropic__ApiKey=sk-ant-...
```

> **Set `StudyApp__Password` on any deployment that has an API key.** Auth is off when no
> password is configured (so local runs stay frictionless), but an open instance with a key
> on it lets anyone who finds the URL spend real money on generation runs — not just read
> your flashcards.

## AI flashcard generation

Two stages, deliberately separated:

1. **Ingest** a material once (Materials tab → *Ingest*). Claude reads the PDF or scan with
   vision and stores a `MaterialExtract`: the document as markdown with LaTeX preserved,
   original language intact, figures described, split into sections tagged by page.
2. **Generate cards** from that extract (→ *Generate cards*). This never re-reads the
   original file, which is why the first pass is the expensive one and every run after it is
   cheap and fast.

Generated cards land in the course's **Inbox**, not in a deck. Nothing enters scheduling
until you accept it; you can edit a card before accepting or reject it outright. Accepted
cards keep their source material and page reference.

Inspect any extract at *Materials → Ingested*. This matters: if handwriting was misread or a
formula mangled, every card generated from it inherits the error — fix it by re-running
extraction rather than patching cards one by one.

Costs are estimated per run and totalled in **Settings** (roughly $0.10–0.40 to ingest an
assignment, $5–10 for a full textbook, well under $1 for a generation run).

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
