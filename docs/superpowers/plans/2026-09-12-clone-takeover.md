# Clone-install + master enable — implementation plan

Spec: ../specs/2026-09-12-clone-takeover-design.md

1. **Master enable core.** SettingKeys: `Engine.Enabled`, `Clone.SourceUrl`,
   `PerNodeKeys`. SettingsStore fires SettingsChanged + NotifierSettingsChanged
   on `Engine.*` writes. Snapshot export excludes per-node keys; importer
   preserves local per-node keys and skips imported ones. WatcherHostService:
   disabled → no engine, recorders/collector flushed. Mqtt/Heartbeat notifier
   loops gate on the key (MQTT session end publishes LWT offline). `/health`
   → 200 "Disabled"; `/status` → 200 archive shape `{enabled:false,...}`.
   Admin "Monitoring" toggle card; dashboard archive banner (React) + hidden
   live tree. Tests: toggle round-trip, archive surfaces, per-node exclusion.
2. **Full snapshot endpoint.** Shared snapshot builder with
   `includeSecrets`; `POST /api/v1/export/snapshot/full {password}` with
   inline AdminGate verify (open while gate disabled). Surface lock: covered
   by the `/api/v1/export` open prefix; add to SkipProbe (401-on-bad-creds).
   Tests: secrets present, redactedSettings empty, 401 wrong password,
   sanitized variant unchanged.
3. **M2M engine endpoint.** `POST /api/v1/admin/engine {enabled, password}`,
   open route with inline verify; applies the setting and rebuilds
   immediately (no debounce) so a snapshot taken right after disable is
   complete. Surface lock: explicit open prefix + SkipProbe.
4. **Clone.** Pure `TransportPolicy` (https / http+loopback allowed;
   http+LAN with secrets or password needs `allowInsecure`).
   `POST /api/v1/setup/clone {sourceUrl, sourcePassword, includeSecrets,
   allowInsecure}`: policy → fetch via IHttpClientFactory("clone") → import →
   `Engine.Enabled=false` + `Clone.SourceUrl`. Wizard third option UI +
   insecure-transport warning flow (use https / continue anyway / clone
   without secrets). Tests: policy matrix; two-factory clone via routed
   handler; refusal without override.
5. **Take-over.** Admin "Migration" card (visible when Clone.SourceUrl set):
   `POST /api/v1/setup/takeover {sourcePassword}` → remote-disable source →
   final full snapshot → import-replace → enable locally (warm restart seeds
   from the imported data). Precise error mapping per step. Tests:
   two-factory take-over incl. rows recorded between clone and take-over;
   wrong password leaves the clone untouched.
6. **Both-active warning.** ConWatch-side `CloneHealth` probe (cached,
   short timeout) merged into `/config/health`. Test via routed handler.
7. **Finish.** README migration section, BACKLOG check-off, full suites,
   client build, merge to master (never push), fresh Synology tar.
