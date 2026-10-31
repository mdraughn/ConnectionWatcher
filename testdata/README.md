Local-only real-data snapshots. NEVER commit snapshots here: they describe the home network topology.

To refresh: run ./tools/Get-NasSnapshot.ps1 (fetches the open, secret-sanitized /api/v1/export/snapshot endpoint from the NAS). RealDataSnapshotTests picks up nas-snapshot.json automatically and skips when it is absent.
