# StudyApp

A personal study environment: courses → decks → flashcards, reviewed daily with spaced repetition (SM-2). Blazor Web App (.NET 10, interactive server rendering), SQLite, single local user.

## Run

```bash
dotnet run --project src/StudyApp.Web --urls http://localhost:5170
```

Then open http://localhost:5170. Works fine in a phone browser too (responsive layout).

A running instance holds a lock on its own build output, so `dotnet build` fails with
`MSB3027 … file is locked by StudyApp.Web` if you forget to stop it first. Stop `Ctrl+C` in
its terminal, or if it's detached:

```bash
Get-Process StudyApp.Web -ErrorAction SilentlyContinue | Stop-Process
```

The app recovers automatically from being killed mid-write — a damaged write-ahead log is
quarantined and retried at startup — but a clean `Ctrl+C` is still kinder to the database.

## Test

```bash
dotnet test
```

## Where your data lives

Everything stateful sits under one directory, so a single backup or volume covers it all:

- Database: `studyapp.db`
- Uploaded materials: `files/`
- Automatic backups: `backups/` — timestamped snapshots, taken at startup *before* migrations run
  and every 4 hours while the app is up. Each is verified after writing; the newest 10 are kept,
  plus the first and last of each of the last 7 days.

Two details worth knowing, both learned the hard way:

- Snapshots are **first-and-last per day**, not newest-per-day. A day can start healthy and end
  damaged, and keeping only the latest would discard the good copy while retaining the broken one.
- If a snapshot has **fewer cards than the previous one**, startup logs a loud warning. The backup
  is still correct — it faithfully recorded what was there — but that is exactly the moment to
  intervene, before retention ages out the copies that still hold the data.

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

Set a sign-in method and an API key on the deployment:

```bash
fly secrets set StudyApp__Password=... StudyApp__Anthropic__ApiKey=sk-ant-...
```

> **Configure a sign-in method on any deployment that has an API key.** Auth is off when
> none is configured (so local runs stay frictionless), but an open instance with a key on it
> lets anyone who finds the URL spend real money on generation runs — not just read your
> flashcards.

## Security

Authentication activates as soon as *any* sign-in method is configured; with none, the app
runs wide open (convenient locally, never do it on a public URL).

**Password.** Prefer a hash over the plaintext variable — generate one with:

```bash
dotnet run --project src/StudyApp.Web -- hash-password "your password"
```

Set the result as `StudyApp__PasswordHash` (PBKDF2-HMAC-SHA256, 600k iterations). The
plaintext `StudyApp__Password` still works and is fine for local development.

**Google / GitHub sign-in.** Handy on a phone — no password to type. Create credentials, then:

```bash
fly secrets set StudyApp__Auth__Google__ClientId=... StudyApp__Auth__Google__ClientSecret=... StudyApp__Auth__AllowedIdentities=you@gmail.com
```

- *Google*: Cloud Console → APIs & Services → Credentials → OAuth client ID (Web
  application). Authorized redirect URIs: `https://<your-app>.fly.dev/signin-google` and
  `http://localhost:5170/signin-google` for local testing.
- *GitHub*: Settings → Developer settings → OAuth Apps. Callback URL:
  `https://<your-app>.fly.dev/signin-github`.

> **`StudyApp__Auth__AllowedIdentities` is mandatory with any OAuth provider** — comma
> separated emails (Google) or usernames (GitHub). "Sign in with Google" proves who someone
> is, not that they may enter; without an allowlist *every Google account on earth* is a
> valid credential for your app. The app refuses to start if a provider is configured
> without one.

Also in place: brute-force rate limiting (10 sign-in attempts per 5 minutes per IP),
antiforgery on all form posts, open-redirect protection on `ReturnUrl`, a Content-Security
Policy plus `nosniff`/`X-Frame-Options`/`Referrer-Policy`, and Data Protection keys persisted
to the data volume so sign-ins survive redeploys.

## AI flashcard generation

Set your Anthropic key first. For the current terminal only:

```bash
$env:StudyApp__Anthropic__ApiKey = "sk-ant-..."
```

To persist it, use `setx StudyApp__Anthropic__ApiKey "sk-ant-..."` — but note that `setx`
only affects processes started *after* it runs. The terminal you type it into keeps its old
environment, so restarting the app from that same terminal won't pick the key up; open a new
terminal. Settings shows **Connected** with the last four characters when it's loaded.

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
- ✅ v0.3: AI ingestion → reusable extracts → card generation into a review inbox
- ✅ v0.4: config-gated auth (password + Google/GitHub); progress tracking (mastery/recall, rolling
  deck → unit → course); `/how-it-works` explaining the scheduling and progress math
- next — **the course map**: upload a course's material in bulk, ingest it all, and derive one
  weighted model of what the course covers. Importance is driven by what the course actually
  assesses, with per-course weights (some courses live or die by the final; others by weekly
  assignments). The map revises itself as new material lands — and every revision is reviewed
  before it applies. Then: coverage-aware card generation, a study plan against assignment due
  dates, and practice exams after that.
- Scheduler: SM-2 today behind an `IScheduler` seam; FSRS is a drop-in swap later — though FSRS
  fits itself to your review history, so it only starts paying off once there's a real one.
