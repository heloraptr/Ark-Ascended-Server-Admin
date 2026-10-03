# How this was built

This project was written with the assistance of [Claude Code](https://claude.com/claude-code).
Most of the C#, Razor, tests, and documentation in the repository came out of Claude sessions that
I directed, reviewed, and shipped. [OpenAI Codex](https://openai.com/codex/) hardened the plans:
every substantial design went through rounds of Codex review before implementation started. I made
the decisions, ran the software on my own servers, and merged every change.

I write .NET for a living. That matters for how to read the rest of this page: I did not read
every diff line by line, but C# is a language I read fluently, and when I look at a piece of this
code I know what it does and why it is there. If both tools disappeared tomorrow I could maintain
this myself — more slowly, but without having to learn it first.

The commit history does not say any of this, by choice: a commit carries the name of the person
accountable for it, and the tooling is disclosed here, once, where someone looking for it will
find it. If you came here to decide whether this code was understood by the person who ships it,
this page is the evidence.

## Why another server manager

There are a lot of ARK server managers, and I ran two of them for my own servers before writing
this one. Both are good tools made by people who put real work into them, and both shaped what
this app does.

[Ark Server Creation Tool](https://github.com/Ragonz/Ark-Server-Creation-Tool) by Ragonz is the one
I liked most and used longest. Its cluster and instance management is the model for the Instances
page here: servers grouped under a cluster, each with its own settings, all visible at once. Two
things eventually pushed me off it. Every instance carried its own copy of the game, which at
twelve-plus gigabytes a server adds up fast, and the map was a fixed dropdown, so a new or modded
map meant waiting for a release. That was true as of the version I stopped using; it may not be
true now.

[ASA Server Manager](https://github.com/Grahamvs/ASA-Server-Manager) by Grahamvs solved the install
problem: one game install shared by every server, which is the layout this app uses. In the version
I tried, each server was a profile file and only one was open at a time, so running a cluster meant
switching between them. I never used it in earnest, but it is where the shared-install idea was
proved out for me.

What neither did, and what I wanted, was run without someone logged in. Both are desktop apps that
live in an interactive session, so managing a server meant remoting into the box. Neither, nor any
other manager I found, talked to the CurseForge API: mods were lists of IDs you typed in. Those two
gaps — a service on the game box operated from a browser, and a mod library that knows what a mod
is — are the reason this project exists. Everything else is a consequence of wanting those on one
Windows machine with no cloud account in the loop.

A few other projects answered a specific question along the way: [yark](https://github.com/gabomarin/yark)
for the build-id poll behind the update check, and [ASMA](https://github.com/ChronosWS/asma) and
[ArkAscendedServerManager](https://github.com/JensvandeWiel/ArkAscendedServerManager) for how
the player cap is really passed to the game.

They were the inspiration and the reference; none of their code is in here.

## The order things happened in

The first commit is from 2026-09-06 and the 1.0.0 tag is from early October 2026. In between, the
work followed the same loop every time, and the loop is the part worth describing.

**Design before code.** The project started with a written set of requirements from me: what the
app had to do, what it must not do, the constraints of the box it would run on. Claude then
interviewed me against that document, one open choice at a time — not "what do you want to build"
but "you said X; here is what X forces; which way?" — until every branch was closed. The result was
a design note and then a phased plan for the minimum viable product. The same format was used again
for the public release and for everything after 1.0. Those notes are in
[`docs/design/`](design/README.md), with the dates they were written.

**Spikes for the things nobody could know from reading.** Before the plan was final, throwaway
code was run against a real ASA server to find out how it actually behaves: whether stdout or the
log file is the reliable output source (the log), whether `saveworld` returns before the save file
is written (it does, by about a second), what exit code a clean `doexit` produces (−1, so the exit
code can never mean "crashed"), what happens to the game when the service that launched it stops
(nothing), and which directories can be junctions to a shared install. Later a second spike on the
live server established that the ban list is one box-wide file that the game rewrites from memory,
which is why this app does not manage bans. The findings are in
[`docs/design/spike-results.md`](design/spike-results.md).

**A second model reviews the plan.** Each plan went to Codex before implementation. More on that
below.

**Implementation in branches, merged by PR.** The MVP was built in six phases over the first week.
After that, every piece of work was a branch with a plan behind it, opened as a pull request, and
squash-merged by me. The plan IDs in the commit log (B0 through B10, "spike B1", and so on) point
at the post-1.0 plan in `docs/design/`.

The pull request descriptions are long for a one-person project, and that is deliberate. I had
already approved the plan before the code was written, so the description was never there to
persuade me. It is the record of what the change actually did and why, written at the moment that
was fresh, for the version of me who opens the file a year from now wondering why a thing is the
way it is. Now it serves a reader of the repository the same way.

**Tests as part of the work, not after it.** Unit tests cover the domain in `Core` with no
filesystem or Windows dependencies; integration tests run the infrastructure against a real
temporary filesystem and SQLite database. As of 1.0.0 there are about nine hundred test methods
between them, and CI runs both suites and checks that the EF migration snapshot matches the model
on every pull request.

**Review of the whole, not only the diff.** Before 1.0.0, four independent review passes were run
over the entire solution. They produced 30 findings. I went through them one at a time and decided
each one: 18 were fixed in one pull request, six were deferred to tracked issues, and the rest were
closed with a reason. The decisions, including the ones I overruled, are recorded alongside the
plans.

**My own servers as the last test.** Everything was deployed to the machine that runs my cluster
and used there before it was tagged. A clean-VM install of the release candidate was run against a
written smoke protocol, and the issues that produced became their own batch of fixes.

## Who did what

What I did:

- Decided what the app is for, what it will never do, and every design choice that involved a
  tradeoff. The "never" list — localization, UPnP, a Discord bot, self-update, remote agents — is
  mine.
- Answered the design interviews, and for each review finding chose fix, defer, or won't-fix.
- Reviewed every change and merged every pull request. Most of my review was done by running the
  thing — the UI, the installer, the service on my own box — with the code read where the behavior
  or the plan gave me a reason to; the tests and the second-model review covered the rest. Some
  work went back for rework: the scheduled-actions feature was rebuilt after I saw the first version
  and asked for cron expressions, a builder dialog, and an existing cron library instead of a
  hand-written parser. One feature, crash restart, I handed off at the planning stage: the decisions that mattered —
  a per-instance toggle, off by default, what counts as a crash, a cap on restarts before giving
  up — were already made in the post-1.0 interview, it was the last item of a long day, and the
  implementation plan was something Claude and Codex could settle between them. I reviewed the
  pull request. The review log records the hand-off as it happened.
- Ran the spikes that needed a real player or a live server: joining a test server to capture the
  real join and `ListPlayers` output, sending the ban commands on my own box to see what the game
  did with the file.
- Ran the smoke tests, deployed every build, and created every tag.
- Sent the first draft of the user guide back. Every page had the same section headings and the
  prose leaned on names from the code. The rule that came out of it — write for the person at the
  keyboard, in plain words, no template — is in `CLAUDE.md` for anyone to read. It is a rule
  about who the docs are for, not about hiding how they were written; this page is the same voice.

I was also corrected by the process at least once that I know of: I was sure the max-player count
belonged in the INI, and the evidence from the spike and from other managers said it belongs on the
command line. It is on the command line.

What Claude did:

- Ran the design interviews and wrote the resulting notes and plans.
- Wrote the implementation, the tests, and the first draft of every document, from the agreed
  plans. Larger features were split across parallel sessions, each with a brief.
- Wrote the solution reviews and the per-finding fix plans.
- Took screenshots, ran the local UI before each pull request, and kept a running handover between
  sessions so the next one started with the current state rather than the code alone.

What Codex did:

- Hardened the plans: read each one against the code it referred to and argued with it until it
  held up. See the next section.

## Second opinions

Eight plans went through Codex review before implementation, 34 review rounds in total: the MVP
plan, the public-release plan, the post-1.0 plan, the crash-restart design, a batch of UI tweaks,
and three of the pre-release fixes. The format was adversarial: Codex read the plan and the code
it referred to, returned findings, and the plan was revised and resubmitted until Codex approved it
or I decided the remaining points were settled.

Neither the interview nor the review loop is my invention. Both came from chaseai-yt's
`grill-me-codex` and `codex-review` skills in
[claudex-loop](https://github.com/chaseai-yt/claudex-loop), which were used for every plan here.
Their interview half descends from Matt Pocock's original
[grill-me](https://github.com/mattpocock/skills) skill, which is the one most people know. Both
repositories have moved on since the versions used here, and both are worth a look if the loop
described on this page sounds useful.

Some of what it caught, which would otherwise have been found later and more expensively:

- The MVP plan had the web host project targeting `net10.0` while referencing a
  `net10.0-windows` library, which does not build, and the wrong host builder for a Windows
  service.
- The stop path recorded "stop requested" only after the shutdown countdown began, so a server that
  died during a graceful stop would have been counted as a crash. The intent is now recorded the
  moment the stop is accepted.
- The firewall cleanup selected rules by `Name` where the installer had set `DisplayName`, which
  would have left every old rule in place while reporting success.
- Copying the SQLite file is not a safe live backup; the export now uses the engine's online backup
  API.
- An editor lost-edit race that turned out to be a real bug already on `main`.

It was also wrong sometimes, or right about something I had deliberately scoped out. Cluster-wide
coordinated backups, service-interruption integration tests, and a partial-line console
optimization were all raised and all declined, with the reasons written down in the review logs.
Keeping those logs is part of the point: the review is only worth something if the rejections are
as visible as the fixes.

## What this means for reading the repository

- `docs/design/` holds the design notes, plans, spike results, and review logs, cleaned of
  machine-specific detail but otherwise as written. They are dated and were not revised after the
  fact to look better. Where a later decision reversed an earlier one, both are there.
- [`CLAUDE.md`](../CLAUDE.md) at the repository root is the instruction file Claude Code reads
  when working here. It holds the project rules a contributor would need anyway: how to run the
  tests, the one-migration-branch rule, the writing voice for docs, the requirement to run the UI
  locally before opening a pull request. It was added at 1.0.0; before that the rules lived in
  session notes.
- Pull requests are squash-merged, so one commit on `main` is one reviewed unit of work, and the
  PR number in the subject leads to the discussion.
- Commits carry no AI co-author trailers. See the top of this page.

## Contributing with the same tools

Contributions written with Claude Code, Codex, or anything else are welcome on the same terms as
any other: discuss the design first if it is more than a fix, include tests, and expect a human to
read the diff. See [CONTRIBUTING.md](../CONTRIBUTING.md). The bar is the one this project was built
under, and the design notes are there so you can check that a change fits before writing it.
