# ConnectionWatcher Phase 4b — MQTT + Webhooks (Home Assistant)

**Date:** 2026-08-24
**Status:** Approved design, pending implementation plan

## Purpose

Push ConnectionWatcher's state into the home-automation world: Home Assistant
entities via MQTT discovery (enabling automations like verdict-gated modem
power-cycling) and generic HTTP webhooks for everything else.

## Decisions

| Topic | Decision |
|---|---|
| Structure | New `ConNotify` class library (isolates the MQTTnet dependency): `MqttNotifier` + `WebhookNotifier` BackgroundServices, attached to engine streams by WatcherHostService like the recorder (rebuild-safe). |
| Settings | DB settings table, Admin-editable: `Mqtt.Enabled`, `Mqtt.Host`, `Mqtt.Port` (1883), `Mqtt.Username`, `Mqtt.Password`, `Mqtt.BaseTopic` (`conwatch`), `Mqtt.DiscoveryPrefix` (`homeassistant`); `Webhook.Enabled`, `Webhook.Urls` (one per line), `Webhook.VerdictOnly` (false). New `NotifierSettingsChanged` signal on `Mqtt.*`/`Webhook.*` writes → notifiers reconfigure live. Password is plaintext in SQLite — accepted LAN-appliance tradeoff, documented. |
| HA entities | One "ConnectionWatcher" device (manufacturer DSoftCo): verdict **sensor** (state + evidence attributes), **internet binary_sensor** (`ON` unless verdict `LinkDown` — weather counts as up-but-degraded), one connectivity **binary_sensor per target** (`ON` = Responding/Faltering/Recovering, `OFF` = Down/Unreachable). |
| Topics | `{base}/availability` (LWT, retained), `{base}/verdict/state` + `/attributes` (retained), `{base}/internet/state` (retained), `{base}/target/{slug}/state` (retained), `{base}/transitions` (JSON event stream, not retained). Discovery configs retained under `{prefix}/…`. |
| Publish cadence | On transitions + full refresh on connect and engine rebuild. Never per-ping. |
| Entity lifecycle | Discovery republished on connect/rebuild; targets removed mid-session get empty retained config (cleanup). Known limitation: deletion while disconnected can strand an HA entity. |
| Webhooks | POST JSON per transition (subject, from/to, trigger, verdict, detail, evidence) to each URL; 5 s timeout, one retry, bounded queue, failure counter in logs. `VerdictOnly` filter. |
| Admin UI | New "Notifications" section: MQTT fields + enabled + **Test** button (`POST /api/v1/admin/mqtt-test` — connect + publish test message, inline result); webhook URLs textarea + toggles. Settings validation extended. |
| Testing | Unit: slug/topic builders, discovery document shape, state mappings (incl. ON-unless-LinkDown), webhook payload, settings validation. Integration: throwaway **Mosquitto container** (docker CLI fixture; self-skips loudly without Docker) — discovery + retained states + transition events + availability LWT verified end-to-end with fake pings; webhooks against an in-process listener. |
| Out of scope | HA-side automation authoring (user's side); TLS to the broker (backlog if ever needed); toast/MQTT unification. |

## Slug rule

Target names → MQTT/HA-safe slugs: lowercase, alphanumerics kept, everything
else collapsed to `_`, trimmed; collisions get numeric suffixes. Slugs are
derived from the stable target GUID + name at publish time; the GUID is the HA
`unique_id` so renames keep entity history in HA.
