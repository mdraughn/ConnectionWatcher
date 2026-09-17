# Release Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two release channels from one source: `edge` (every master push, Mark's testing) and stable (a `vX.Y.Z` tag, which publishes semver image tags, moves `latest`, builds the Windows installer, and creates a GitHub Release), with the version number flowing from the tag into the image, the binary's header/health, and the installer file name.

**Architecture:** Version is a single MSBuild property (`Directory.Build.props`: `VersionPrefix` + `VersionSuffix=dev`) so every local/edge build reads `X.Y.Z-dev+sha` and every release build gets `/p:Version=<tag>`. `publish-image.yml` keeps handling images for both channels; a new `release.yml` runs on tags only: builds the installer on a Windows runner and attaches it to a GitHub Release (pre-release when the tag has a `-` suffix). No signing yet (next phase).

**Tech Stack:** GitHub Actions (docker/metadata-action, docker/build-push-action, softprops/action-gh-release), Inno Setup 6, .NET 10 SDK, PowerShell.

**Spec:** the conversation of 2026-09-17 (channels, tag conventions, pre-releases approved).

## Global Constraints

- Never push branches or tags to origin without Mark's explicit instruction. The pipeline can only be exercised end to end by pushing a tag, so this plan verifies locally what it can (YAML validity, installer build, version stamping) and leaves the first tag to Mark.
- `latest` must mean the newest stable release and nothing else.
- Image name stays `ghcr.io/mdraughn/conwatch`. Installer name stays `ConnectionWatcher-Setup-<version>.exe`.
- The tag format is `vMAJOR.MINOR.PATCH` with an optional `-suffix` (e.g. `v1.0.0-rc1`) for pre-releases.

---

### Task 1: One version source (`Directory.Build.props`)

**Files:**
- Create: `Directory.Build.props`
- Modify: `Programs/ConWatch/ConWatch.csproj:22-25` (remove `<Version>`), `Build-Installer.ps1` (default `-Version` from the props; pass `/p:Version` to both publishes), `Dockerfile` (new `APP_VERSION` build arg)
- Test: `Tests/ConWatch.Tests/BuildInfoTests.cs`

- [ ] **Step 1: Failing test — dev builds carry the `-dev` suffix**

```csharp
[Fact]
public void Dev_builds_are_marked_as_such()
{
    // Local and edge builds must never impersonate a release: the props file
    // sets VersionSuffix=dev, and only a release build overrides Version.
    Assert.Contains("-dev", BuildInfo.ShortVersion);
}
```

Run: `dotnet test Tests/ConWatch.Tests --filter BuildInfoTests` → FAIL (version is `0.9.0`).

- [ ] **Step 2: Create `Directory.Build.props`**

```xml
<Project>
  <!-- The ONE place the version lives. Local and edge builds read
       "<prefix>-dev+<sha>"; a release build passes /p:Version=<tag> (which
       overrides prefix and suffix) from the tag that triggered it. -->
  <PropertyGroup>
    <VersionPrefix>0.9.0</VersionPrefix>
    <VersionSuffix>dev</VersionSuffix>
    <Company>DSoftCo</Company>
    <Product>ConnectionWatcher</Product>
  </PropertyGroup>
</Project>
```

Remove `<Version>0.9.0</Version>` and its comment from `ConWatch.csproj` (keep `Company`/`Product` there or drop them; the props now supply both).

- [ ] **Step 3: Dockerfile takes `APP_VERSION`**

After `ARG GIT_SHA=` add `ARG APP_VERSION=` and extend the publish line with `${APP_VERSION:+/p:Version=$APP_VERSION}`.

- [ ] **Step 4: `Build-Installer.ps1` stamps the binaries**

Default `-Version` to the props value (`<VersionPrefix>-<VersionSuffix>`, read with a regex), and add `/p:Version=$Version` to both `dotnet publish` calls so the installed service reports the same version the installer is named with.

- [ ] **Step 5: Run the test, then the full web suite; commit**

`git commit -m "build: one version source (Directory.Build.props); dev builds are X.Y.Z-dev"`

### Task 2: Image channels (`publish-image.yml`)

**Files:**
- Modify: `.github/workflows/publish-image.yml`

- [ ] **Step 1: Tag rules**

Replace the `tags:` block with:

```yaml
tags: |
  type=raw,value=edge,enable={{is_default_branch}}
  type=semver,pattern={{version}}
  type=semver,pattern={{major}}.{{minor}}
  type=semver,pattern={{major}}
  type=sha
```

`flavor: latest=auto` (the default) adds `latest` only for non-pre-release semver tags, and the major/minor patterns are skipped for pre-releases automatically.

- [ ] **Step 2: Version build-arg from the tag**

Add a step before `meta`:

```yaml
- id: ver
  run: |
    if [[ "$GITHUB_REF" == refs/tags/v* ]]; then echo "version=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"; else echo "version=" >> "$GITHUB_OUTPUT"; fi
```

and pass `build-args: |` `GIT_SHA=${{ github.sha }}` and `APP_VERSION=${{ steps.ver.outputs.version }}`.

- [ ] **Step 3: Validate the YAML** (`python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/publish-image.yml'))"` or `gh workflow view`), commit: `ci: edge channel on master pushes; latest and semver tags only from v* tags`.

### Task 3: Release workflow (`release.yml`)

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Write the workflow**

```yaml
name: release
on:
  push:
    tags: ['v*']
permissions:
  contents: write
jobs:
  installer:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - uses: actions/setup-node@v4
        with: { node-version: 22 }
      - name: Ensure Inno Setup 6
        shell: pwsh
        run: if (-not (Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")) { choco install innosetup -y --no-progress }
      - id: ver
        shell: pwsh
        run: '"version=$($env:GITHUB_REF_NAME.TrimStart(''v''))" >> $env:GITHUB_OUTPUT'
      - shell: pwsh
        run: .\Build-Installer.ps1 -Version ${{ steps.ver.outputs.version }}
      - uses: actions/upload-artifact@v4
        with: { name: installer, path: dist/ConnectionWatcher-Setup-*.exe }
  release:
    needs: installer
    runs-on: ubuntu-latest
    steps:
      - uses: actions/download-artifact@v4
        with: { name: installer, path: dist }
      - id: ver
        run: echo "version=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"
      - uses: softprops/action-gh-release@v2
        with:
          files: dist/ConnectionWatcher-Setup-*.exe
          prerelease: ${{ contains(steps.ver.outputs.version, '-') }}
          generate_release_notes: true
          body: |
            **Container:** `docker pull ghcr.io/mdraughn/conwatch:${{ steps.ver.outputs.version }}`
            **Windows:** `ConnectionWatcher-Setup-${{ steps.ver.outputs.version }}.exe` below (unsigned: SmartScreen will ask you to confirm).
```

- [ ] **Step 2: Validate YAML; commit** `ci: release workflow builds the Windows installer and publishes a GitHub Release on v* tags`.

### Task 4: Local scripts and docs

**Files:**
- Modify: `Build-Images.ps1` (stop stamping `:latest` on `-Push`; that is the release's job), `README.md` (quick start: `latest` = stable, `edge` = newest master build; a "Releases" paragraph), `CONTRIBUTING.md` (how to cut a release: tag `vX.Y.Z`, `-rc1` for pre-release; bump `VersionPrefix` after a release), `docs/superpowers/BACKLOG.md` (release pipeline DONE).

- [ ] **Step 1: Edit; run the full suite; commit** `docs: two release channels (edge / latest), how to cut a release`.

### Task 5: Local proof

- [ ] Build the installer locally with `.\Build-Installer.ps1 -Version 0.9.0-local` (Inno Setup present?) and confirm `dist\ConnectionWatcher-Setup-0.9.0-local.exe` exists and the staged `ConWatch.dll` reports `0.9.0-local` in its file version.
- [ ] `docker build --build-arg APP_VERSION=0.9.0-local .` and check `/api/v1/health` reports `0.9.0-local`.
- [ ] Both workflow files parse.

What cannot be verified without a push: the GitHub Release creation and the GHCR tags. First real check is Mark tagging (a `-rc1` pre-release is the safe first try).
