# Contributing

Thanks for your interest! ConnectionWatcher is a small MIT-licensed project
maintained in spare time — bug reports with evidence and focused pull
requests are the most useful contributions.

## Getting set up

Everything about building, running from source, the frontend loop, and the
test suites is in [DEVELOPMENT.md](DEVELOPMENT.md). The short version:
.NET 10 SDK, Node 22+, `dotnet run --project Programs/ConWatch`.

## Bug reports

The best bug reports come with evidence: the transition or glitch rows from
the Data page's exports, a snapshot of the relevant History view, or the
`conwatch service status` output. "It said the internet was down and it
wasn't" is much easier to chase when the report says what the anchors and
path hops were doing at the time.

## Pull requests

- Changes should come with tests — the codebase is built test-first and the
  suites are the safety net that lets refactors land quickly. Engine tests
  use fake transceivers and a fake clock; if a test needs real sockets or
  real time, that's a smell worth mentioning in the PR.
- Keep PRs focused; separate refactors from behavior changes.
- Match the surrounding style (file-scoped namespaces, expression-bodied
  where the neighbors are, comments explain *why*, not *what*).
- Use `example.com` hostnames and RFC 5737 addresses (`203.0.113.x`,
  `198.51.100.x`) in tests and docs — never real endpoints.
- CI must be green.

## Where to learn how it works

[THEORY_OF_OPERATION.md](THEORY_OF_OPERATION.md) explains how the engine
makes decisions. Design specs and implementation history live in
`docs/superpowers/` — often the fastest way to learn why something is the
way it is. Releases are cut by the maintainer; the process is documented in
[DEVELOPMENT.md](DEVELOPMENT.md#releases).
