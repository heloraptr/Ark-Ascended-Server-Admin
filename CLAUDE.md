# Working in this repository

This file is read by Claude Code at the start of a session. It holds the project rules a
contributor needs regardless of tooling; [docs/how-this-was-built.md](docs/how-this-was-built.md)
explains why it exists and how the project was made. Design notes, plans, and review logs are in
[docs/design/](docs/design/README.md); read the relevant one before proposing a change to how
something works.

## Build and test

- .NET 10 SDK per `global.json`. Package versions are central (`Directory.Packages.props`); add or
  change packages with `dotnet add package`, not by editing version numbers by hand.
- The test projects are Microsoft.Testing.Platform executables. Run them with
  `dotnet run --project <test csproj>`, never `dotnet test`, which reports "Zero tests ran" on this
  SDK and xunit.v3 combination. Exact commands are in the README under "Building and testing".
- Warnings are errors. `CS1573` bites on positional records: a `<param>` tag needs every
  parameter, so document them in `<summary>` with `<paramref>` instead.
- `Infrastructure`, `Server`, and the integration tests are Windows-only at run time.

## Branches, pull requests, releases

- Every change goes on a branch and arrives on `main` through a pull request. The owner reviews
  and squash-merges; do not merge, and do not commit directly to `main`.
- Never create or push a git tag. Versions come from tags via MinVer, and the owner tags releases.
- Patches for a released minor come from `rel/vX.Y`, created from the last `vX.Y.N` tag when the
  first patch is needed and kept afterwards. PRs into `rel/vX.Y` are squash-merged like any other.
  The PR from `rel/vX.Y` back into `main` is merged with a merge commit, never squashed: a squash
  leaves the merge base behind, and the next back-merge conflicts on changes that already landed.
- Commit messages and pull request descriptions carry no AI attribution trailers (no
  `Co-Authored-By`, no "Generated with" line). The tooling is disclosed once, in
  `docs/how-this-was-built.md`, and the commit carries the name of the person accountable for it.
- Anything larger than a fix gets a short plan first, written to `docs/design/` in the style of
  the existing ones, and the plan is agreed before implementation starts.

## Database migrations

- One open branch carrying an EF Core migration at a time. Check open pull requests before adding
  a migration. If two overlap, the second rebases onto `main`, runs `dotnet ef migrations remove`
  and then `migrations add` again so the designer file and the snapshot come from the merged model.
  Never hand-merge `AppDbContextModelSnapshot.cs`.
- CI runs `dotnet ef migrations has-pending-model-changes` after the build; a stale snapshot fails
  the pull request.
- Migrations are additive and are never collapsed or rewritten once merged.
- EF trap seen here: deleting and re-adding a row with the same key in one `SaveChanges` is folded
  into an `UPDATE` and can drop a changed boolean. Update rows in place.

## UI changes

- Run the app from the repo and look at the page before opening a pull request; a screenshot in
  the PR is expected for anything visible. "Running from the repo" in the README has the setup.
- Captions go above pictures in the docs, not below.

## Writing

- American English.
- Plain register, written for the person at the keyboard. Do not carry names from the code into
  user-facing text (a component called `Rail` is "the sidebar" to a reader), and do not give every
  page the same section headings. Each page is shaped by what the reader needs on it.
- No marketing voice, no "Mental model" sections, no emoji.

## Never

- Read, add, or change `dotnet user-secrets`.
- Commit anything under `data/`, an `appsettings.Production.json`, a `.db` file, or a CurseForge
  API key.
- Touch a running production service or its data root from a session. Deployment is the owner's.
