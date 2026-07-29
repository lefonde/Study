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
   vision and stores a `MaterialExtract`: markdown with LaTeX preserved, original language
   intact, figures described, split into sections tagged by page.

   How much is reproduced depends on the material's **kind**. Your own work — exams,
   assignments, handwritten notes, the syllabus — is transcribed word for word, because a
   question means exactly what it says. Published reference material — book chapters, lecture
   notes — becomes comprehensive study notes instead: every definition, theorem, formula and
   worked-example method is kept in full and formal statements are quoted, but the surrounding
   prose is rewritten rather than copied. That is what the later stages actually need, and
   attempting to transcribe a whole course book verbatim will simply be refused by the API's
   content filter.
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

## Mapping a course

Once a course's material is in, the **Map** tab turns it into a model of what the course covers
and what matters in it.

1. **Materials** → select as many files as you like at once, upload, then **Ingest all**. The
   estimate shown before you confirm is a wide range on purpose — real cost tracks page count,
   which is only knowable by opening the file.
2. Set **what this course is graded on** (same tab). Defaults rank exams above assignments above
   lecture notes; change it if your course doesn't work that way. This is what decides which
   topics come out as *core*, so it's worth a moment.
3. **Map** → *Map this course*. Every proposed topic is reviewed before it applies.
4. **Link cards** — connects existing cards to the topics they teach, so coverage counts them.
   Only ever sends cards that aren't linked yet, so re-running it later is cheap.

After that the Map tab shows **coverage beside mastery**. They answer different questions and are
never blended: low coverage means write more cards, low mastery means study the ones you have.
*Core covered* is the number to act on — overall coverage counts peripheral topics and can look
fine while everything examinable has nothing behind it.

**Generate cards for the gaps** writes cards only for topics that have none, drawing on the
material those topics cite. A topic with no supporting source is left empty rather than filled
with invented material — a card that hides a gap is worse than the gap.

Upload a past exam later and **re-map**: the emphasis legitimately shifts, and you review that
shift before it lands. Topics keep their identity across a re-map, so cards stay attached.
**📌 Pin** a topic to fix its importance yourself — re-mapping will never override it.

## The learning path

Topics also record what they **build on**, which turns the list into an order you can follow. The
Map tab's **Path** view lays them out in stages — a topic sits one stage after the deepest thing
it needs — and draws the dependencies between them. **List** is the same topics in one column
with their settings, and the better view on a phone.

**🧭 Work out the learning path** is a separate run from mapping, and on an existing course it is
the one you need. Mapping is a *revision*: shown a settled map and no new evidence, it correctly
proposes nothing, so re-mapping will never retrofit a path onto topics that already exist — it
only wires up what it happens to be adding. The learning-path run does nothing but fill those
gaps. It sends topic names and section titles rather than the material itself, so it is cheap
(~$0.02 for a 17-topic course), it only ever *adds* dependencies, and running it again once the
path is complete finds nothing and costs almost nothing.

Prerequisites are editable by hand from the List view: a wrong edge would otherwise cost an API
call to fix, and one bad link distorts everything below it. Loops are refused by name.

**Assignments appear on the path**, placed after the deepest stage they assess. Click one and
everything unrelated dims, so which subjects feed which deadline is something you see rather than
work out.

## Before an assignment

Give an assignment a **due date** and its **📋 Plan** says what stands between you and it. It has
two halves:

- **Assessed** — what the assignment's own material tests.
- **Foundations** — what those rest on, pulled in through the prerequisite graph. A foundation
  with no cards is flagged as *blocking*, because it stops the topics above it being learnable.

That one is pure arithmetic over the map — no API call, no cost.

**🎯 Study this assignment** starts a review session over just those cards. It doesn't use the
usual "due today" filter: preparing for Thursday means clearing what won't still be solid *on*
Thursday, which is a larger and more useful set. The 20-new-cards-per-session cap still applies —
the night before an assignment is exactly when cramming 60 new cards works worst — so the
completion screen tells you how many are waiting instead.

**✓ Submitted** marks an assignment handed in; it stops appearing as upcoming everywhere.

## Getting a solution reviewed

Press **📤 Solution** on an assignment and upload your answers. The file is ingested and then read
alongside the assignment's own questions, coming back as per-question feedback: a verdict
(correct / partly / incorrect / missing), what specifically is wrong, and which course topic each
question turns on.

There is **no score or predicted grade**, deliberately. Nothing here has seen the marking scheme,
and a number invented by a model would carry an authority it hasn't earned — the per-question
verdicts say the same thing honestly.

The payoff is the last column: because every finding names a topic, **✨ Generate cards for the
topics you slipped on** turns a mistake into cards rather than a note to self.

Your solutions are kept out of the course map and out of the card glossary. They record what you
think, not what the course teaches, and a confident wrong answer must not be able to promote a
topic or define a term.

## Roadmap (deliberately deferred)

- ✅ v0.2: course structure (chapters/lessons + "you are here"), material uploads, markdown/LaTeX/RTL card rendering
- ✅ v0.3: AI ingestion → reusable extracts → card generation into a review inbox
- ✅ v0.4: config-gated auth (password + Google/GitHub); progress tracking (mastery/recall, rolling
  deck → unit → course); `/how-it-works` explaining the scheduling and progress math
- ✅ v0.5: **the course map** — bulk upload and ingest a whole course, derive one weighted model
  of what it covers (importance driven by what the course *assesses*, with per-course weights),
  revised under review as new material lands; coverage reported beside mastery; card generation
  aimed at the gaps; and a study plan against assignment due dates
- ✅ v0.6: **the learning path** — prerequisites between topics, derived by the same map run and
  editable by hand; the map drawn as stages with the dependencies between them and assignments
  placed on it; plans that reach past what an assignment literally tests into the foundations it
  rests on; assignment-scoped review sessions and a submitted flag; and solution upload with
  per-question feedback that names the topic each mistake turns on
- next: **practice exams** generated from what the course actually assesses — the reason
  assessment materials keep their full text and `TopicMention.Assessed` is recorded
- Scheduler: SM-2 today behind an `IScheduler` seam; FSRS is a drop-in swap later — though FSRS
  fits itself to your review history, so it only starts paying off once there's a real one.
