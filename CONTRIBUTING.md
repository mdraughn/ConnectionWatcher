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
- `Programs/ConWatchCli` is the `conwatch` command (help, `serve`, service,
  config, tray); `Programs/ConWatchTray` is the tray companion and is
  Windows-only. Run the command from source with
  `dotnet run --project Programs/ConWatchCli -- --help`. Everything else is
  cross-platform.

## Tests

Changes should come with tests — the codebase is built test-first and the
suites are the safety net that lets refactors land quickly.

```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
dotnet test Tests/ConConfig.Tests -c Debug
dotnet test Tests/ConWatchCli.Tests -c Debug
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
- CI must be green.

## Releases

Two channels, one source:

- **edge** — every push to `master` publishes
  `ghcr.io/mdraughn/conwatch:edge` (and `sha-<commit>`). Binaries report
  `<version>-dev+<sha>`.
- **stable** — push a tag and everything else is automatic:

  ```bash
  git tag v1.2.3
  git push origin v1.2.3
  ```

  This publishes the image as `1.2.3`, `1.2`, `1` and moves `latest`,
  builds `ConnectionWatcher-Setup-1.2.3.exe` and `ConnectionWatcher-1.2.3-win-x64.zip` on a Windows runner (`Build-Installer.ps1`), and creates a GitHub Release with generated notes and both attached.
  A tag with a suffix (`v1.2.3-rc1`) becomes a GitHub *pre-release*, gets
  only its own image tag, and never moves `latest`.

The version lives in one place, `Directory.Build.props` (`VersionPrefix`);
bump it right after a release so dev builds announce the version they are
heading toward. Release builds are code-signed with Azure Artifact Signing:
`Build-Installer.ps1 -Sign` signs the project's exes and DLLs, the
uninstaller and the Setup exe (`deploy/windows/signing-metadata.json` names
the account and certificate profile; `deploy/windows/Get-SigningTools.ps1`
fetches signtool and the signing dlib). The workflow authenticates with a
federated OIDC credential on an app registration; a workstation needs
`az login` as a user who holds the Certificate Profile Signer role.

Design notes and implementation history live in `docs/superpowers/` — often
the fastest way to learn why something is the way it is.
