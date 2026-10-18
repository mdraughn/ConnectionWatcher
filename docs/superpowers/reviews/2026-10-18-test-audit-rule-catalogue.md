# Rule catalogue: engine and configuration (appendix to the 2026-10-18 test audit)

Companion to `2026-10-18-test-coverage-audit.md`. Commit `b847038`.

This is the design knowledge the engine and configuration code carries, written as one plain rule per line. Next to each rule is the test that would go red if that rule were removed or inverted. It was harvested from the specs, the plans' rulings and the code's own comments. Later documents supersede earlier ones, and the code decides current behaviour.

**Status:**
- **Pinned**: a named test fails if the rule goes.
- **Partly**: some branch, boundary or the wiring is open.
- **Unpinned**: nothing would notice.
- **Superseded / Parked**: no longer current. Listed so nobody "restores" it during the refactor.

Use it two ways during the refactor:
1. Before touching a file, read its rules here. Any rule marked Partly or Unpinned should get its test (see the fix plan) first.
2. If a refactor makes a Pinned rule's test fail, that is the net working. Don't edit the test to match. Stop and decide whether the behaviour change is wanted.

Ids are prefixed by area: **EV** verdict, **ET** tracking, **PR** probes, **CF** configuration. Line numbers are at `b847038` and will drift.


## Engine: verdict, topology, model check, DNS verdict

| Id | Rule | Source | Test(s) asserting it | Status |
|---|---|---|---|---|
| EV-R1 | "Answering" = Responding/Faltering/Recovering; Down/Unreachable not; Initializing/Starting undetermined | TopologyCoordinator.cs:49; VerdictCoordinator.cs:381-383; plan 10-09 Global | Recovering_anchor_still_counts_degraded, A_descendant_reproving_while_the_root_is_still_down… | Partly (a lone Faltering anchor proving reachability is not asserted) |
| EV-R2 | No anchor determined (counted or ISP-owned) → Unknown "No anchors determined yet", **uncapped** | VerdictCoordinator.cs:397-399; spec 10-09 §4.2 row 1 | Starts_unknown_then_healthy (initial state only) | Partly → EV4 |
| EV-R3 | One answering counted anchor proves the internet up (Mark 2026-10-09) | VC:402; spec D2 | One_off_net_anchor_answering_proves_the_internet_is_up; With_the_isp_unknown_one_answering_anchor_proves_it_up | Pinned |
| EV-R4 | ISP = operator tags on path hops, trimmed + case-insensitive; an anchor with that tag never proves up | VC:170-175; spec §4.1 | An_isp_owned_anchor_never_proves_the_internet_is_up (" comcast") | Pinned |
| EV-R5 | No tag on the path → no anchor is ISP-owned, every anchor counts | VC:171-175 | With_the_isp_unknown_*; Untagged_anchors_are_not_the_isp_fallback | Pinned |
| EV-R6 | Every anchor ISP-owned → all count as fallback; UsingIspFallback true | VC:176-183; decision (a) | When_every_anchor_is_isp_owned_they_count_as_a_fallback | Pinned |
| EV-R7 | An anchor nested under another anchor is not a path hop (never names the ISP, never blamed) | VC:127-145 (T4 M1) | A_tagged_anchor_nested_under_another_anchor_does_not_make_its_operator_the_isp | Pinned |
| EV-R8 | None answering + an undetermined counted anchor → Unknown "Waiting for anchors to report", at most AnchorWaitCap | VC:420-421; spec §4.2 | A_cold_start_whose_first_anchor_reports_down_waits…; An_anchor_stuck_undetermined_does_not_hide_link_down_when_the_isp_is_known / _unknown | Pinned |
| EV-R9 | An ISP-owned anchor determined Down is evidence (starts the capped wait) | VC:413-415 | An_off_net_anchor_stuck_undetermined_does_not_hide_link_down_for_longer_than_the_cap | Pinned |
| EV-R10 | Only undetermined **counted** anchors hold the wait (a stuck ISP-owned anchor does not) | VC:420 | — | Unpinned (Low, folded into EV4) |
| EV-R11 | The cap timer is authoritative even if the clock reads a tick short | VC:352-370 | A_wait_cap_timer_that_fires_early_still_ends_the_wait | Pinned |
| EV-R12 | The cap reclassifies with no monitor transition | VC:342-346 | An_anchor_stuck_undetermined_does_not_hide_link_down_when_the_isp_is_known | Pinned |
| EV-R13 | The wait ends (clock reset) when its condition clears | VC:256-257 | A_cold_start_whose_first_anchor_reports_down_waits… (advance after clear) | Pinned |
| EV-R14 | One answering anchor never waits for a stuck one | spec §4.2 | One_answering_anchor_never_waits_for_a_stuck_one | Pinned |
| EV-R15 | All anchors determined, none answering → LinkDown at once | VC:420-424 | A_cold_start_with_every_anchor_down_is_link_down_without_waiting_for_the_cap | Pinned |
| EV-R16 | A Down/Faltering path hop with anchors answering never makes a Link* verdict by itself | spec §4.2; VC Quality | A_down_hop_alone_is_not_an_internet_outage; A_faltering_hop_with_healthy_anchors_is_still_healthy | Partly (SiteIncident + faltering hop open → EV8) |
| EV-R17 | LinkDown frontier blame: last hop with proven forwarding, blame the next one | VC:542-580; spec §4.3 | Blame_skips_a_hop_that_traffic_passes…; Link_verdicts_without_an_operator_keep_plain_wording | Pinned |
| EV-R18 | Every path hop proven → "nothing answers beyond {last hop}" with its operator | VC:576-577 | An_isp_owned_anchor_never_proves…; A_cold_start_with_every_anchor_down… | Pinned |
| EV-R19 | Across anchors with **different paths** the shallowest blame index wins | VC:566-571; spec §7 "branching anchor paths" | — | Unpinned → EV5 |
| EV-R20 | A path-less (root-hung) anchor never outranks one with a path; all path-less → "no anchor is answering" | VC:557-579 (T4 M2) | An_anchor_without_a_path_never_outranks_one_with_a_path_for_blame; Anchors_hung_directly_off_the_root_with_none_answering_say_so | Pinned |
| EV-R21 | Link* wording "{Verdict}: {Op} — {Hop} is {State}" / no op; Operator null when untagged | VC:583-588; plan 10-09 Global | Link_verdicts_name_the_operator…; …without_an_operator…; A_faltering_modem_during_weather_is_blamed_with_its_operator | Pinned |
| EV-R22 | LinkDown + every system resolver Down → suffix " — name lookups are failing too", never DnsDown | VC:425-430; spec 10-10 §4.4 | Anchors_down_with_resolvers_down_is_LinkDown_with_the_dns_suffix_never_DnsDown; DnsDown_then_anchors_down_is_LinkDown_with_the_suffix | Pinned |
| EV-R23 | DnsDown outranks Healthy, SiteIncident, InternetWeather, LinkDegraded (DNS checked before Quality) | VC:405-408; plan 10-14 Global | Anchors_up_and_every_system_resolver_down… (vs Healthy only) | Partly → EV3 |
| EV-R24 | Degraded anchor = Down, Faltering or Recovering (Unreachable is neither degraded nor answering) | VC:491-492 | Recovering_anchor_still_counts_degraded | Partly → EV6, EV8 |
| EV-R25 | ISP-owned anchors still count for SiteIncident/weather quality | spec §4.1 "On-net anchors" | — | Unpinned → EV8 |
| EV-R26 | Weather counts independent operators (same tag once, trimmed/case-insensitive; untagged each) | VC:495-500, 591-595 | Three_anchors_of_one_operator_down_is_still_a_site_incident; A_second_operator_degraded_makes_it_weather | Pinned |
| EV-R27 | Degraded units < threshold → SiteIncident ("— all {Op}" when one shared operator); ≥ threshold → weather | VC:508-515 | One_anchor_down_is_site_incident; Two_anchors_down_is_internet_weather; Three_anchors_of_one_operator… | Pinned (at threshold 2) |
| EV-R28 | Fewer independent determined anchors than the threshold → "(only N … cannot distinguish…)" qualifier | VC:502-506 | Below_threshold_anchor_count_carries_limited_confidence_qualifier; At_or_above_threshold_has_no_qualifier | Pinned |
| EV-R29 | Weather + a losing path hop → LinkDegraded blaming the **shallowest** hop that is Faltering | VC:517-534; plan loss-attr ruling 2026-10-12 | Loss_starting_at_the_router_blames_the_router_not_the_modem; Weather_shared_by_a_faltering_path_hop_is_link_degraded | Pinned |
| EV-R30 | …or Down with nothing beyond it answering | VC:528 | — | Unpinned → EV5 |
| EV-R31 | A bypassed Down hop is never blamed for weather (M1) | VC:520-521, 528 | A_bypassed_down_hop_is_never_blamed_for_weather | Pinned |
| EV-R32 | Cheap veto: a Faltering hop whose ProbeOnlyLoss note holds is never blamed; note raise/clear reclassifies with no state change; blamed until the note proves itself | VC:186-208, 527; plan loss-attr ruling 6 | VerdictBlameOrderTests (5 facts) | Pinned (unit); engine wiring Unpinned → EV2 |
| EV-R33 | DnsDown needs: reachable, watch on, ≥1 system resolver determined, every system resolver Down (Starting/Faltering/Recovering never Down) | VC:438-451; spec 10-10 §4.4 | Anchors_up_and_every_system_resolver_down…; One_system_resolver_still_answering_is_not_DnsDown; A_watch_that_is_off_never_produces_DnsDown | Partly → EV3 (≥1-determined guard, Faltering resolver) |
| EV-R34 | DnsDown blame: YourResolver (first Down resolver, first answering upstream) else NoResolver; advice when no upstream | VC:453-463 | three DnsDown blame tests + The_blame_names_the_first_resolver_that_is_Down… | Pinned |
| EV-R35 | Resolver wait shares the cap but has its own clock; a reachable internet never shows Unknown while waiting (ruling 2026-10-15) | VC:318-350, 450-451 | Undetermined_resolvers_wait_for_the_cap_then_conclude; An_expired_anchor_wait…; An_expired_resolver_wait…; A_resolver_recovering_during_the_wait_ends_it_cleanly | Pinned |
| EV-R36 | After the link returns, a Down resolver without a failure newer than the return is undetermined; a fresh success counts only within the cap after the return (C1) | VC:466-481 | Anchors_recovering_while…; Fresh_answers_from_a_resolver_still_marked_Down…; A_resolver_that_stays_dead…; A_resolver_with_no_fresh_evidence…; A_resolver_that_answers_now_and_then… | Pinned |
| EV-R37 | A seeded reachable verdict is not a return (no stale resolvers); a seeded LinkDown/Unknown is | VC:223-226 | A_warm_start_seeded_DnsDown_holds_without_a_false_recovery | Partly (LinkDown-seed half open, Low) |
| EV-R38 | Resolver results land in DnsResultLog before the monitor sees them | WatcherEngine.cs:305-309 | — (VerdictDnsDownTests mimic the order in their own helper) | Unpinned → EV10 |
| EV-R39 | Verdict leaving LinkDown bursts every Resolver monitor | WatcherEngine.cs:267-275 | A_LinkDown_to_reachable_verdict_transition_bursts_the_resolvers | Pinned |
| EV-R40 | Verdict→HostState map (Healthy/SiteIncident→Responding, Weather/LinkDegraded→Faltering, DnsDown/LinkDown→Down, Unknown→Starting) | VC:597-603 | Two_anchors_down_is_internet_weather; Label_change_with_same_mapped_state_still_emits; DnsDown first test | Partly → EV11 |
| EV-R41 | Same verdict → no transition, but Detail/Operator/Dns refresh in the snapshot | VC:259-262 | — | Unpinned → EV7 |
| EV-R42 | Verdict transitions: Kind Verdict, SubjectId/"Internet", VerdictChanged, Reason.Verdict, Reason.Dns only on DnsDown | VC:267-283 | Verdict_transitions_are_named_by_the_subject_constants; Starts_unknown_then_healthy; DnsDown first test | Pinned |
| EV-R43 | Snapshot is one immutable value; publish outside the lock (review E3) | VC:74-78, 284-316 | Current_is_one_snapshot_whose_detail_matches_its_state; Verdict_transitions_are_published_outside_the_verdict_lock | Pinned |
| EV-R44 | Warm seed adopted silently; Start reclassifies once ignoring Unknown; contradiction corrects once | VC:212-243 | Seeded_verdict_consistent…; Seeded_verdict_contradicted…; Seeded_verdict_outranks_unknown…; Wiring_the_veto_does_not_unseat… | Pinned |
| EV-R45 | Rebuild carries the non-Unknown verdict and determined monitor/pool states into the new engine; a target whose address or probe changed cold-starts | WarmStartSeeder.cs:129-153; WatcherEngine.cs:202-209, 261-265 | Probe_change_discards_the_seed (engine half only); FromEngine never called | Unpinned → EV1 |
| EV-R46 | Loss windows and the DNS result log are per engine (veto absent ~10 min after a rebuild) | plan loss-attr decision 4 / ruling 6 | Before_the_note_holds_the_faltering_hop_is_still_blamed | Partly (documented gap, adequate) |
| EV-R47 | Unreachable only on own Down + an ancestor Down without proven forwarding | TopologyCoordinator.cs:58-69; spec D4 §4.4 | A_dead_router_explains_every_descendant…; A_descendant_that_still_answers_is_never_unreachable…; Own_down_with_an_explanation_is_unreachable… | Pinned |
| EV-R48 | Root cause = shallowest such ancestor | TC:63-67 | A_dead_router_explains_every_descendant_that_proves_itself_down | Pinned |
| EV-R49 | A bypassed hop explains nothing | TC:65 | A_bypassed_hop_explains_nothing… | Pinned |
| EV-R50 | Only a **Down** ancestor can be a root cause (a Faltering one never) | TC:65 | — | Unpinned → EV12 |
| EV-R51 | A withdrawn explanation always releases to Starting with a burst (I1) | TC:99-112 | When_proof_of_forwarding_appears…; A_descendant_reproving_while_the_root_is_still_down…; Root_cause_recovery_releases… | Pinned |
| EV-R52 | A different non-null root re-pins silently (ExplainedBy changes, no transition) | TC:107-111; spec §4.4 | — | Unpinned → EV12 |
| EV-R53 | Released target's first transition out of Starting names the former cause | spec §4.4 | A_released_target_names_its_former_cause…; Only_the_first_transition_out_of_a_released_starting… | Pinned |
| EV-R54 | D5: a hop going Faltering/Down, or leaving Down/Faltering, bursts everything beyond it (not siblings) | TC:80-97 | A_hop_starting_to_fail_bursts…; A_hop_recovering_bursts…; A_hop_returning_from_faltering… | Pinned |
| EV-R55 | Topology Start settles seeded explanations once | TC:71-78 | TopologyWarmStartTests (2) | Pinned |
| EV-R56 | HopBypassed: Down hop with an answering descendant, at once; message names the nearest witness and probe | ModelCheck.cs:209-232 | The_2026_10_08_incident_raises_one_hop_bypassed_note_and_clears_it | Pinned |
| EV-R57 | Faltering hop → HopBypassed only after FalteringHold (2 min) | MC:233-239; plan model-warnings decision 1 | A_faltering_hop_is_a_note_only_after…; A_faltering_blip_shorter_than_the_hold… | Pinned |
| EV-R58 | NeverAnswered: zero accepted **and refused** replies, only after 5 min from engine start, no note before | MC:218-227; decision 2 | A_hop_that_never_answers_becomes_never_answered_after_the_wait; A_hop_that_answered_once… | Partly (refused-counts-as-answer open → EV14) |
| EV-R59 | Nothing beyond answering → real failure, no note | MC:211-213 | A_real_outage_beyond_a_hop_raises_no_note; A_hop_whose_descendants_go_down… | Pinned |
| EV-R60 | UnprovableUp exactly when the verdict uses the ISP fallback | MC:195-196; WatcherEngine.cs:281 | Unprovable_up_is_raised_at_start… (flag passed by hand) | Partly → EV2 |
| EV-R61 | ProbeOnlyLoss: last 10 attributable windows; raise avg probe-only ≥ 15 % **and** forwarding ≤ 2 %; clear when probe-only < 10 % **or** forwarding > 5 % | MC:259-283; spec §9.3; plan loss-attr Global | A_hop_dropping_only_its_own_pings…; Real_forwarding_loss_is_never_probe_only; The_note_clears_with_hysteresis… | Partly → EV9 |
| EV-R62 | A holding ProbeOnlyLoss suppresses the Faltering HopBypassed (one note) | MC:233-238 | A_faltering_hop_with_a_probe_only_note_is_not_also_bypassed | Pinned |
| EV-R63 | ProbeOnlyLoss never changes a target's state; advice depends on probe kind | spec §4.5; plan decision 5 | Probe_only_loss_never_changes_any_targets_state; An_icmp_hop_is_advised_to_try_tcp | Pinned |
| EV-R64 | Down-based resolver notes wait for a reachable internet; the engine defines reachable as "no verdict, or verdict not LinkDown/Unknown" | MC:314-328; WatcherEngine.cs:283 | A_resolver_down_during_an_outage_raises_no_Down_based_note (Func supplied by hand) | Partly → EV2 |
| EV-R65 | A condition publishes once on raise and once on clear; a standing one is silent | MC:244-254 | Repeated_evaluations_of_a_standing_condition_publish_nothing_new | Pinned |
| EV-R66 | Loss windows: UTC-minute aligned, close at end + 5 s, late samples dropped, usable ≥ 5 samples, keep 30 | LossWindows.cs | LossWindowsTests (6) | Pinned |
| EV-R67 | Only Verdict-scope samples enter loss windows and suspicion | WatcherEngine.cs:324-331; spec 10-10 R2 | Resolver_samples_stay_out_of_the_loss_windows…; LocalScopeEngineTests | Partly (suspicion exclusion not asserted, Low) |
| EV-R68 | Forwarding loss = min(own, lowest usable+answered witness loss) | LossAttribution.cs | LossAttributionTests (4) | Pinned |
| EV-R69 | Pool: all undetermined → Starting; any Responding → Responding; all Down/Unreachable → Down; else Faltering | PoolCoordinator.cs:187-202 | PoolCoordinatorTests | Partly (Unreachable-as-Down open, Low) |
| EV-R70 | Critical is carried by the graph and exposed by IsCriticalSubject; no engine coordinator reads it (critical never changes the verdict) | TargetGraph.cs; WatcherEngine.cs:162; plan critical-targets | IsCriticalSubject_is_true_only_for_critical_targets | Pinned (the "never read" half is structural: grep shows no coordinator touches Critical) |
| EV-R71 | A first loss on a previously healthy anchor/trunk target bursts every anchor and trunk hop | SuspicionCoordinator.cs:110-114 | Anchor_loss_sweeps_anchors_and_trunk_but_not_bystanders; Bystander_loss_does_not_sweep | Pinned |
| EV-R72 | ≥ threshold overlapping anchor glitches → one Internet glitch; trunk overlap changes wording | SC:195-242 | Correlated_anchor_glitches_emit_an_internet_glitch; Trunk_involvement_changes_the_wording; Single_anchor_glitch_stays_a_target_glitch | Pinned (10 s slack / 120 s prune boundaries open, Low) |
| EV-R73 | Spec'd design constants: AnchorWaitCap 30 s, FalteringHold 2 min, NeverAnsweredAfter 5 min, ProbeOnly 10 windows / 15 / 10 / 2 / 5 %, resolver window 10 / raise 3, NameMisses 3, DnsResultLog.Keep 20, WeatherThreshold default 2 | specs/plans as cited | tests reference the constants, so a changed value passes | Partly → EV13 |
| EV-S1 | "Down ancestor always outranks anchor evidence"; parking of descendants | spec 08-14 | — | Superseded (spec 10-09) |
| EV-S2 | ISP unknown needs ≥ min(2, independent anchors) answering | plan 10-09 Global | — | Superseded (Mark's one-anchor rule; pinned by R3/R5 tests) |
| EV-S3 | LinkDegraded from per-window forwarding-loss attribution ("deepest hop"); attribution floor; 3-window loss veto; "(forwarding loss NN %)" suffix | spec 10-09 §4.2/§4.5; plan loss-attr | — | Superseded (2026-10-12) |
| EV-S4 | Resolver wait shows Unknown "Waiting for resolvers to report" | plan 10-14 Global | — | Superseded (ruling 2026-10-15, R35) |
| EV-S5 | Paired-probe statistical veto (Plan D2) | spec 10-13 | — | Parked |
| EV-S6 | Before 5 min a never-answering hop shows HopBypassed | plan model-warnings decision 2 | code: no condition at all until the wait (MC:221-226), pinned by A_hop_that_never_answers… | Superseded by code |

## Engine: per-target tracking, detection, scheduling

| Id | Rule | Source | Test(s) asserting it | Status |
|---|---|---|---|---|
| ET-R1 | State is classified over the newest MinUsableSample samples, not the whole HistorySize buffer | HostTracker.cs:92 | SuspicionCoordinatorTests.Faltering_escalation_discards_the_episode, Adjacent_episodes_coalesce (only work if window=10 of 20) | Partly |
| ET-R2 | Starting exits only at MinUsableSample: >=Responding thr -> Responding, <Down thr -> Down, else Faltering | HostAnalysisEngine.cs:202, 256-259 | HostAnalysisEngineTests.Starting_exits_to_responding_on_good_sample / _to_down_ / _to_faltering_ (SampleSize==5 in reason pins "not earlier") | Pinned |
| ET-R3 | Responding -> Faltering when rate < FalteringThreshold; stays Responding between Faltering and Responding thresholds | HostAnalysisEngine.cs:204 | HostAnalysisEngineTests.Only_the_first_transition_out_of_a_released_starting...; hysteresis only via SuspicionCoordinatorTests.Two_loss_episode_records_glitch... (8/10 stays Responding) | Partly |
| ET-R4 | Faltering -> Down when rate < DownThreshold | :206 | HostAnalysisEngineTests.Responding_falls_to_faltering_then_down | Pinned |
| ET-R5 | Faltering -> Responding only at >= RespondingThreshold (not at >= Faltering) | :205 | none | Unpinned |
| ET-R6 | Down -> Recovering at >= RecoveringThreshold | :207 | HostAnalysisEngineTests.An_unreachable_host_that_answers_again... (explained Down only), TopologyCoordinatorTests:254 | Partly |
| ET-R7 | Recovering -> Responding at >= RespondingThreshold | :208 | WarmStartTests.Seeded_down_that_proves_alive_exits_via_recovering (end state) | Partly |
| ET-R8 | Recovering -> Down at < DownThreshold | :209 (branch partial in coverage) | none | Unpinned |
| ET-R9 | Threshold comparisons: ">=" to go up, "<" to go down, at exact boundary | :204-209, rate is float (:172) | none | Unpinned (latent defect, ET6) |
| ET-R10 | Explanation never makes a healthy host Unreachable; own Down + explanation shows Unreachable (ParentDown, root cause) | :37-46, 102-119 | HostAnalysisEngineTests.An_explanation_alone..., Own_down_with_an_explanation..., Explaining_an_already_down_host...; TargetMonitorTests.An_explained_monitor... | Pinned |
| ET-R11 | Withdrawing the explanation from Unreachable releases to Starting, clears history, ParentRecovered naming former cause | :102-139, HostTracker.cs:63-82 | HostAnalysisEngineTests.Withdrawing_the_explanation..., Release_returns_an_unreachable_host... | Pinned |
| ET-R12 | Only the first threshold transition out of a released Starting carries the former cause; a new explanation forgets it | :230-237, 106-107 | HostAnalysisEngineTests.Only_the_first_transition..., A_new_explanation_forgets... | Pinned |
| ET-R13 | Release/explain are idempotent; a different root re-pins silently; release of a non-Unreachable host keeps history | :104-112, 127 | HostAnalysisEngineTests.Release_and_repeated_explanations_are_idempotent, Release_of_a_host_that_is_not_unreachable... | Pinned |
| ET-R14 | Unreachable that answers again leaves through Recovering on own evidence | spec 10-09 §4.4 | HostAnalysisEngineTests.An_unreachable_host_that_answers_again... | Pinned |
| ET-R15 | A release bursts the target immediately (ForceBurst) | TargetMonitor.cs:308-309, 245 | TargetMonitorTests.Withdrawing_an_explanation_releases_the_monitor_and_bursts_it (pre-tracker path :245 unasserted) | Partly |
| ET-R16 | Topology's word before tracking is remembered and applied at StartTracking; null withdraws a seeded placeholder | TargetMonitor.cs:123-131, 184-194 | TargetMonitorTests (4 tests: An_explanation_given_before_tracking..., Seeded_unreachable_with_no_cause..., An_unseeded_monitor_told_null..., A_seeded_unreachable_awaiting_dns...) | Pinned |
| ET-R17 | Transitions published after the monitor lock is released, delivered in state order | TargetMonitor.cs:12-21, 328-338 | TargetMonitorTests.Transition_is_published_after_the_monitor_lock_is_released, Transitions_from_two_threads_arrive_in_state_order | Pinned |
| ET-R18 | Seed is silent; Assumed until MinUsableSample; no transition below it; one honest correction from the seeded state | HostAnalysisEngine.cs:76-92, 177-197 | WarmStartTests.Confirmed_seed_emits_no_transition, Seed_is_assumed_until..., Early_loss_does_not_flip..., Contradicted_seed_corrects_once... | Pinned |
| ET-R19 | Seeded Down (or Unreachable) that proves alive exits via Recovering | :191-195 | WarmStartTests.Seeded_down_that_proves_alive..., Seeded_unreachable_that_proves_alive... | Pinned |
| ET-R20 | Engine applies a target seed only when address AND probe still match config | WatcherEngine.cs:202-209 | WarmStartTests.Probe_change_discards_the_seed (negative only; also passes if seeds are ignored entirely) | Partly |
| ET-R21 | Warm start bursts every activated monitor so presumptions confirm in seconds | WatcherEngine.cs:367-368; spec 09-11 "Warm-up burst" | none | Unpinned |
| ET-R22 | Engine applies pool and verdict seeds; verdict seed primes the LinkDown->up resolver burst | WatcherEngine.cs:237-238, 261-265 | unit-level only (WarmStartTests pool/verdict Seed) | Partly |
| ET-R23 | Rebuild seed (FromEngine) skips Initializing/Starting, carries address+probe, never seeds Unknown verdict | Programs/ConWatch/WarmStartSeeder.cs:132-153 | none | Unpinned |
| ET-R24 | Normal cadence is phase-locked to epoch+offset+k*interval; first ping waits the offset; a late wakeup fires the owed ping at once | PingDiscipline.cs:167-204 | PingDisciplineTests.Normal_state_uses_normal_interval, Phase_offset_delays..., Normal_cadence_returns_to_the_phase_slot..., Late_wakeup_fires... | Pinned |
| ET-R25 | Faltering/Recovering/Down/Unreachable burst at BurstInterval, capped at BurstMaxDuration, then normal cadence | :56-75, 172-176 | PingDisciplineTests.Faltering_uses_burst_interval, Down_burst_expires_after_cap, Unreachable_keeps_probing... (Recovering only inside Reentering_down...) | Pinned |
| ET-R26 | Entering a failing state (re)starts the burst window regardless of the guard | :62-68 | PingDisciplineTests.Reentering_down_restarts_burst_window | Pinned |
| ET-R27 | Entering Responding/Starting ends any burst at once (normal cadence resumes) | :70-73 | none | Unpinned |
| ET-R28 | A lost ping while Responding or Starting starts a suspicion burst; a success never starts or extends one | :91-101 | PingDisciplineTests.Failed_ping_while_responding..., Success_never_starts_or_extends_a_burst (Starting case untested) | Partly |
| ET-R29 | A lost ping in Faltering/Down/Recovering/Unreachable never starts a burst (no 1 s hammering through a long outage) | :97; spec 08-14 §6 | none | Unpinned |
| ET-R30 | No burst is ever extended (suspicion, sweep or ForceBurst) | :126-136 | PingDisciplineTests.Suspicion_burst_respects_politeness_guard, ForceBurst_never_extends_an_active_burst | Pinned |
| ET-R31 | 60 s guard from the last burst start refuses suspicion and sweep bursts; ForceBurst ignores it (E4: one rule, 60 s) | :17, 131-132 | PingDisciplineTests.Suspicion_burst_blocked_within_60s..., ForceBurst_ignores_the_suspicion_guard | Pinned |
| ET-R32 | A burst start wakes a controller sleeping out a normal interval | :35-39, 138-165 | PingDisciplineTests.TriggerBurst_wakes_a_pending_normal_wait, Lost_ping_wakes_a_pending_normal_wait | Pinned |
| ET-R33 | Probe timeout: steady outside a burst; in a burst min(steady, cap), cap TCP 2500 / DNS 2000 / ICMP 1000 ms | WatcherEngine.cs:376-384 | ProbeThreadingTests.Burst_clamps_tcp_timeout_higher_than_icmp (TCP+ICMP caps only); PingControllerTests.Timeout_provider_is_read_per_ping | Partly |
| ET-R34 | Targets are never parked: Unreachable keeps probing | spec 10-09 D4; IPingCadence.cs | PingDisciplineTests.Unreachable_keeps_probing...; WatcherEngineTests.Router_down_with_everything_beyond_down... (recovers) | Pinned |
| ET-R35 | Pool members staggered k*interval/n, first pool by name; the engine feeds that offset into the discipline | PoolCoordinator.cs:48; WatcherEngine.cs:58-65, 197-200 | PoolCoordinatorTests (PhaseOffset), PoolStaggerTests; engine hand-off untested | Partly |
| ET-R36 | Engine cancellation is not a lost sample: transceivers rethrow; a reply or fault racing stop is dropped (E2) | PingController.cs:81-98; transceivers | PingControllerTests.A_probe_cancelled_mid_flight_writes_nothing...; PingTransceiverIcmpTests, TcpConnectTransceiverTests.Engine_cancellation_throws...; DomainNameResolverDnsTests.Cancelled_token_throws...; DnsQueryTransceiver untested | Partly |
| ET-R37 | A transceiver exception becomes one failed sample and the loop survives | PingController.cs:92-99 | PingControllerTests.Transceiver_exception_reports_failure_and_loop_survives | Pinned |
| ET-R38 | Results are stamped with the engine clock (E5) | PingController.cs:141-148 | PingControllerTests.Results_carry_the_engine_clock_not_the_wall_clock | Pinned |
| ET-R39 | Stopping one controller never completes the shared channel | PingController.cs:110-112 | PingControllerTests.Stopping_one_controller_does_not_complete_shared_channel | Pinned |
| ET-R40 | Controllers and results are keyed by target id, never by IP | PingManager.cs:12-15, RoutedPingResult.cs | WatcherEngineTests.Two_targets_on_one_ip_are_tracked_independently | Pinned |
| ET-R41 | The pump survives any subscriber exception, including a foreign OperationCanceledException; only its own cancellation ends it (E1) | PingManager.cs:143-160; WatcherEngine.cs:310-323 | PingPumpResilienceTests (both tests hit the ENGINE's catch; PingManager guard 0% covered) | Partly |
| ET-R42 | Stop is bounded by the grace on each phase and idempotent | PingController.cs:122-139; PingManager.cs:108-134 | PingManagerTests.StopAsync_with_ten_hanging_controllers... (fake honours cancellation, so the grace path never runs) | Partly |
| ET-R43 | Stall window = 3 x longest NormalInterval + longest PingTimeout over enabled targets | WatcherEngine.cs:186-189 | PingPumpResilienceTests.Stall_threshold_follows_the_slowest_profile | Pinned |
| ET-R44 | A loss from a healthy anchor or trunk hop bursts every anchor and trunk hop, never bystanders | SuspicionCoordinator.cs:64-80, 105-114 | SuspicionCoordinatorTests.Anchor_loss_sweeps_anchors_and_trunk_but_not_bystanders, Bystander_loss_does_not_sweep (trunk-triggered sweep and the "prior healthy" gate untested) | Partly |
| ET-R45 | Episode opens on a loss from a healthy target, closes after 5 straight successes, is a glitch only with >= 2 losses | :21-22, 116-140, 163-167 | SuspicionCoordinatorTests.Single_loss_episode_records_no_glitch, Two_loss_episode_records_glitch_with_span_and_counts (SampleCount 8 pins the 5) | Pinned |
| ET-R46 | Any non-healthy state during an episode discards it (transitions own it) | :142-150 | SuspicionCoordinatorTests.Faltering_escalation_discards_the_episode | Pinned |
| ET-R47 | Glitch is flagged Coalesced when its first loss is < 60 s after the previous glitch's last loss | :169-171 | SuspicionCoordinatorTests.Adjacent_episodes_coalesce (true case only) | Partly |
| ET-R48 | >= WeatherThreshold anchor episodes overlapping within +/-10 s make ONE Internet glitch; span = union; trunk wording | :195-246 | SuspicionCoordinatorTests.Correlated_anchor_glitches..., Trunk_involvement_changes_the_wording, Single_anchor_glitch_stays_a_target_glitch (no slack/span assertion) | Partly |
| ET-R49 | Local and Resolver targets get no suspicion bursts, sweeps, glitches or loss windows | WatcherEngine.cs:324-331 | LocalScopeEngineTests.IsLocalSubject..., ResolverScopeEngineTests.Resolver_samples_stay_out_of_the_loss_windows... (loss windows only) | Partly |
| ET-R50 | Engine glitch stream forwards SuspicionCoordinator glitches | WatcherEngine.cs:290-293 | none (GlitchRecorderTests use a hand-made Subject) | Unpinned |
| ET-R51 | Refused TCP connect is a reply; 200 refusals with no accept flag PersistentlyRefused | TargetMonitor.cs:25-26, 82-89, 149-153 | TargetMonitorTests.Two_hundred_refusals_with_no_accept_flags_persistent_refusal (only n=200; a smaller constant also passes) | Pinned (boundary weak) |
| ET-R52 | DNS resolution failure emits one ResolutionFailed transition; later ones are quiet | TargetMonitor.cs:249-262 | TargetMonitorTests.Resolution_failure_reports_once; DnsRefreshTests.Lookup_failure_reason_reaches_the_transition | Pinned |
| ET-R53 | BurstLossRate = loss over entries since the burst started, attached to transitions during a burst | TargetMonitor.cs:295-305 | TargetMonitorTests.Burst_loss_rate_is_attached_to_transitions_during_burst (value also matches the window rate) | Partly |
| ET-R54 | Hostname target re-resolves immediately when it goes Down; keeps an address still in the answer set; re-points (state+history kept, burst) when it leaves; failed refresh keeps it; backoff 30 s doubling to 5 min until first answer | WatcherEngine.cs:337-344, 387-500 | DnsRefreshTests (keep, re-point end state, failure keeps); Down trigger line 343 never executed; backoff 456-459 uncovered | Partly |
| ET-R55 | Disabled targets get no monitor or controller; pools/verdict skip them | WatcherEngine.cs:187, 194, 229-234 | none (line 234 uncovered) | Unpinned |
| ET-R56 | Seed is ignored once tracking or a topology command touched the monitor | TargetMonitor.cs:101-111; HostAnalysisEngine.cs:78-79 | none (both guard lines uncovered) | Unpinned (Low) |
| ET-S1 | Unreachable "holds"/parks pinging until the parent recovers | spec 08-14 §6, warm-restart spec | - | Superseded (10-09 D4) |
| ET-S2 | Sweep cooldown 30 s in the coordinator | spec 09-06 Part 1 | - | Superseded (E4 decision 09-18: discipline's 60 s guard) |
| ET-S3 | Burst timeout = min(PingTimeout, 1 s) for every probe | spec 09-06 Part 1 | - | Superseded (probe-aware caps, TCP spec / DNS watch) |
| ET-S4 | Children released only when the parent leaves Down | spec 08-14 §6 | - | Superseded (release = explanation withdrawn, 10-09 §4.4) |
| ET-S5 | Coalescing extends the previous glitch row inside the engine | spec 09-06 | - | Superseded in shape: engine emits Coalesced=true, the recorder merges |

## Probes, network infrastructure, core types

| Id | Rule | Source | Test(s) asserting it | Status |
|---|---|---|---|---|
| PR-R1 | ICMP: only IPStatus.Success is a reply; every other status is a loss whose Message is the status name | PingTransceiverIcmp.cs:51-56 | none | Unpinned |
| PR-R2 | ICMP: PingException / any non-cancel error is a failed sample, never a throw | PingTransceiverIcmp.cs:58-65 | none (lines uncovered) | Unpinned |
| PR-R3 | ICMP: engine-token cancel propagates as OCE promptly (WaitAsync), not as a lost sample (review E2) | PingTransceiverIcmp.cs:39-50 | PingTransceiverIcmpTests.Cancellation_ends_an_in_flight_ping_before_its_timeout_and_throws | Pinned |
| PR-R4 | TCP: SYN-ACK = reply "Accepted" | TcpConnectTransceiver.cs:33-34; tcp-probe spec §Semantics | TcpConnectTransceiverTests.Accepted_connection_is_a_reply | Pinned |
| PR-R5 | TCP: RST = reply "Refused" (TargetMonitor counts that exact word) | TcpConnectTransceiver.cs:36-38; TargetMonitor.cs:151 | TcpConnectTransceiverTests.Refused_connection_is_ALSO_a_reply; TargetMonitorTests:223 | Pinned |
| PR-R6 | TCP: silence = loss "TimedOut", bounded by the caller's timeout (race beats the OS SYN-retry schedule) | TcpConnectTransceiver.cs:29-30,40-42 | Silence_is_loss asserts !Success only — no elapsed bound, no message | Partly |
| PR-R7 | TCP: other socket errors = loss named by SocketError | TcpConnectTransceiver.cs:44-46 | none | Unpinned (Low) |
| PR-R8 | TCP: engine cancel throws | TcpConnectTransceiver.cs:40 | Engine_cancellation_throws_instead_of_reporting_loss | Pinned |
| PR-R9 | TCP: fresh socket per probe, no data sent, FIN close | spec §Semantics; TcpConnectTransceiver.cs:8-10 | none | Unpinned (Low) |
| PR-R10 | Burst timeout cap by probe: TCP 2500, DNS 2000, ICMP 1000 ms | WatcherEngine.cs:382 | ProbeThreadingTests.Burst_clamps_tcp_timeout_higher_than_icmp (TCP, ICMP only) | Partly |
| PR-R11 | Factory: tcp→TcpConnectTransceiver(port), dns→DnsQueryTransceiver, else ICMP | PingTransceiverFactoryIcmp.cs:15-21 | DnsQueryTransceiverTests.The_factory_returns_the_dns_transceiver_for_the_dns_probe (DNS only) | Partly |
| PR-R12 | DNS: no lookup names → miss "NoLookupNames", nothing sent | DnsQueryTransceiver.cs:24-26 | No_lookup_names_is_a_miss_that_sends_nothing | Pinned |
| PR-R13 | DNS: names rotate per resolver; a shorter list never overruns | DnsLookupNames.cs:31-41 | Names_rotate_across_probes; Next_rotates_per_resolver_and_is_null_when_empty | Pinned |
| PR-R14 | DNS seeded name: an expected address → hit "Verified" | DnsQueryTransceiver.cs:85-86; spec §4.2 | An_expected_address_is_Verified; stray-id test | Pinned |
| PR-R15 | DNS seeded name: all-blocked / NXDOMAIN / empty → hit "Filtered: …" (D11) | DnsQueryTransceiver.cs:96-99 | NXDOMAIN_is_Filtered…; A_blocked_address…; An_empty_answer… | Pinned |
| PR-R16 | DNS seeded name: any real wrong address (even mixed with blocked) → miss "WrongAnswer" | DnsQueryTransceiver.cs:100-101 | A_wrong_address…; A_real_wrong_address_is_still_a_WrongAnswer_miss | Pinned |
| PR-R17 | DNS seeded name: SERVFAIL / REFUSED / other rcode → miss, NOT Filtered | DnsQueryTransceiver.cs:87-102; spec §4.2 "Timeout, SERVFAIL, REFUSED … are misses" | none (rcode tests use an admin name; lines 92-94 uncovered) | Unpinned |
| PR-R18 | DNS admin name: NOERROR or NXDOMAIN → hit; error rcodes → miss; never the bare word "Refused" | DnsQueryTransceiver.cs:104-116 | Error_rcodes_are_misses_named_without_the_bare_word_Refused; NXDOMAIN test | Pinned |
| PR-R19 | DNS: silence → "TimedOut"; garbage → "Malformed"; other-id reply ignored; unencodable name → "BadName", nothing sent | DnsQueryTransceiver.cs:40-60 | Silence_is_TimedOut; Garbage_is_Malformed; A_stray_reply…; A_name_the_wire_format_cannot_carry… | Pinned |
| PR-R20 | DNS: engine-token cancel throws, not a miss (E2 rule applied to the newer transceiver) | DnsQueryTransceiver.cs:58 | none | Unpinned |
| PR-R21 | DNS: ICMP port-unreachable → miss named by the socket error (host up, nothing on 53) | DnsQueryTransceiver.cs:62-66 | none (uncovered) | Unpinned |
| PR-R22 | System resolver: engine cancel throws; failures carry a reason status ("Host not found", "Timeout", …) that the engine shows | DomainNameResolverDns.cs:29-50; review decision 6 | DomainNameResolverDnsTests (pre-cancel; status ≠ "Success") | Partly |
| PR-R23 | System resolver: success returns the addresses with status "Success" | DomainNameResolverDns.cs:25-27 | none (uncovered) | Unpinned |
| PR-R24 | ProbeSpec: absent/"icmp" = ICMP stored as null; "tcp:1-65535"; "dns" takes no port; everything else rejected; case/space tolerant | ProbeSpec.cs:20-50 | ProbeSpecTests | Pinned |
| PR-R25 | Probe politeness: public TCP only 80/443/53; hostnames and IPv6 count as public; ICMP/DNS allowed everywhere | ProbeSpec.cs:58-85; tcp-probe spec §Port policy | ProbeSpecTests.Port_policy_by_address_class etc. | Pinned (range edges thin, PR12) |
| PR-R26 | IsPrivateOrCarrier = IsPrivate + 100.64/10; IsPrivate excludes CGNAT | ProbeSpec.cs:93-104 | ProbeSpecTests | Pinned |
| PR-R27 | Seeded lookup names and their published addresses | DnsLookupName.cs:17-23 | Seeded_names_are_the_four_anchor_operators… | Pinned |
| PR-R28 | Lookup-name JSON and host-name validation rules | DnsLookupName.cs:35-100 | DnsLookupNamesTests | Pinned |
| PR-R29 | System resolvers: IPv4 first, OS order kept, deduped, 127.0.0.11 flagged Docker, fec0::ffff placeholders dropped, error → empty; resolv.conf parse | SystemResolverSource.cs | SystemResolverSourceTests | Pinned |
| PR-R30 | DoH: Prohibit → none; Require → all; Allow or EnableAutoDoh=2 → well-known; per-interface entries; zone ids ignored | DohConfiguration.cs:60-75 | DohDetectorTests | Pinned |
| PR-R31 | Hop kinds: private/CGNAT before first public = Internal; later private = Path; reached = Destination; silent = null | HopClassifier.cs | HopClassifierTests | Pinned |
| PR-R32 | Traceroute: 3 probes per hop; first answering address; best (min) RTT; reached if any probe reached | TracerouteRunner.cs:57-66 | none (FakeProbe answers identically every call) | Unpinned |
| PR-R33 | Traceroute: stop at destination, TTL 30, or stopWhen; ASN only for public Path/Destination hops; throwing lookup leaves hops unchanged | TracerouteRunner.cs | TracerouteRunnerTests | Pinned |
| PR-R34 | ASN: IPv6/private/carrier never queried | CymruAsnLookup.cs:54-57 | CymruAsnLookupTests | Pinned |
| PR-R35 | ASN: system resolvers only, no public-DNS fallback (hop addresses never leave via a host the user did not configure); cache 24 h hit / 10 min miss; name falls back to "AS{n}" | CymruAsnLookup.cs:26-83 | none | Unpinned |
| PR-R36 | LAN scan: RFC1918 only; /24 cap; 1024 hosts; ≤64 concurrent; skip gateway-less virtual nets when a gateway exists; own + gateway excluded; cancel keeps results + bounded name budget | LanScanner.cs | LanScannerTests | Pinned |
| PR-R37 | Neighbor tables (Linux /proc, Windows arp -a, macOS arp -an); MAC normalisation; drop zero/broadcast/multicast | NeighborTable.cs | NeighborTableTests | Pinned |
| PR-R38 | OUI: locally-administered MAC = "private (randomized) address" regardless of table | OuiTable.cs:39-41 | OuiTableTests | Pinned |
| PR-R39 | Router search: default route kept even without a ping reply; probes only if reachable; edge = first public hop that is not the destination | DiscoveryScanner.cs | DiscoveryScannerTests, GatewayDiscoveryTests | Pinned |
| PR-R40 | Scout: 3xx never followed; password never sent over plain HTTP to a non-loopback host without override; sign in once; 512-item batches; cancel ≠ Unreachable | ScoutClient.cs | ScoutClientTests | Pinned |
| PR-R41 | UPnP: WAN service types; controlURL resolved against LOCATION; "0.0.0.0"/empty = no external address | UpnpWanAddressProbe.cs:18-44,86-114 | none (0%) | Unpinned |
| PR-R42 | Preset values: Internal 1 s, Path 5 s, Public 10 s, Resolver (all fields) | PingProfile.cs | PingProfileTests | Pinned |
| PR-R43 | Preset values: Local (30/6/0.80/0.50, 10 s "so the stall window is unchanged"); shared defaults 3 s timeout, 100/10, 0.95/0.75/0.10/0.25 | PingProfile.cs:9-41 | none | Unpinned |
| PR-R44 | TreeWalk: cycle-safe, depth-capped at 32 | TreeWalk.cs | none direct | Unpinned (Thin indirect) |
| PR-R45 | LogThrottle: first logs, then one per window carrying the suppressed count | LogThrottle.cs | LogThrottleTests | Pinned |

## Configuration that builds and controls the engine

| Id | Rule | Source | Test(s) asserting it | Status |
|---|---|---|---|---|
| CF-R1 | Target names unique, case-insensitive | WatcherOptionsValidator.cs:23-26 | WatcherOptionsValidatorTests.Duplicate_target_name_fails | Pinned |
| CF-R2 | A target with an empty Name is rejected | Validator:23-24 | only incidentally (ConfigServiceTests:315 asserts "failed", not why) | Partly |
| CF-R3 | A target with an empty Address is rejected | Validator:37-38 | same incidental test | Partly |
| CF-R4 | Same address may repeat with different probes; same address+probe twice is rejected (stored/normalized form) | Validator:14-16,39-40; ConfigMapping.cs:117-122 | Same_address_with_different_probes_is_allowed, Same_address_and_probe_twice_fails, TargetWriteTests.Import_validates_addresses_the_way_they_will_be_stored, ResolverScopeValidationTests | Pinned |
| CF-R5 | Profile must be a built-in or a defined custom profile | Validator:28-29,146-148 | Unknown_profile_fails, Custom_profile_is_accepted | Pinned |
| CF-R6 | Probe string must parse; public addresses only on polite ports | Validator:31-35 | Invalid_probe_string_fails_validation, Impolite_public_port_fails_validation, ProbeFieldTests.Impolite_public_probe_is_rejected_with_400 | Pinned |
| CF-R7 | Scope must be a defined enum value; unknown scope JSON is rejected not defaulted | Validator:42-43; TargetDto | LocalScopeValidationTests.An_undefined_scope_value_is_rejected, TargetScopeTests.An_unknown_scope_is_rejected_not_defaulted, LocalScopeApiTests | Pinned |
| CF-R8 | Local and Resolver targets: no parent, no children, never pool members, cannot parent a pool | Validator:45-51,75-82,93-94 | LocalScopeValidationTests (4), ResolverScopeValidationTests (3), ConfigServiceTests Local switch tests | Pinned |
| CF-R9 | SystemManaged requires Resolver scope | Validator:53-54 | TargetSystemManagedTests.SystemManaged_requires_the_Resolver_scope | Pinned |
| CF-R10 | Pool names unique; pool name may not equal a target name | Validator:60-65 | none (lines 61,63,65 uncovered) | Unpinned |
| CF-R11 | Pool must have members; members must be targets, not pools | Validator:67-74 | Empty_pool_fails, Pool_member_referencing_pool_fails | Pinned |
| CF-R12 | Pool member / pool parent must name an existing target | Validator:71-80; ConfigService.cs:247-260 | none (validator 80 + service 251, 259 uncovered) | Unpinned |
| CF-R13 | A pool cannot be a target's parent; parent must exist | Validator:85-92 | Pool_as_parent_fails, Unknown_parent_fails, ConfigServiceTests.Unknown_parent_name_is_validation_error, ConfigApiTests.Unknown_parent_is_400 | Pinned |
| CF-R14 | No parent cycles | Validator:97-115 | Parent_cycle_fails, Reparenting_into_cycle_is_rejected_and_rolled_back | Pinned |
| CF-R15 | Profile intervals and timeout must be > 0 | Validator:119-121 | none (line 121 uncovered) | Unpinned |
| CF-R16 | Thresholds within [0,1] | Validator:122-131 | Bad_thresholds_fail (only >1; <0 untested) | Partly |
| CF-R17 | 1 <= MinUsableSample <= HistorySize | Validator:133-134 | MinUsableSample_exceeding_history_fails (lower bound untested) | Partly |
| CF-R18 | Faltering < Responding and Down < Recovering (review E6) | Validator:135-138 | Responding_threshold_below_faltering_threshold_fails, Recovering_threshold_below_down_threshold_fails | Pinned |
| CF-R19 | Every mutation validates the whole resulting config, commits or rolls back, and raises ConfigChanged only on commit | ConfigService.cs:374-402 | ConfigServiceTests (signal counts at :36,:53,:193,:214,:302,:321,:342,:430) | Pinned |
| CF-R20 | Uniqueness violations map to Validation, other DB errors do not (review P4) | ConfigService.cs:387-392; SqliteErrors | SqliteErrorsTests, DiscoveryAdoptTests duplicate name | Pinned |
| CF-R21 | An address edit must say correction/repoint (HistoryReset) or renumber (AddressChanged) | ConfigService.cs:147-169 | Address_edit_* (4), ConfigApiTests.Address_edit_requires_kind_and_records_marker | Pinned |
| CF-R22 | Probe/operator/address/description normalized identically on every write path | ConfigMapping.cs:25-37 | TargetWriteTests (3 paths), ConfigStoreTests.Seed_normalizes_probe_and_operator..., ProbeFieldTests, Profile_description_* | Pinned |
| CF-R23 | Profile description <= 500 chars, blank stored as null | ConfigService.cs:41-46 | Profile_description_is_trimmed..., Blank_profile_description..., Overlong_* (2), ConfigApiTests | Pinned |
| CF-R24 | A profile in use cannot be deleted; a target referenced by a child/pool cannot be deleted | ConfigService.cs:72-83,219-242 | Delete_profile_in_use_conflicts, Delete_target_with_children_conflicts, Delete_pool_member_target_conflicts | Pinned (successful profile delete untested) |
| CF-R25 | Deleting a pool (incl. the anchor pool) is allowed; verdict then goes off and health says anchor-pool-missing | ConfigService.cs:287-294; WatcherEngine.cs:247-254; ConfigHealth.cs:52-57 | Dangling_anchor_pool_name_warns_missing (health only); DeletePoolAsync never called | Unpinned |
| CF-R26 | InsertParent: new node takes the children's shared parent, children re-parented, one transaction | ConfigService.cs:193-217 | InsertParent_* (4), ConfigApiTests.Insert_parent_reparents_children_over_http | Pinned |
| CF-R27 | System-managed row: only name/enabled/operator/critical editable; cannot be deleted, created or promoted via API | ConfigService.cs:128-145,177-179,224-226 | ConfigServiceTests (address, probe, systemManaged frozen; enabled editable), SystemResolverServiceTests | Partly (parent/profile/scope frozen and name/operator/critical editable not asserted) |
| CF-R28 | Import validates before touching the DB, strips foreign system-managed targets, and restores missing built-ins | ConfigService.cs:297-319; ConfigStore.cs:154-156 | Import_invalid_document_changes_nothing, Import_strips_system_managed..., Import_of_an_old_export_naming_External_restores_the_builtins | Pinned |
| CF-R29 | Config export never carries system-managed targets | ConfigEndpoints.cs:56-61 | ExportApiTests (systemManaged check) | Pinned |
| CF-R30 | First boot seeds built-ins (+ a valid appsettings Watcher section); invalid file config falls back to built-ins only | ConfigStore.cs:33-66; WatcherHostService.cs:98-104 | ConfigStoreTests.Seed_* (store level) | Partly (host binding of the Watcher section untested, CF17) |
| CF-R31 | Every start ensures all built-in profiles exist and fills null descriptions, never overwriting numbers | ConfigStore.cs:75-100 | EnsureBuiltIns_* (2), ProfileMigrationTests | Pinned |
| CF-R32 | Migration renames External→Public unless a Public exists | Migrations/20261001075430_AddProfileDescription.cs | ProfileMigrationTests (2) | Pinned |
| CF-R33 | Missing JSON keys default: Scope=Verdict, Critical=false, SystemManaged=false; profile fields take PingProfileConfig defaults | ConfigDtos.cs | TargetScopeTests, TargetCriticalTests, TargetSystemManagedTests, ProfileMappingTests.A_profile_posted_with_only_a_name... | Pinned (Profile="Public", Enabled=true defaults not) |
| CF-R34 | Pool member order is preserved (it is the stagger order) | ConfigStore.cs:146-149,189; ConfigService.cs:280-283 | ConfigStoreTests.Seed_with_home_config_roundtrips, Export_carries_names_refs_and_ids | Pinned (store path; API reorder via PUT not asserted) |
| CF-R35 | Stored rows map to stable engine ids (two loads → same graph ids) | ConfigStore.cs:160-191 | Loaded_options_build_identical_graphs_twice | Pinned |
| CF-R36 | Whole-config reads are one snapshot (review P4) | ConfigStore.cs:216-228 | ConfigSnapshotReadTests (2) | Pinned |
| CF-R37 | A stored profile (including an edited built-in) overrides the compiled preset in the engine | TargetGraph.cs:37-40; ConfigStore.cs:180-181 | none | Unpinned (CF4) |
| CF-R38 | Each PingProfileConfig field maps to the matching PingProfile field | WatcherOptions.cs:66-79 | none directly; engine tests exercise intervals only | Partly (CF4) |
| CF-R39 | Disabled targets get no monitor/probe, are skipped in pools, the DNS watch, the stall window and the lookup-name list | WatcherEngine.cs:187,194,232-235; WatcherHostService.cs:183 | none | Unpinned (CF2) |
| CF-R40 | Operator/Critical/Scope/Probe/SystemManaged reach the engine Target | TargetGraph.cs:44-59 | indirectly via engine behaviour tests that build WatcherOptions | Partly (CF5) |
| CF-R41 | AnchorPool / WeatherThreshold / DnsWatch come from the Settings table on every engine start (appsettings only seeds) | WatcherHostService.cs:203-209; SettingsDefaults.cs | SettingsApiTests.Clearing_and_restoring_anchor_pool_toggles_the_verdict, Appsettings_values_seed_the_table_on_first_boot | Partly (WeatherThreshold effect untested, CF7) |
| CF-R42 | Lookup names = configured names + hostnames of enabled non-Resolver targets, de-duplicated; empty when Dns.Enabled=false; DnsWatch on only when the list is non-empty | WatcherHostService.cs:179-187,208 | DnsStatusTests (4 seeded names, IP-only targets), DnsPageTests watch-off | Partly (CF8) |
| CF-R43 | Verdict.*, Dns.* (except RetiredResolvers) and Engine.* writes trigger a rebuild; Retention/Mqtt/Webhook do not | SettingsStore.cs:142-153 | SettingsStoreTests (3) | Pinned |
| CF-R44 | Rebuilds are debounced (RebuildDebounceSeconds, 2 s in production) and coalesce | WatcherHostService.cs:344-374 | none (tests run with 0) | Unpinned (CF14) |
| CF-R45 | RebuildIfStale skips when a rebuild already serviced the generation; Engine.Enabled toggle rebuilds exactly once (review H4) | WatcherHostService.cs:241-261 | MasterEnableTests.M2M_engine_toggle_rebuilds_exactly_once, Unconditional_rebuild_runs... | Pinned |
| CF-R46 | A rebuild after StopAsync starts nothing | WatcherHostService.cs:252-256 | HostLifecycleTests.Rebuild_after_stop... | Pinned |
| CF-R47 | A config rebuild carries the outgoing engine's states (targets, pools, verdict); a re-addressed or re-probed target cold-starts; WarmRestartMaxGapMinutes<=0 disables carry | WatcherHostService.cs:264-275; WarmStartSeeder.cs:132-150; WatcherEngine.cs:200-203 | WarmStartTests.Probe_change_discards_the_seed (engine side, probe only); WarmRestartTests covers the process-restart path only | Unpinned (CF3) |
| CF-R48 | A rebuild that throws is retried on 1, 5, 15, 30, 60 s (last repeats) until one succeeds; a later good rebuild or shutdown cancels it | WatcherHostService.cs:62-68,284-290,311-328 | RebuildRetryTests (one test; retry body uncovered in the coverage run) | Partly (CF1) |
| CF-R49 | Invalid stored config: no engine, /health = ConfigInvalid with the failure text; the next good start clears it | WatcherHostService.cs:189-199,159; EngineHealth.cs:22-26 | HostLifecycleTests.Invalid_stored_config..., EngineHealthTests.Validation_failure... | Partly (recovery + boot path, CF9) |
| CF-R50 | Archive (Engine.Enabled=false): observers detached, one disabled snapshot pushed, data flushed (review H2) | WatcherHostService.cs:160-171 | MasterEnableTests.Disable_parks..., SignalRTests.Disabling_the_engine_pushes_one_disabled_snapshot... | Pinned |
| CF-R51 | /admin/engine, factory reset and clone/take-over answer 500 when the rebuild fails (review H4 second half) | AdminEndpoints.cs:36-38,159-161; CloneService.cs:85-87,172-174 | none | Unpinned (CF10) |
| CF-R52 | Settings PUT validation (anchor pool must exist, threshold >= 1, booleans, Dns names, read-only keys) | SettingsEndpoints.cs:31-123 | SettingsApiTests (theories), DnsSettingsApiTests | Pinned (retention month ranges + Clone.* read-only not; low value) |
| CF-R53 | Wizard: router Internal+critical (reused router untouched), WAN Path+ISP operator, anchors Public+catalog operator, upstream resolver, pool union, one config change, all-or-nothing (review S9) | SetupService.cs:40-128 | SetupApiTests (14), DnsWizardTests, FirstRunWithResolverTests | Pinned |
| CF-R54 | Wizard fills Verdict.AnchorPool only when unset | SetupService.cs:136-140 | none effective (factory pre-seeds AnchorPool; lines 137-140 uncovered) | Unpinned (CF12) |
| CF-R55 | Adopting a Gateway discovery pre-checks Critical | Discoveries.cshtml.cs:92-95; Targets.cshtml.cs:34-35 | DiscoveriesPageTests.Adopt_links_prefill_the_Targets_dialog_by_kind (URL only) | Partly |
| CF-R56 | ConfigHealth: anchors vs threshold/recommended, anchor-pool missing/none, all-anchors-ISP, pool-parent drift, DNS-not-watched/no-upstream/DoH, page routing | ConfigHealth.cs | ConfigHealthTests (12), HealthWarningPageTests, RuntimeHealthTests | Pinned |
| CF-R57 | System-resolver reconcile (create, name, leave hand-made alone, delete vanished, retire/restore with watch, empty OS list = no info) | SystemResolverReconciler.cs, SystemResolverService.cs | SystemResolverReconcilerTests (13), SystemResolverServiceTests (5) | Pinned |
| CF-R58 | Clone/take-over drop system-managed targets always and Local devices when the clone said so | CloneService.cs:58-64,143-152 | CloneTests, CloneWithoutLocalDevicesTests, CloneWithoutSystemManagedTests | Pinned |
| CF-R59 | conwatch.json precedence flag > env > user > machine > default; unknown properties survive; atomic write | ConConfig/ConfigResolver.cs, ConfigFile.cs | ConfigResolverTests, ConfigFileTests | Pinned |
