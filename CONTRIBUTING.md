# Contributing

Thanks for your interest! ConnectionWatcher is a small MIT-licensed project
maintained in spare time — bug reports with evidence and focused pull
requests are the most useful contributions.

## Dev setup

- **.NET 10 SDK** and **Node 22+** (the web UI is Vite + React islands).
- Run the server: `dotnet run --project Programs/ConWatch` (UI at
  `http://localhost:8080`, SQLite DB in `./data`).
- Frontend loop: `npm run watch` in `Programs/ConWatch/ClientApp` beside the
  running server.
- `ConTray` (the tray companion) is Windows-only; everything else is
  cross-platform.

## Tests

Changes should come with tests — the codebase is built test-first and the
suites are the safety net that lets refactors land quickly.

```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
cd Programs/ConWatch/ClientApp && npx vitest run
```

The ConWatch suite drives the real app over HTTP with a fake ping
transceiver; engine tests use a fake clock. If a test needs real sockets or
real time, that's a smell worth mentioning in the PR.

## Pull requests

- Keep them focused; separate refactors from behavior changes.
- Match the surrounding style (file-scoped namespaces, expression-bodied
  where the neighbors are, comments explain *why*, not *what*).
- Use `example.com` hostnames and RFC 5737 addresses (`203.0.113.x`,
  `198.51.100.x`) in tests and docs — never real endpoints.
- CI must be green (one historically flaky notifier test class runs
  non-blocking; everything else is required).

Design notes and implementation history live in `docs/superpowers/` — often
the fastest way to learn why something is the way it is.
