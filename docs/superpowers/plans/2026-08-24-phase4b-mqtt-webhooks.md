# Phase 4b MQTT + Webhooks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the Phase 4b spec (`docs/superpowers/specs/2026-08-24-phase4b-mqtt-webhooks-design.md`).

**Global constraints:** never push; branch `phase4b`; all suites stay green; MQTTnet is referenced ONLY by ConNotify; setting keys/topics/mappings exactly per spec; TDD per task.

### Task 1: ConNotify library + pure builders (unit-tested)
Create `ConNotify/ConNotify.csproj` (net10.0, MQTTnet + Microsoft.Extensions.Hosting.Abstractions + ref ConEngine/ConPersistence), `Tests/ConNotify.Tests` (xunit + refs). Pure classes: `Slugger` (spec slug rule + collision suffixes), `MqttTopics` (all topic builders from base/prefix), `HaDiscovery` (JSON documents for verdict sensor / internet binary / target binary, device block, GUID unique_ids, availability topic wired), `StateMapping` (target state → ON/OFF, verdict → internet ON/OFF, verdict sensor state string), `WebhookPayload` (TargetTransition → JSON). Tests for each incl. slug collisions, LinkDown→OFF, Faltering→ON, discovery shape (`state_topic`, `unique_id`, `device.identifiers`, `availability_topic`).

### Task 2: Settings plumbing
`SettingsStore`: add `NotifierSettingsChanged` event raised on `Mqtt.*`/`Webhook.*` writes (Verdict.* behavior unchanged). `SettingsEndpoints`: accept/validate new keys (`Mqtt.Port` 1-65535 int; `Mqtt.Enabled`/`Webhook.Enabled`/`Webhook.VerdictOnly` true/false; free-text host/user/pass/topics; `Webhook.Urls` each line parses as absolute http(s) URI). Tests: signal fires for Mqtt.* not Retention.*; PUT validation accept/reject cases.

### Task 3: Notifier services + wiring
`MqttNotifier` (BackgroundService): reads settings; when enabled, MQTTnet managed client with LWT (`availability`→`offline`, retained), on-connect publishes `online`, discovery set, full state refresh; `Attach(WatcherEngine)` subscribes TransitionSource → per-transition state publishes + `transitions` event JSON; rebuild → rediscovery + refresh + clear discovery for vanished targets (tracked set); `NotifierSettingsChanged` → teardown/reconnect. `WebhookNotifier` (BackgroundService): bounded channel, per-transition POST to each URL (5 s timeout, 1 retry, VerdictOnly filter), failure counter logging. ConWatch DI + `WatcherHostService` attaches both alongside recorder/collector. All suites stay green with notifiers disabled by default.

### Task 4: Admin UI + mqtt-test
Admin page "Notifications" section (MQTT fields, enabled, webhook textarea + toggles) submitting through the existing settings form pipeline; `POST /api/v1/admin/mqtt-test` connects with current form values (request body override) and publishes `{base}/test`, returning ok/error message; Test button wired in site-admin.js. Page smoke + endpoint test (failure path against a dead port; success path covered in Task 5).

### Task 5: Integration tests
`MosquittoFixture` (ConNotify.Tests or ConWatch.Tests): `docker run -d -p 127.0.0.1:0->1883 eclipse-mosquitto` with anonymous config (`docker exec` write conf or mount temp dir), resolve mapped port, dispose removes container; if `docker info` fails → fixture sets Skip flag and every dependent test early-returns with a logged SKIPPED line. End-to-end (ConWatchFactory + fake pings + MQTTnet client subscriber): enable MQTT via settings → observe availability online, retained discovery docs, internet/verdict/target states; force outage → target OFF + transition event + verdict change; disable → offline. Webhook test: in-process listener endpoint records posts; transition triggers delivery; VerdictOnly filters.

### Task 6: Wrap-up
README notifications section (incl. HA hookup steps + smart-plug automation sketch); backlog updates (mark MQTT done; webhooks done); full suites + Vitest; finishing-a-development-branch.

## Self-review: spec table maps 1:1 to tasks; pure logic all under test; container-dependent tests self-skip; no other project takes the MQTTnet dependency.
