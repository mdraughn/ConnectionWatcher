# Development

How to build, run, and work on ConnectionWatcher from source. For
contribution guidelines (PR etiquette, what makes a good bug report), see
[CONTRIBUTING.md](CONTRIBUTING.md).

## Notice of AI development

This application started out as a hand-coded program that pinged several websites and reported their status.  It then went through a period of rapid-AI-driven growth on the way to becoming what it is today.
While not strictly vibe-coded, it is almost 100% AI-written code.

## Prerequisites

- **.NET 10 SDK**
- **Node 22+** (the web UI is Vite + React islands)

Everything except the Windows service/tray pieces is cross-platform.

## Running from source

```bash
dotnet run --project Programs/ConWatch
```

The UI comes up at `http://localhost:8080`. `dotnet publish` builds the
frontend bundles automatically.

- The SQLite database lives at `data/conwatch.db` (relative to the working
  directory; configurable via `Persistence:DbPath`). Schema migrations apply
  automatically at startup.
- Configuration (targets, profiles, pools) lives **in the database** and is
  edited through the web UI or the REST API under `/api/v1` — Swagger UI at
  `/swagger` in Development. On first boot, a `Watcher` section in
  appsettings.json (if present) seeds the config; after that the DB is the
  source of truth.
- Timestamps in query strings (`from`, `to`) are ISO-8601; an explicit
  offset is honored and **no offset means UTC**, never the server's local
  zone.
- The `conwatch` command can be run from source too:
  `dotnet run --project Programs/ConWatchCli -- --help`.

### Frontend dev loop

```bash
cd Programs/ConWatch/ClientApp
npm install
npm run watch     # rebuild islands on change, beside a running dotnet server
npm test          # Vitest suite
```

## Project layout

| Project | Purpose |
|---|---|
| `Programs/ConWatch` | The web host: monitoring engine + SQLite persistence + REST API + UI (what the container runs) |
| `Programs/ConWatchCli` | `conwatch.exe`: hosts the same app on Windows and manages the service, config and tray |
| `Programs/ConWatchTray` | `conwatch-tray.exe`, the Windows tray companion |
| `Core/ConEngine` | The engine: adaptive ping disciplines, reachability tree, pools, verdict layer. `Topology/`, `Verdict/` and `Diagnostics/` share `namespace ConEngine.Topology` by design (one namespace, three folders). |
| `Core/ConCore` | Contracts and domain primitives |
| `Libraries/ConConfig` | The install-time config files (`conwatch.json`, `tray.json`) and their precedence |
| `Libraries/ConInfrastructure` | Real ICMP/TCP/DNS probes, gateway discovery, traceroute, LAN scanning, tray logic |
| `Libraries/ConPersistence` | EF Core (SQLite) schema, config store, transition/rollup recording, retention |
| `Libraries/ConNotify` | MQTT (Home Assistant), webhook, and heartbeat notifiers |
| `Tests/*` | xUnit suites for the engine, persistence, config, the command, and the full app over HTTP |

How the engine actually decides things — states, verdicts, anchors,
glitches — is documented in
[THEORY_OF_OPERATION.md](THEORY_OF_OPERATION.md). Design specs and
implementation plans live in `docs/superpowers/` — the project's full
engineering history, often the fastest answer to "why is this the way
it is."

## Tests

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
real time, that's a smell. Tests wait on conditions, never sleep-then-assert
— CI runners have two cores and will flake out anything timing-sensitive.

## Database migrations

```bash
dotnet ef migrations add <Name> --project Libraries/ConPersistence
```

The library carries its own design-time factory, so no startup project is
needed (passing `--startup-project Programs/ConWatch` fails because the
design package is private to the library).

## Building the deliverables

- **Docker image:** `Build-Images.ps1` builds the same multi-arch image
  (amd64 + arm64 via cross-compilation) that CI publishes to
  `ghcr.io/mdraughn/conwatch`.
- **Windows installer and zip:** `Build-Installer.ps1` publishes the
  self-contained binaries, builds the Inno Setup installer, and packages the
  zip. `-Sign` code-signs the exes, DLLs, uninstaller, and Setup exe with
  Azure Artifact Signing (`deploy/windows/signing-metadata.json` names the
  account and certificate profile; `deploy/windows/Get-SigningTools.ps1`
  fetches signtool and the signing dlib). CI authenticates with a federated
  OIDC credential; a workstation needs `az login` as a user who holds the
  Certificate Profile Signer role.

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

  This publishes the image as `1.2.3`, `1.2`, `1` and moves `latest`, builds
  `ConnectionWatcher-Setup-1.2.3.exe` and
  `ConnectionWatcher-1.2.3-win-x64.zip` on a Windows runner
  (`Build-Installer.ps1`), and creates a GitHub Release with generated notes
  and both attached. A tag with a suffix (`v1.2.3-rc1`) becomes a GitHub
  *pre-release*, gets only its own image tag, and never moves `latest`.

The version lives in one place, `Directory.Build.props` (`VersionPrefix`);
bump it right after a release so dev builds announce the version they are
heading toward.
