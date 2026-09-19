# Anchor Hackathon MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a runnable Windows 11 desktop assistant that detects likely task drift, intervenes gently, restores context after interruption, and records attention/progress metrics.

**Architecture:** A .NET 10 WinUI 3 host owns Windows integration, the interface, safety controls, state, and persistence. A Python 3.11-compatible worker exposes local multimodal inference over gRPC, while a Manifest V3 browser extension provides DOM-level relevance and visual filtering. The MVP remains useful when the worker, browser extension, webcam, or audio input is unavailable.

**Tech Stack:** .NET 10.0.401, C# 14, Windows App SDK 2.5.1, WinUI 3, xUnit, SQLite, gRPC/Protocol Buffers, Python 3.11, pytest, OpenCV/MediaPipe optional extras, TypeScript-free Manifest V3 JavaScript.

**Spec:** `docs/superpowers/specs/2026-09-19-anchor-general-task-assistant-design.md`

## Global Constraints

- Target Windows 11 x64; Windows 10 compatibility is not an MVP requirement.
- Use an unpackaged, self-contained Windows App SDK deployment for the hackathon.
- Raw keystrokes, full clipboard history, and raw microphone recordings must never be persisted.
- Webcam and microphone sensing are opt-in and must fail independently without disabling the core assistant.
- Every restrictive intervention must be reversible with `Esc`, a global emergency hotkey, focus loss, and a watchdog timeout.
- The MVP state model is explicit rules plus a Gaussian HMM-compatible temporal smoothing layer; a learned classifier is outside the MVP.
- Recovery summaries must be deterministic unless an optional AI provider is explicitly enabled.
- Cross-application visual filtering uses dimming, desaturation, or softened covers; browser images may use direct CSS blur.
- Keep all user data local in the MVP.

## Review Focus

- Sensor storms or missing events must not freeze the interface; bounded channels drop stale low-priority events while preserving manual reports.
- A foreground change to a password, permission, secure, or system-critical window must immediately clear overlays and pointer restrictions.
- The worker being absent, slow, malformed, or terminated must move the host to deterministic degraded mode without losing the task session.
- Empty, whitespace-only, non-Latin, and very long task titles must be accepted safely and produce bounded relevance scores.
- Repeated incorrect interventions must be dismissible and must reduce future intervention sensitivity rather than escalating.

---

## File Structure

```text
Anchor.slnx
Directory.Build.props
Directory.Packages.props
README.md
contracts/anchor.proto
src/Anchor.Core/
  Anchor.Core.csproj
  Models/*.cs
  Services/*.cs
src/Anchor.Infrastructure/
  Anchor.Infrastructure.csproj
  Persistence/SqliteEventStore.cs
  Windows/ForegroundWindowSensor.cs
  Windows/InputActivitySensor.cs
  Worker/InferenceWorkerClient.cs
src/Anchor.Desktop/
  Anchor.Desktop.csproj
  App.xaml(.cs)
  MainWindow.xaml(.cs)
  ViewModels/MainViewModel.cs
  Views/*.xaml(.cs)
  Overlays/*.xaml(.cs)
  Services/SessionOrchestrator.cs
src/Anchor.Worker/
  pyproject.toml
  anchor_worker/*.py
  tests/*.py
browser/anchor-extension/
  manifest.json
  service-worker.js
  content-script.js
  options.html
  options.js
tests/Anchor.Core.Tests/
  Anchor.Core.Tests.csproj
  *.cs
tests/Anchor.Infrastructure.Tests/
  Anchor.Infrastructure.Tests.csproj
  *.cs
demo/replay/*.jsonl
scripts/build.ps1
scripts/run-demo.ps1
```

### Task 1: Repository and Domain Foundation

**Files:**
- Create: `.gitignore`, `Anchor.slnx`, `Directory.Build.props`, `Directory.Packages.props`
- Create: `src/Anchor.Core/Anchor.Core.csproj`
- Create: `src/Anchor.Core/Models/AttentionState.cs`, `GoalSession.cs`, `SensorWindow.cs`, `AttentionPrediction.cs`, `ContextCapsule.cs`, `InterventionDecision.cs`
- Create: `tests/Anchor.Core.Tests/Anchor.Core.Tests.csproj`, `ModelValidationTests.cs`

**Interfaces:**
- Produces: immutable domain records used by every later task.
- `GoalSession.Create(string title, DateTimeOffset startedAt) -> GoalSession`
- `SensorWindow` contains only derived counts, durations, process metadata, and optional relevance hints.

- [x] **Step 1: Initialize Git and create the solution skeleton**

Run the local .NET 10 SDK with a project-local `DOTNET_CLI_HOME`, create the solution and projects, and add project references. Add `.tools/`, `bin/`, `obj/`, `.venv/`, Python caches, browser build output, SQLite files, and generated gRPC files to `.gitignore`.

- [x] **Step 2: Write failing domain validation tests**

```csharp
[Fact]
public void Create_trims_title_and_rejects_blank_title()
{
    GoalSession.Create("  Read paper  ", DateTimeOffset.UnixEpoch).Title.Should().Be("Read paper");
    var act = () => GoalSession.Create(" \t ", DateTimeOffset.UnixEpoch);
    act.Should().Throw<ArgumentException>();
}

[Fact]
public void Sensor_window_rejects_negative_counts()
{
    var act = () => SensorWindow.Create(keyCount: -1, mouseDistance: 0, idleSeconds: 0);
    act.Should().Throw<ArgumentOutOfRangeException>();
}
```

- [x] **Step 3: Verify the tests fail for missing models**

Run: `dotnet test tests/Anchor.Core.Tests/Anchor.Core.Tests.csproj`

- [x] **Step 4: Implement immutable validated models**

Use records with constructor guards. Clamp free-text task titles to 240 Unicode scalar values, relevance/confidence values to `[0,1]`, and durations to non-negative values. Store keyboard activity as counts by category, never key values.

- [x] **Step 5: Run tests and commit**

Run: `dotnet test tests/Anchor.Core.Tests/Anchor.Core.Tests.csproj`

Commit: `feat: add Anchor domain foundation`

### Task 2: Deterministic Attention Engine and Intervention Policy

**Files:**
- Create: `src/Anchor.Core/Services/TaskRelevanceScorer.cs`
- Create: `src/Anchor.Core/Services/AttentionStateMachine.cs`
- Create: `src/Anchor.Core/Services/InterventionPolicy.cs`
- Create: `src/Anchor.Core/Services/TemporalEvidenceBuffer.cs`
- Create: `tests/Anchor.Core.Tests/AttentionStateMachineTests.cs`
- Create: `tests/Anchor.Core.Tests/InterventionPolicyTests.cs`

**Interfaces:**
- Consumes: `GoalSession`, `SensorWindow`, `AttentionPrediction`.
- Produces: `AttentionStateMachine.Update(SensorWindow) -> AttentionPrediction`.
- Produces: `InterventionPolicy.Decide(AttentionPrediction, UserPreferences) -> InterventionDecision`.

- [x] **Step 1: Write state transition tests**

```csharp
[Fact]
public void Sustained_irrelevant_activity_enters_distracted_but_one_noisy_window_does_not()
{
    var machine = AttentionStateMachine.CreateDefault();
    machine.Update(Fixtures.FocusedWindow()).State.Should().Be(AttentionState.Focused);
    machine.Update(Fixtures.IrrelevantWindow()).State.Should().NotBe(AttentionState.Distracted);
    machine.Update(Fixtures.IrrelevantWindow()).State.Should().NotBe(AttentionState.Distracted);
    machine.Update(Fixtures.IrrelevantWindow()).State.Should().Be(AttentionState.Distracted);
}

[Theory]
[InlineData("")]
[InlineData("   ")]
[InlineData("阅读量子物理章节")]
public void Relevance_score_is_bounded_for_any_title(string title)
{
    var score = TaskRelevanceScorer.Score(title, "browser", "example");
    score.Should().BeInRange(0, 1);
}
```

- [x] **Step 2: Verify the tests fail**

Run: `dotnet test tests/Anchor.Core.Tests/Anchor.Core.Tests.csproj --filter Attention`

- [x] **Step 3: Implement feature scoring and temporal hysteresis**

Compute deterministic evidence from application relevance, idle duration, rapid switching, scroll loops, pointer wandering, typing continuity, gaze presence when available, and manual reports. Use 1 s, 5 s, and 30 s rolling summaries. Require sustained evidence to enter `Drifting` or `Distracted`, and stronger sustained positive evidence to return to `Focused`.

- [x] **Step 4: Implement explainable policy**

Map predictions to `None`, `BeaconPulse`, `VisualFilter`, `IntentionGate`, `RecoveryCard`, or `BreakSuggestion`. Include reason codes and a cooldown. Manual distraction reports bypass detector thresholds and immediately request recovery.

- [x] **Step 5: Add safety and false-positive tests**

Test cooldowns, low confidence, repeated dismissals, manual overrides, secure-window suppression, and worker-unavailable predictions.

- [x] **Step 6: Run tests and commit**

Commit: `feat: add deterministic attention and intervention engine`

### Task 3: Context Capsules, Progress Metrics, and SQLite

**Files:**
- Create: `src/Anchor.Core/Services/ContextCapsuleManager.cs`
- Create: `src/Anchor.Core/Services/ProgressTracker.cs`
- Create: `src/Anchor.Core/Services/SensitiveTextRedactor.cs`
- Create: `src/Anchor.Infrastructure/Anchor.Infrastructure.csproj`
- Create: `src/Anchor.Infrastructure/Persistence/SqliteEventStore.cs`
- Create: `tests/Anchor.Core.Tests/ContextCapsuleManagerTests.cs`
- Create: `tests/Anchor.Infrastructure.Tests/SqliteEventStoreTests.cs`

**Interfaces:**
- `ContextCapsuleManager.Observe(ContextObservation) -> ContextCapsule?`
- `ContextCapsuleManager.Freeze(DistractionReason) -> ContextCapsule`
- `ProgressTracker.Apply(DerivedEvent) -> ProgressSnapshot`
- `IEventStore.AppendAsync(DerivedEvent, CancellationToken)` and date-range deletion.

- [x] **Step 1: Write recovery-anchor tests**

```csharp
[Fact]
public void Freeze_keeps_last_confident_anchor_not_distracting_window()
{
    var manager = new ContextCapsuleManager();
    manager.Observe(Fixtures.ConfidentAnchor("section 3", "Compare results"));
    manager.Observe(Fixtures.LowConfidenceObservation("social feed"));
    manager.Freeze(DistractionReason.AppSwitch).Location.Should().Be("section 3");
}
```

- [x] **Step 2: Implement deterministic capsule generation**

Store task title, application, sanitized document/URL identity, last meaningful action, current location, selected non-sensitive text, next-step suggestion, and restoration commands. Redact email addresses, tokens, likely secrets, password fields, and user-configured applications before persistence.

- [x] **Step 3: Write SQLite round-trip and deletion tests**

Use a temporary database. Verify schema migration, append/query order, cancellation, session deletion, all-history deletion, and that raw input values are absent from serialized payloads.

- [x] **Step 4: Implement WAL-mode SQLite persistence**

Use parameterized commands, an explicit schema version, WAL mode, bounded payload sizes, and transactions for capsule plus event writes.

- [x] **Step 5: Run tests and commit**

Commit: `feat: add local context recovery and progress store`

### Task 4: Python Inference Worker and gRPC Contract

**Files:**
- Create: `contracts/anchor.proto`
- Create: `src/Anchor.Worker/pyproject.toml`
- Create: `src/Anchor.Worker/anchor_worker/server.py`, `features.py`, `inference.py`, `health.py`, `__main__.py`
- Create: `src/Anchor.Worker/tests/test_features.py`, `test_inference.py`, `test_server.py`
- Create: `src/Anchor.Infrastructure/Worker/InferenceWorkerClient.cs`
- Create: `tests/Anchor.Infrastructure.Tests/InferenceWorkerClientTests.cs`

**Interfaces:**
- gRPC methods: `Health`, `PredictAttention`, `AnalyzeFrame`, `Shutdown`.
- The host sends derived `SensorWindow` messages and optional compressed screen crops only when visual analysis is enabled.
- The worker returns bounded scores, availability flags, latency, and reason codes.

- [x] **Step 1: Define the Protocol Buffer contract**

Define numeric feature fields rather than arbitrary maps for the stable MVP contract. Add a `oneof` for unavailable/degraded analysis and include protocol version `1` in health responses.

- [x] **Step 2: Write failing Python tests**

```python
def test_predict_is_bounded_and_explainable():
    result = predict_attention(sensor_window(idle_seconds=14, app_relevance=0.05))
    assert 0.0 <= result.distraction_probability <= 1.0
    assert result.reason_codes

def test_malformed_values_are_sanitized():
    result = normalize_features({"idle_seconds": float("nan"), "mouse_distance": -4})
    assert result.idle_seconds == 0
    assert result.mouse_distance == 0
```

- [x] **Step 3: Implement deterministic worker baseline**

Implement feature normalization, calibrated logistic scoring, and exponential temporal smoothing in pure Python/Numpy. Make OpenCV and MediaPipe optional extras; report unavailable capabilities instead of failing startup.

- [x] **Step 4: Implement authenticated loopback startup**

Require a random token in gRPC metadata, bind only to `127.0.0.1`, print one JSON readiness line to stdout, and support graceful shutdown.

- [x] **Step 5: Implement host worker lifecycle**

Start the worker with redirected streams, wait for readiness with a five-second timeout, validate protocol version, use deadlines on every call, restart at most twice with backoff, and remain in deterministic mode after failure.

- [x] **Step 6: Run Python and C# tests, then commit**

Run: `python -m pytest src/Anchor.Worker/tests -q`

Run: `dotnet test tests/Anchor.Infrastructure.Tests/Anchor.Infrastructure.Tests.csproj`

Commit: `feat: add local inference worker protocol`

### Task 5: Windows Sensors and Safety Watchdog

**Files:**
- Create: `src/Anchor.Infrastructure/Windows/ForegroundWindowSensor.cs`
- Create: `src/Anchor.Infrastructure/Windows/InputActivitySensor.cs`
- Create: `src/Anchor.Infrastructure/Windows/IdleTimeSensor.cs`
- Create: `src/Anchor.Infrastructure/Windows/SecureWindowClassifier.cs`
- Create: `src/Anchor.Infrastructure/Windows/PointerConfinement.cs`
- Create: `src/Anchor.Infrastructure/Windows/SafetyWatchdog.cs`
- Create: `tests/Anchor.Infrastructure.Tests/WindowsFeatureAggregationTests.cs`
- Create: `tests/Anchor.Infrastructure.Tests/SafetyWatchdogTests.cs`

**Interfaces:**
- Sensors publish sanitized `DerivedEvent` values through a bounded `Channel<DerivedEvent>`.
- `SafetyWatchdog.ReleaseAll()` clears overlays, hooks, and `ClipCursor` synchronously.

- [x] **Step 1: Write aggregation and watchdog tests using fake native adapters**

Cover rapid app switches, idle periods, mouse distance without coordinates, keyboard category counts without keys, channel overflow, secure-window activation, focus loss, `Esc`, and watchdog expiry.

- [x] **Step 2: Implement foreground and idle sensors**

Use `SetWinEventHook`, `GetForegroundWindow`, process identity, window title redaction, and `GetLastInputInfo`. Never poll faster than needed and marshal callbacks onto a dedicated service queue.

- [x] **Step 3: Implement aggregate input sensing**

Use Raw Input to count key-down categories and accumulate pointer distance/velocity. Discard raw key codes immediately after categorization and never expose typed content outside the sensor.

- [x] **Step 4: Implement opt-in pointer confinement and fail-open watchdog**

Wrap `ClipCursor`; release on `Esc`, emergency hotkey, target focus loss, process exit, secure-window activation, shutdown, or a 30-second watchdog timeout.

- [x] **Step 5: Run tests and commit**

Commit: `feat: add privacy-safe Windows sensing and watchdog`

### Task 6: WinUI Shell, Goal Beacon, Recovery, and Progress UI

**Files:**
- Create: `src/Anchor.Desktop/Anchor.Desktop.csproj` and template assets
- Create: `src/Anchor.Desktop/MainWindow.xaml(.cs)`
- Create: `src/Anchor.Desktop/ViewModels/MainViewModel.cs`
- Create: `src/Anchor.Desktop/Views/SessionPage.xaml(.cs)`, `TimelinePage.xaml(.cs)`, `SettingsPage.xaml(.cs)`
- Create: `src/Anchor.Desktop/Overlays/GoalBeaconWindow.xaml(.cs)`, `VisualFilterWindow.xaml(.cs)`, `RecoveryCardWindow.xaml(.cs)`, `IntentionGateWindow.xaml(.cs)`
- Create: `src/Anchor.Desktop/Services/SessionOrchestrator.cs`
- Create: `tests/Anchor.Core.Tests/SessionOrchestratorTests.cs`

**Interfaces:**
- `SessionOrchestrator.StartAsync(title)`, `ReportDistractedAsync()`, `StopAsync()`.
- View model exposes task title, attention state, confidence, reasons, elapsed focus, interruption count, recovery time, and capability status.

- [x] **Step 1: Write orchestrator tests against fake sensors, store, worker, and intervention presenter**

Verify start/stop lifecycle, one active session, manual distraction recovery, cooldown, repeated dismissals, missing worker, cancellation, and clean shutdown.

- [x] **Step 2: Create the unobtrusive shell**

Build a compact main window with task title entry, Start/Stop, “I’m distracted,” current state, capability chips, progress timeline, and privacy controls. Closing the window minimizes to the notification area while an active session exists; Exit performs full cleanup.

- [x] **Step 3: Implement Goal Beacon**

Create a click-through always-on-top compact overlay showing the current task. Pulse opacity/scale only after sustained high-confidence drift, cap animation at two seconds, respect reduced-motion settings, and expose dismiss/snooze controls when hovered.

- [x] **Step 4: Implement visual filter and intention gate**

The filter overlay dims/desaturates configured non-task regions without capturing protected windows. The gate appears before a high-confidence irrelevant transition and offers Continue, Park for later, Return to task, and Disable gates.

- [x] **Step 5: Implement recovery card and timeline**

Show last meaningful action, last location, next step, elapsed interruption, and actions for Resume, Reopen, Recap, Break down, and Dismiss. Record intervention response and recovery latency.

- [x] **Step 6: Build, smoke-test launch, and commit**

Run: `dotnet build Anchor.slnx -c Debug -p:Platform=x64`

Run the app, start a demo session, trigger manual distraction, verify the recovery card, press `Esc`, and verify all overlays clear.

Commit: `feat: add Anchor desktop experience`

### Task 7: Browser Adapter and Dynamic Picture Blur

**Files:**
- Create: `browser/anchor-extension/manifest.json`
- Create: `browser/anchor-extension/service-worker.js`
- Create: `browser/anchor-extension/content-script.js`
- Create: `browser/anchor-extension/options.html`, `options.js`, `styles.css`
- Create: `browser/anchor-extension/tests/content-script.test.mjs`

**Interfaces:**
- Native messaging payloads contain URL origin, page title, selected-text summary, reading progress, image rectangles/salience, and user actions.
- Content script accepts `setVisualFilter`, `setFutureTextMask`, `clearInterventions`, and `showRecoveryAnchor` commands.

- [x] **Step 1: Write DOM tests**

Use Node’s built-in test runner with a minimal DOM fixture. Test image classification, future-text masking, restoration, mutation handling, excluded form/media elements, and idempotent cleanup.

- [x] **Step 2: Implement Manifest V3 least-privilege extension**

Use `activeTab`, `storage`, `scripting`, and native messaging permissions. Do not request browsing history. Disable all content processing on password, payment, browser-internal, and user-denied origins.

- [x] **Step 3: Implement dynamic image filtering**

Score visible images by area, animation, contrast proxy, viewport position, and task relevance hints. Apply CSS blur/desaturation only above the configured threshold and add a temporary reveal-on-hover affordance.

- [x] **Step 4: Implement reading support**

Track viewport progress, detect large forward skips, optionally mask future paragraphs, detect repeated dwell on one phrase, and send a recovery anchor without claiming medical diagnosis.

- [x] **Step 5: Test and commit**

Run: `node --test browser/anchor-extension/tests/*.test.mjs`

Commit: `feat: add browser focus adapter`

### Task 8: Demo Replay, Packaging, Documentation, and Completion Audit

**Files:**
- Create: `demo/replay/focused-to-distracted.jsonl`, `interrupted-and-returned.jsonl`, `stuck-reading.jsonl`
- Create: `scripts/build.ps1`, `scripts/run-demo.ps1`
- Create: `README.md`, `docs/privacy.md`, `docs/demo-script.md`
- Modify: all project manifests and package locks

**Interfaces:**
- `run-demo.ps1 -Scenario <name>` feeds deterministic events to the same orchestrator used by live sensors.
- `build.ps1` restores, tests, builds x64, runs browser tests, runs Python tests, and publishes the unpackaged self-contained app.

- [x] **Step 1: Add deterministic replay scenarios**

Cover normal focus, app-switch drift, reading skip, stuck phrase, sudden interruption, manual distraction, worker failure, and recovery. Every scenario includes expected states and intervention outputs.

- [x] **Step 2: Add an end-to-end replay test**

Run each JSONL file through the orchestrator and assert the final state, intervention sequence, context capsule, and metrics.

- [x] **Step 3: Write build and demo scripts**

Pin the local .NET SDK path, set telemetry opt-out, restore locked dependencies, run all tests, publish `win-x64` self-contained output, and print the exact output folder. The demo script starts the worker when available and otherwise labels deterministic mode.

- [x] **Step 4: Write user and privacy documentation**

Document setup, controls, all sensors, permissions, degraded modes, data deletion, emergency escape, extension loading, demo flow, known limitations, and the distinction between an assistive hackathon prototype and a medical device.

- [x] **Step 5: Run the full completion audit**

Run:

```powershell
./scripts/build.ps1
node --test browser/anchor-extension/tests/*.test.mjs
python -m pytest src/Anchor.Worker/tests -q
dotnet test Anchor.slnx -c Release -p:Platform=x64
```

Then launch the published app and execute all three replay scenarios plus the manual recovery button. Verify overlays clear with `Esc`, the worker can be killed without ending the session, and local history deletion removes all stored events.

- [x] **Step 6: Commit the verified MVP**

Commit: `feat: complete Anchor hackathon MVP`
