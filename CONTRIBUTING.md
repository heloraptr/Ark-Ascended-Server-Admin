# Contributing

This is a personal project I run for my own servers. Issues are read; fixes and features land when I
have the time and bandwidth. No schedule, no guarantees.

## Issues

Issues are welcome. Use the templates: a bug report asks for the app version (shown on Settings),
the Windows version, the bind mode, an excerpt from the Windows event log, and the `/setup` console
text when the game install is involved. A feature request asks what you want to do and why the
current app cannot do it.

Before opening a feature request, read [What it does not do](README.md#what-it-does-not-do). Those
are decisions, not gaps. A request that changes the reasoning behind one of them is welcome; a
request that repeats the feature is closed with a link there.

## Pull requests

Pull requests are reviewed when I have the bandwidth, and then either merged or closed with a reason.
Open an issue first for anything larger than a fix, so the design is agreed before the code exists.
A pull request that arrives without that conversation may sit for a while, or be closed if it does not
fit.

A fix is: a bug, a typo, a wrong doc statement, a missing null check. Anything that adds a page, a
setting, a table, a dependency, or changes how instances are laid out on disk is larger than a fix.

For a pull request:

- Build with `dotnet build ArkAscendedServerAdmin.slnx -c Release` and run both test executables
  (see [Building and testing](README.md#building-and-testing)); `TreatWarningsAsErrors` is on.
- One change per pull request, one imperative sentence per commit message, ending with a period.
- No `Co-Authored-By` or tool-attribution trailers.
- American English in code, comments, and docs.

## Where things are

| | |
|---|---|
| Issues | On, with templates. |
| Discussions | Off. Use an issue. |
| Wiki | Off. Docs live in `docs/` and are versioned with the code. |
| Security | See [SECURITY.md](SECURITY.md); do not open a public issue for a vulnerability. |
