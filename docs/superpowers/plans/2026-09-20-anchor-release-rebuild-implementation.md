# Anchor Release Rebuild Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a release-ready Windows application that performs real gaze sensing, DeepSeek-backed task planning and relevance, visible desktop/browser interventions and recovery, and with-Anchor versus baseline study recording.

**Architecture:** Preserve the existing C# domain/WinUI/Python-worker split, but replace the disconnected demo boundaries with versioned contracts and visible integration tests. The WinUI host owns lifecycle, safety, settings, overlays, task progress, and the browser bridge; the bundled Python worker owns camera CV and MP4 recording; a minimized DeepSeek gateway supplies structured task plans and cached context judgments.

**Tech Stack:** .NET 10, C# 14, WinUI 3, Windows App SDK 2.5, Windows Forms `NotifyIcon`, SQLite, DPAPI, gRPC/Protobuf, Python 3.11–3.12, OpenCV, MediaPipe, NumPy, MSS, PyInstaller, Manifest V3 JavaScript, xUnit, pytest, Node test runner.

**Spec:** `docs/superpowers/specs/2026-09-20-anchor-release-rebuild-design.md`

## Global Constraints

- Windows 11 x64 is the release target; Windows 10 compatibility is not required.
- `Anchor.exe` must run from the release directory without a separately installed Python runtime.
- Gaze must come from real OpenCV/MediaPipe camera analysis; no constant or simulated value may be labeled live gaze.
- DeepSeek receives minimized text context only; screenshots, frames, gaze samples, raw keys, passwords, and secure-window details never leave the computer.
- User corrections override DeepSeek judgments, and no single gaze or LLM signal confirms distraction.
- All restrictive UI fails open through `Esc`, watchdog expiry, secure-window detection, session stop, and process shutdown.
- Toolkit controls visibly apply immediately; unavailable adapter-specific effects are labeled unavailable rather than silently ignored.
- Recording requires explicit start, a persistent indicator, and explicit consent for a webcam thumbnail or microphone.
- The Goal Beacon is stationary during normal operation and may perform one short shake only when sustained evidence crosses the distraction threshold; reduced-motion mode uses static emphasis.
- New product behavior follows strict test-first red/green/refactor cycles.

## Review Focus

1. **Camera present but landmarks unreliable:** gaze-dependent decisions must disable themselves while desktop sensing continues; Task 4 adds low-confidence and calibration-residual tests.
2. **DeepSeek unavailable, malformed, or slow:** starting/stopping sessions and explicit progress must remain usable with a labeled local fallback; Task 2 adds timeout, schema, and missing-key tests.
3. **Overlay created on a secondary or scaled monitor:** beacon/recovery/toolkit windows must remain visible within the active working area; Tasks 3 and 7 add geometry tests and Task 14 verifies this visibly.
4. **Recording interrupted by display/camera/disk failure:** recorder state must stop, finalize usable output when possible, and report an actionable error; Task 10 adds process and output-integrity tests.
5. **Browser extension absent or disconnected:** desktop controls must still work while DOM-specific controls show unavailable; Task 8 adds disconnect/reconnect and capability tests.

## File and responsibility map

```text
src/Anchor.Core/
  Models/TaskPlan.cs                    validated task/subtask contracts
  Models/RelevanceJudgment.cs           structured semantic relevance result
  Models/GazeSample.cs                  normalized gaze contract created with worker RPCs
  Models/StudyRecording.cs              trial manifest and comparison contracts
  Services/TaskPlanManager.cs           active-subtask lifecycle
  Services/AttentionFusion.cs           multimodal temporal evidence
  Services/RecordingComparison.cs       pure A/B metric calculation
  Services/SessionOrchestrator.cs       integrates plan, context, inference, policy

src/Anchor.Infrastructure/
  DeepSeek/DeepSeekClient.cs             HTTPS structured-output client
  DeepSeek/DeepSeekSettingsStore.cs      DPAPI-protected API settings
  Worker/InferenceWorkerClient.cs        gaze/calibration/recording RPC client
  Browser/NativeBridgeServer.cs          authenticated local browser message hub

src/Anchor.Desktop/
  MainPage.xaml                          Settings page shell
  ViewModels/MainPageViewModel.cs        Settings/session commands and state
  Services/TrayIconService.cs            stationary tray lifecycle
  Services/OverlayPresenter.cs           visible prevention/recovery surfaces
  Services/StudyRecordingService.cs      recorder orchestration and manifests
  Views/GazeTestView.xaml                camera test and calibration controls
  Views/RecordingsView.xaml              recording and comparison controls
  Overlays/GoalBeaconWindow.*            goal/subtask/progress and shake
  Overlays/GazeSpotlightWindow.*          immediate focus-tool preview
  Overlays/WindowFirewallWindow.*         active-window dim/cover behavior

src/Anchor.NativeBridge/
  Program.cs                              Chrome/Edge native-messaging stdio host
  Anchor.NativeBridge.csproj              packaged bridge executable

src/Anchor.Worker/anchor_worker/
  gaze.py                                 MediaPipe landmark and gaze pipeline
  calibration.py                          robust calibration transform
  recording.py                            screen/gaze MP4 and event output
  server.py                               versioned worker RPC implementation

browser/anchor-extension/
  service-worker.js                       bridge lifecycle and tab injection
  content-script.js                       DOM visual/reading functions
  tests/                                  executable browser behavior tests

scripts/
  build.ps1                               test, PyInstaller, publish, assemble
  register-browser-bridge.ps1             per-user native-host registration
  unregister-browser-bridge.ps1           exact rollback
  verify-release.ps1                      packaged smoke tests
```

---

### Task 1: Task-plan and subtask-progress domain

**Files:**
- Create: `src/Anchor.Core/Models/TaskPlan.cs`
- Create: `src/Anchor.Core/Services/TaskPlanManager.cs`
- Modify: `src/Anchor.Core/Models/GoalSession.cs`
- Test: `tests/Anchor.Core.Tests/TaskPlanManagerTests.cs`

**Interfaces:**
- Produces: `TaskPlan`, `TaskStep`, `TaskProgressSnapshot`, `TaskPlanManager.Start`, `TaskPlanManager.MarkCurrentComplete`, `TaskPlanManager.SuggestCompletion`, and `TaskPlanManager.ReplacePlan`.
- Consumes later: DeepSeek client output, Goal Beacon, recovery card, recorder metadata, and browser progress events.

- [ ] **Step 1: Write failing validation and progression tests**

```csharp
[Fact]
public void MarkCurrentComplete_advances_exactly_one_step()
{
    var manager = new TaskPlanManager();
    manager.Start(new TaskPlan("Do 3 USACO questions", [
        new("q1", "Finish question 1", "Accepted submission"),
        new("q2", "Finish question 2", "Accepted submission"),
        new("q3", "Finish question 3", "Accepted submission")
    ]));
    var result = manager.MarkCurrentComplete(CompletionSource.User, DateTimeOffset.Parse("2026-09-20T02:00:00Z"));
    Assert.Equal("q2", result.CurrentStep!.Id);
    Assert.Equal(1, result.CompletedCount);
}
```

In the same file, add separate tests that pass zero steps and nine literal steps to `Start` and assert `ArgumentException`. Add a suggestion test that starts the three-step plan above, calls `SuggestCompletion("Accepted result detected")`, and asserts that `CompletedCount` remains `0`, `CurrentStep.Id` remains `q1`, and `PendingSuggestion` equals the supplied evidence until `ConfirmSuggestedCompletion` is called.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `.\.tools\dotnet\dotnet.exe test tests\Anchor.Core.Tests\Anchor.Core.Tests.csproj --filter TaskPlanManagerTests --nologo`

Expected: compilation fails because the task-plan types do not exist.

- [ ] **Step 3: Implement validated immutable contracts and state manager**

```csharp
public sealed record TaskStep(string Id, string Title, string CompletionCriterion);
public sealed record TaskPlan(string Goal, IReadOnlyList<TaskStep> Steps);
public enum CompletionSource { User, Adapter, LlmSuggestionConfirmed }
public sealed record TaskProgressSnapshot(string Goal, TaskStep? CurrentStep, int CompletedCount, int TotalCount, string? PendingSuggestion);

public sealed class TaskPlanManager
{
    public TaskProgressSnapshot Start(TaskPlan plan);
    public TaskProgressSnapshot MarkCurrentComplete(CompletionSource source, DateTimeOffset at);
    public TaskProgressSnapshot SuggestCompletion(string evidence);
    public TaskProgressSnapshot ConfirmSuggestedCompletion(DateTimeOffset at);
    public TaskProgressSnapshot ReplacePlan(TaskPlan plan);
}
```

Normalize IDs/titles, require two to eight steps for LLM plans while permitting a one-step user-authored fallback, reject duplicate IDs, and keep completed-step events immutable.

- [ ] **Step 4: Run focused and full core tests and verify GREEN**

Run the focused command, then `.\.tools\dotnet\dotnet.exe test tests\Anchor.Core.Tests\Anchor.Core.Tests.csproj --nologo`.

- [ ] **Step 5: Commit the domain slice**

```powershell
git add src/Anchor.Core/Models/TaskPlan.cs src/Anchor.Core/Services/TaskPlanManager.cs src/Anchor.Core/Models/GoalSession.cs tests/Anchor.Core.Tests/TaskPlanManagerTests.cs
git commit -m "feat: add task plan and subtask progress engine"
```

### Task 2: DeepSeek structured task planning and relevance

**Files:**
- Create: `src/Anchor.Core/Models/RelevanceJudgment.cs`
- Create: `src/Anchor.Core/Services/ITaskIntelligence.cs`
- Create: `src/Anchor.Infrastructure/DeepSeek/DeepSeekClient.cs`
- Create: `src/Anchor.Infrastructure/DeepSeek/DeepSeekSettingsStore.cs`
- Modify: `src/Anchor.Infrastructure/Anchor.Infrastructure.csproj`
- Modify: `Directory.Packages.props`
- Test: `tests/Anchor.Infrastructure.Tests/DeepSeekClientTests.cs`
- Test: `tests/Anchor.Infrastructure.Tests/DeepSeekSettingsStoreTests.cs`

**Interfaces:**
- Produces: `PlanTaskAsync`, `JudgeRelevanceAsync`, `BreakDownStepAsync`, `DeepSeekAvailability`, and DPAPI-protected `DeepSeekSettings`.
- Consumes: `TaskPlan` from Task 1 and an injected `HttpClient`/clock for deterministic tests.

- [ ] **Step 1: Write failing HTTP-contract tests with a real fake handler boundary**

```csharp
[Fact]
public async Task PlanTaskAsync_sends_only_minimized_text_and_parses_valid_json()
{
    var handler = new CapturingHandler(HttpStatusCode.OK,
        """{"choices":[{"message":{"content":"{\"goal\":\"Solve three problems\",\"steps\":[{\"id\":\"q1\",\"title\":\"Solve problem 1\",\"completionCriterion\":\"Accepted submission\"},{\"id\":\"q2\",\"title\":\"Solve problem 2\",\"completionCriterion\":\"Accepted submission\"},{\"id\":\"q3\",\"title\":\"Solve problem 3\",\"completionCriterion\":\"Accepted submission\"}]}"}}]}""");
    var client = DeepSeekClient.ForTests(handler, "test-key");
    var plan = await client.PlanTaskAsync("Do 3 USACO questions", CancellationToken.None);
    Assert.Equal(3, plan.Steps.Count);
    Assert.DoesNotContain("screenshot", handler.LastBody, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("gaze", handler.LastBody, StringComparison.OrdinalIgnoreCase);
}
```

Add independent tests with literal responses: HTTP `429` and `500` must return `IsFallback == true` with reason `deepseek_unavailable`; content `{not-json` must return `IsFallback == true` with reason `invalid_deepseek_json`; two judgments for task `Solve USACO` and contexts differing only in case/outer whitespace must produce one HTTP request before the injected clock passes `ExpiresAt`, then a second request after expiry.

- [ ] **Step 2: Run tests and verify RED**

Run: `.\.tools\dotnet\dotnet.exe test tests\Anchor.Infrastructure.Tests\Anchor.Infrastructure.Tests.csproj --filter "DeepSeek" --nologo`.

Expected: compilation fails because the gateway and settings contracts do not exist.

- [ ] **Step 3: Implement the gateway against the official endpoint**

Use `POST https://api.deepseek.com/chat/completions`, `model: deepseek-flash`, `response_format: { type: "json_object" }`, non-streaming responses, an eight-second request timeout, one retry for 429/5xx with bounded jitter, and `JsonSerializer` validation into the Task 1 contracts.

```csharp
public interface ITaskIntelligence
{
    Task<TaskPlanningResult> PlanTaskAsync(string goal, CancellationToken cancellationToken);
    Task<RelevanceJudgment> JudgeRelevanceAsync(TaskContext context, CancellationToken cancellationToken);
    Task<TaskStep> BreakDownStepAsync(TaskContext context, CancellationToken cancellationToken);
}

public sealed record TaskPlanningResult(TaskPlan Plan, bool IsFallback, string Source, string? ErrorCode);
public sealed record TaskContext(
    string Goal,
    string CurrentSubtask,
    string ProcessName,
    string? WindowTitle,
    string? Domain,
    IReadOnlyList<string> UserRelevantTargets);
public enum RelevanceClass { Relevant, Ambiguous, LikelyDetour }

public sealed record RelevanceJudgment(
    double Score,
    RelevanceClass Classification,
    string Reason,
    bool IsFallback,
    DateTimeOffset ExpiresAt);
```

- [ ] **Step 4: Implement DPAPI settings with test-isolated storage**

Add `System.Security.Cryptography.ProtectedData`; store only ciphertext, model, opt-in, and endpoint under `%LOCALAPPDATA%\Anchor\settings.json`. Expose `SaveAsync`, `LoadAsync`, and `DeleteAsync`; reject non-HTTPS endpoints outside test construction.

- [ ] **Step 5: Run focused tests, mutate malformed/timeout paths, then run both .NET suites**

Run the focused command and `.\.tools\dotnet\dotnet.exe test Anchor.slnx --nologo`.

- [ ] **Step 6: Commit the intelligence boundary**

```powershell
git add Directory.Packages.props src/Anchor.Core src/Anchor.Infrastructure tests/Anchor.Infrastructure.Tests
git commit -m "feat: add DeepSeek task and relevance intelligence"
```

### Task 3: Settings task flow and visible Goal Beacon

**Files:**
- Modify: `src/Anchor.Desktop/MainPage.xaml`
- Modify: `src/Anchor.Desktop/ViewModels/MainPageViewModel.cs`
- Modify: `src/Anchor.Desktop/Services/AppServices.cs`
- Modify: `src/Anchor.Desktop/Overlays/GoalBeaconWindow.xaml`
- Modify: `src/Anchor.Desktop/Overlays/GoalBeaconWindow.xaml.cs`
- Modify: `src/Anchor.Desktop/Overlays/OverlayWindowHelper.cs`
- Create: `src/Anchor.Core/Services/BeaconAnimationModel.cs`
- Test: `tests/Anchor.Core.Tests/BeaconAnimationModelTests.cs`
- Test: `tests/Anchor.Core.Tests/SessionOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ITaskIntelligence` and `TaskPlanManager`.
- Produces: observable `CurrentSubtask`, `ProgressLabel`, editable plan, `MarkDoneCommand`, and `GoalBeaconWindow.SetGoal(goal, subtask, progress, emphasis)`.

- [ ] **Step 1: Write failing pure animation and orchestration tests**

```csharp
[Fact]
public void Shake_is_bounded_returns_to_origin_and_respects_reduced_motion()
{
    var frames = BeaconAnimationModel.CreateShake(reducedMotion: false);
    Assert.All(frames, frame => Assert.InRange(frame.X, -8, 8));
    Assert.Equal(0, frames[^1].X);
    Assert.InRange(frames[^1].At.TotalMilliseconds, 1, 700);
    Assert.Empty(BeaconAnimationModel.CreateShake(reducedMotion: true));
}
```

Add an orchestration test whose fake intelligence returns the literal three-question plan from Task 1. Start `Do 3 USACO questions` and assert that the real `TaskPlanManager` reports `q1`, `CompletedCount == 0`, and `TotalCount == 3`; then mark it complete and assert the presenter receives `q2` and `1 of 3`.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `.\.tools\dotnet\dotnet.exe test tests\Anchor.Core.Tests\Anchor.Core.Tests.csproj --filter "BeaconAnimationModel|Starting_session_plans" --nologo`.

- [ ] **Step 3: Implement task planning and progress controls in the Settings page**

Show planning state, fallback state, ordered editable steps, current step, `Done`, `Confirm completion`, and `Break into a smaller step`. Do not start sensors until planning returns or the user chooses the labeled local fallback.

- [ ] **Step 4: Rebuild the beacon as a reliably placed no-activation window**

Position from the active monitor `WorkArea`, call `Activate` before applying no-activate/click-through styles, subscribe to display changes, and update text on the UI dispatcher. Animate `Translation.X` through the tested frame sequence once per cooldown; reduced-motion uses a static accent border for 700 ms.

```csharp
public void SetGoal(string goal, string currentSubtask, string progressLabel, BeaconEmphasis emphasis);
```

- [ ] **Step 5: Add explicit `Preview beacon` and `Test shake` development controls behind Diagnostics**

These controls exercise the real presenter and window, not a mock preview. They remain in the release diagnostics so judges can verify behavior.

- [ ] **Step 6: Run all .NET tests and build the desktop project**

Run `.\.tools\dotnet\dotnet.exe test Anchor.slnx --nologo` and `.\.tools\dotnet\dotnet.exe build src\Anchor.Desktop\Anchor.Desktop.csproj -c Debug --nologo`.

- [ ] **Step 7: Commit the visible task/beacon slice**

```powershell
git add src/Anchor.Desktop src/Anchor.Core tests/Anchor.Core.Tests
git commit -m "feat: add LLM task flow and visible goal beacon"
```

### Task 4: Real gaze estimation, calibration, and adjustable Test Gaze

**Files:**
- Modify: `contracts/anchor.proto`
- Create: `src/Anchor.Core/Models/GazeSample.cs`
- Create: `src/Anchor.Worker/anchor_worker/gaze.py`
- Create: `src/Anchor.Worker/anchor_worker/calibration.py`
- Modify: `src/Anchor.Worker/anchor_worker/server.py`
- Modify: `src/Anchor.Worker/pyproject.toml`
- Create: `src/Anchor.Worker/tests/fixtures/face_center.jpg`
- Create: `src/Anchor.Worker/tests/fixtures/face_away.jpg`
- Create: `src/Anchor.Worker/tests/test_gaze.py`
- Create: `src/Anchor.Worker/tests/test_calibration.py`
- Modify: `src/Anchor.Infrastructure/Worker/InferenceWorkerClient.cs`
- Create: `src/Anchor.Desktop/Views/GazeTestView.xaml`
- Create: `src/Anchor.Desktop/Views/GazeTestView.xaml.cs`
- Create: `src/Anchor.Desktop/ViewModels/GazeTestViewModel.cs`
- Test: `tests/Anchor.Infrastructure.Tests/InferenceWorkerClientTests.cs`

**Interfaces:**
- Produces RPCs: `ListCameras`, `ConfigureGaze`, `StartGaze`, `ReadGaze`, `AddCalibrationSample`, `FinishCalibration`, and `StopGaze`.
- Produces `GazeSample(x, y, confidence, facePresent, yaw, pitch, roll, timestamp)` with normalized screen coordinates.

- [ ] **Step 1: Add failing Python tests using consent-safe fixture images**

```python
def test_center_fixture_returns_nonconstant_confident_sample(gaze_estimator, center_frame):
    sample = gaze_estimator.estimate(center_frame)
    assert sample.face_present is True
    assert 0.0 <= sample.x <= 1.0
    assert 0.0 <= sample.y <= 1.0
    assert sample.confidence >= 0.35

def test_low_landmark_confidence_disables_gaze_without_faking_center(gaze_estimator, blank_frame):
    sample = gaze_estimator.estimate(blank_frame)
    assert sample.face_present is False
    assert sample.confidence == 0.0
    assert sample.x is None and sample.y is None

def test_offsets_rotation_mirror_and_smoothing_change_output(calibrated_samples):
    # Literal expected normalized points verify each transform independently.
```

- [ ] **Step 2: Add failing robust-calibration tests**

Fit nine literal target/sample groups including one outlier; assert median validation error, transformed corner coordinates, and invalidation when display geometry changes.

- [ ] **Step 3: Run pytest and verify RED**

Run: `.\.venv\Scripts\python.exe -m pytest src\Anchor.Worker\tests\test_gaze.py src\Anchor.Worker\tests\test_calibration.py -q -p no:cacheprovider`.

Expected: imports fail because gaze/calibration modules do not exist.

- [ ] **Step 4: Install and lock worker vision/build dependencies**

Add base release dependencies `opencv-python`, `mediapipe`, and `mss`; add a `build` extra containing `pyinstaller`. Install into `.venv` only after dependency-download approval and record exact compatible versions resolved on Python 3.11/3.12.

- [ ] **Step 5: Implement camera and MediaPipe landmark pipeline**

Use one owned capture thread, bounded latest-frame queue, monotonic timestamps, MediaPipe 478-point face landmarks, iris-to-eye ratios, `solvePnP` head pose, blink rejection, confidence fusion, and exponential smoothing. No frame leaves the process.

- [ ] **Step 6: Implement calibration and live configuration**

Use robust least-squares polynomial features `[1, irisX, irisY, yaw, pitch, irisX*yaw, irisY*pitch]`, reject samples outside median absolute deviation, persist camera/display-specific coefficients, and apply mirror/rotation/offset/sensitivity/smoothing in a deterministic order pinned by tests.

- [ ] **Step 7: Extend Protobuf and worker/client implementations**

```protobuf
rpc ListCameras(ListCamerasRequest) returns (ListCamerasReply);
rpc ConfigureGaze(ConfigureGazeRequest) returns (GazeConfigurationReply);
rpc StartGaze(StartGazeRequest) returns (GazeStatusReply);
rpc ReadGaze(ReadGazeRequest) returns (GazeSampleReply);
rpc AddCalibrationSample(AddCalibrationSampleRequest) returns (CalibrationProgressReply);
rpc FinishCalibration(FinishCalibrationRequest) returns (CalibrationResultReply);
rpc StopGaze(StopGazeRequest) returns (GazeStatusReply);
```

Regenerate Python/C# stubs through the existing build pipeline and add client deadline/cancellation tests.

- [ ] **Step 8: Build Test Gaze UI against the real worker**

Display preview JPEG frames at a throttled rate, landmarks, gaze cursor, target, FPS, confidence, residual error, and all approved settings. Require all nine targets and a corner-validation pass before **Accept calibration** enables gaze fusion.

- [ ] **Step 9: Run Python, infrastructure, and desktop build verification**

Run the worker suite, `InferenceWorkerClientTests`, all .NET tests, and a Debug desktop build.

- [ ] **Step 10: Commit real gaze**

```powershell
git add contracts src/Anchor.Worker src/Anchor.Infrastructure src/Anchor.Desktop tests
git commit -m "feat: add calibrated computer vision gaze detection"
```

### Task 5: Supply gaze and semantic relevance to temporal attention fusion

**Files:**
- Create: `src/Anchor.Core/Services/AttentionFusion.cs`
- Modify: `src/Anchor.Core/Models/SensorWindow.cs`
- Modify: `src/Anchor.Core/Services/AttentionStateMachine.cs`
- Modify: `src/Anchor.Desktop/Services/WindowsSensorCoordinator.cs`
- Modify: `src/Anchor.Desktop/Services/InferenceEngineAdapter.cs`
- Modify: `src/Anchor.Worker/anchor_worker/inference.py`
- Test: `tests/Anchor.Core.Tests/AttentionFusionTests.cs`
- Test: `src/Anchor.Worker/tests/test_inference.py`

**Interfaces:**
- Consumes: live `GazeSample`, cached `RelevanceJudgment`, Windows features, browser events, progress, and manual report through `AttentionEvidence`.
- Produces: `AttentionFusion.Apply(AttentionEvidence)` returning a fused `SensorWindow` and explainable `AttentionPrediction`.

- [ ] **Step 1: Write failing fusion tests for independent and combined evidence**

```csharp
[Fact]
public void One_gaze_away_sample_never_confirms_distraction()
{
    var fusion = new AttentionFusion();
    var result = fusion.Apply(AttentionEvidence.At(
        DateTimeOffset.Parse("2026-09-20T02:00:00Z"),
        relevance: .9,
        gaze: new GazeSample(.95, .50, .9, true, 0, 0, 0,
            DateTimeOffset.Parse("2026-09-20T02:00:00Z")),
        gazeOnTaskRegion: false,
        idleSeconds: 0,
        progressObserved: true));
    Assert.NotEqual(AttentionState.Distracted, result.Prediction.State);
}
```

Add a six-window test at two-second intervals with relevance `.1`, confident gaze off the task region, no progress, and two app switches; assert the first window is not distracted and the sixth is. Add an override test where the LLM judgment is `likely_detour/.1` but `UserMarkedRelevant == true`; assert fused relevance `1.0`. Add a no-camera test with `gaze: null`; assert `SensorWindow.GazeAvailable == false` and no `gaze_absent` reason.

- [ ] **Step 2: Run tests and verify RED**

Run the focused C# tests and existing Python inference tests.

- [ ] **Step 3: Implement bounded evidence windows and fusion**

Track gaze-away duration and dispersion only when calibration confidence is valid. Resolve semantic relevance in order: secure suppression, user override, cached DeepSeek judgment, adapter evidence, labeled local fallback. Preserve reason codes that identify each contributing source.

- [ ] **Step 4: Update Python inference without duplicating task state**

The worker scores normalized features and temporal continuity; C# remains authoritative for task state and intervention policy. Remove any default that makes missing gaze look like live center gaze.

- [ ] **Step 5: Run mutation-oriented focused tests, full suites, and deterministic replays**

Verify that removing any required sustained window or override branch causes a named test to fail, then run `scripts\build.ps1 -SkipPublish`.

- [ ] **Step 6: Commit fusion**

```powershell
git add src/Anchor.Core src/Anchor.Desktop src/Anchor.Worker tests
git commit -m "feat: fuse live gaze and semantic relevance"
```

### Task 6: Immediate desktop toolkit and reliable overlay lifecycle

**Files:**
- Create: `src/Anchor.Desktop/Overlays/GazeSpotlightWindow.xaml`
- Create: `src/Anchor.Desktop/Overlays/GazeSpotlightWindow.xaml.cs`
- Create: `src/Anchor.Desktop/Overlays/WindowFirewallWindow.xaml`
- Create: `src/Anchor.Desktop/Overlays/WindowFirewallWindow.xaml.cs`
- Modify: `src/Anchor.Desktop/Overlays/VisualFilterWindow.*`
- Modify: `src/Anchor.Desktop/Services/OverlayPresenter.cs`
- Modify: `src/Anchor.Desktop/ViewModels/MainPageViewModel.cs`
- Modify: `src/Anchor.Desktop/MainPage.xaml`
- Create: `src/Anchor.Core/Services/OverlayGeometry.cs`
- Test: `tests/Anchor.Core.Tests/OverlayGeometryTests.cs`
- Test: `tests/Anchor.Core.Tests/InterventionPolicyTests.cs`

**Interfaces:**
- Produces immediate `SetToolkitStateAsync(ToolkitState)`, `PreviewAsync(ToolkitFeature)`, `UpdateGazeAsync(GazeSample)`, and deterministic `ClearAsync`.

- [ ] **Step 1: Write failing geometry and policy tests**

Use literal monitor work areas at 100%, 150%, and negative secondary-monitor coordinates. Assert that beacon, recovery card, spotlight aperture, and firewall bounds stay within the selected work area and that secure windows return no overlay bounds.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `.\.tools\dotnet\dotnet.exe test tests\Anchor.Core.Tests\Anchor.Core.Tests.csproj --filter "OverlayGeometry|InterventionPolicy" --nologo`.

- [ ] **Step 3: Implement immediate toolkit state instead of passive booleans**

Each Settings-page toggle calls the presenter immediately and exposes `Active`, `Previewing`, `Adapter unavailable`, or `Suppressed for secure window`. Add Preview buttons for spotlight, peripheral dim, window firewall, intention gate, context reminder, and pointer guard.

- [ ] **Step 4: Implement gaze spotlight and low-relevance firewall**

The spotlight follows only confident smoothed gaze at a capped movement rate and freezes/hides on low confidence. The firewall aligns a reversible dim/desaturation cover to the foreground HWND bounds. Both are click-through, no-activate, excluded on secure/system-critical windows, and cleared by the watchdog.

- [ ] **Step 5: Repair overlay creation order and active-monitor placement**

Create/activate the window, obtain valid `AppWindow`, set presenter properties, move within `DisplayArea.WorkArea`, then apply click-through/no-activate styles. Store visibility state and recreate closed windows rather than reusing invalid objects.

- [ ] **Step 6: Run all .NET tests and build**

Run all tests and build Debug/Release desktop configurations.

- [ ] **Step 7: Commit desktop toolkit**

```powershell
git add src/Anchor.Desktop tests/Anchor.Core.Tests
git commit -m "feat: make desktop focus toolkit immediately usable"
```

### Task 7: Browser native bridge and working page interventions

**Files:**
- Create: `src/Anchor.NativeBridge/Anchor.NativeBridge.csproj`
- Create: `src/Anchor.NativeBridge/Program.cs`
- Create: `src/Anchor.Infrastructure/Browser/NativeBridgeServer.cs`
- Modify: `Anchor.slnx`
- Modify: `browser/anchor-extension/manifest.json`
- Modify: `browser/anchor-extension/service-worker.js`
- Modify: `browser/anchor-extension/content-script.js`
- Modify: `browser/anchor-extension/styles.css`
- Modify: `browser/anchor-extension/tests/content-script.test.mjs`
- Create: `browser/anchor-extension/tests/service-worker.test.mjs`
- Create: `scripts/register-browser-bridge.ps1`
- Create: `scripts/unregister-browser-bridge.ps1`
- Test: `tests/Anchor.Infrastructure.Tests/NativeBridgeServerTests.cs`

**Interfaces:**
- Native host stdio: Chrome/Edge 32-bit-length-prefixed JSON.
- Desktop transport: per-launch authenticated named pipe.
- Messages: `hello`, `capabilities`, `toolkitState`, `taskState`, `pageContext`, `readingProgress`, `adapterProgress`, `showRecoveryAnchor`, and `clearInterventions`.

- [ ] **Step 1: Write failing native-message framing and reconnect tests**

Feed fragmented length-prefixed JSON to the real parser, assert one exact message; feed an oversized frame and assert rejection; disconnect the pipe and assert the bridge reconnects without dropping the next `toolkitState` snapshot.

- [ ] **Step 2: Write failing browser behavior tests**

Add tests proving that settings apply immediately, `<img>` and CSS `background-image` candidates can be filtered, dynamic DOM images are handled once, animation suppression is reversible, future-text masks do not clear unrelated blur, protected forms disable all interventions, and disconnect status is visible.

- [ ] **Step 3: Run .NET and Node focused tests and verify RED**

Run the native bridge test filter and `node --test browser\anchor-extension\tests\*.test.mjs`.

- [ ] **Step 4: Implement the bridge and per-user registration scripts**

Generate Chrome and Edge host manifests containing the absolute packaged bridge path and extension origin. Write only exact per-user registry keys under each browser's documented native-messaging host location; unregister removes only `com.anchor.desktop` keys created by Anchor.

- [ ] **Step 5: Make extension activation and settings state explicit**

The toolbar click enables the current origin and injects once; enabled origins reinject on navigation through registered content-script rules or `chrome.scripting`. The service worker requests the latest task/toolkit snapshot after reconnect. Options show desktop connection and active-origin status.

- [ ] **Step 6: Implement real page changes**

Extend visual candidate detection to CSS background images and animated elements, add mutation debouncing, use actual task/relevance/gaze-dwell messages when available, and preserve hover-to-reveal and immediate clear. Ensure all commands return an applied-count/capability response displayed in Settings diagnostics.

- [ ] **Step 7: Run focused and full browser/infrastructure suites**

Run all Node, infrastructure, and full .NET tests.

- [ ] **Step 8: Commit browser integration**

```powershell
git add Anchor.slnx src/Anchor.NativeBridge src/Anchor.Infrastructure browser scripts tests
git commit -m "feat: connect desktop toolkit to browser adapter"
```

### Task 8: LLM-informed intention gate and complete context recovery

**Files:**
- Modify: `src/Anchor.Core/Models/ContextCapsule.cs`
- Modify: `src/Anchor.Core/Services/ContextCapsuleManager.cs`
- Modify: `src/Anchor.Core/Services/SessionOrchestrator.cs`
- Modify: `src/Anchor.Desktop/Overlays/IntentionGateWindow.*`
- Modify: `src/Anchor.Desktop/Overlays/RecoveryCardWindow.*`
- Modify: `src/Anchor.Desktop/Services/OverlayPresenter.cs`
- Test: `tests/Anchor.Core.Tests/ContextCapsuleManagerTests.cs`
- Test: `tests/Anchor.Core.Tests/SessionOrchestratorTests.cs`

**Interfaces:**
- Context Capsule adds active subtask, relevance reason, evidence timestamp, restore target, and estimated-context flag.
- Gate choices: return, needed-for-task, park, deliberate-break, disable.
- Recovery choices: resume, recap, smaller-step, reopen, break, dismiss.

- [ ] **Step 1: Write failing tests for context and user authority**

Create a test-only `SessionFixture` in `SessionOrchestratorTests.cs` that assembles the real orchestrator, task-plan manager, and context-capsule manager with an in-memory event store, a deterministic inference fake, and a presenter fake that records recovery cards without replacing production behavior.

```csharp
[Fact]
public async Task Manual_report_always_opens_recovery_with_current_subtask()
{
    var fixture = SessionFixture.CreateWithPlan(currentStep: "Solve problem 2");
    await fixture.Orchestrator.ReportDistractedAsync();
    var shown = Assert.Single(fixture.Presenter.RecoveryCards);
    Assert.Equal("Solve problem 2", shown.Capsule!.CurrentSubtask);
    Assert.Equal("manual_report", shown.Decision.ReasonCode);
}
```

Add a gate test that applies `NeededForTask` to `codeforces.com`, closes the gate, and verifies the next identical context resolves to user-overridden relevance `1.0`. Add a breakdown test whose intelligence fake throws `TimeoutException`; assert the card remains open, uses the current plan's next action, and displays source label `Local fallback`.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Extend capsule capture with adapter and plan evidence**

Store only redacted titles/domains and coarse locations. Update only on sufficient confidence; manual recovery freezes the last safe capsule and never overwrites it with the displaced gaze/current detour.

- [ ] **Step 4: Rebuild gate and reminder UI for visibility**

Use active-monitor centering, no hidden auto-dismiss, clear task/subtask/reason text, keyboard focus, and accessible names. `Reopen` uses only validated local targets/adapters; otherwise it highlights the saved identity. `Break down` displays DeepSeek or fallback source.

- [ ] **Step 5: Run all core/desktop tests and build**

- [ ] **Step 6: Commit recovery**

```powershell
git add src/Anchor.Core src/Anchor.Desktop tests/Anchor.Core.Tests
git commit -m "feat: add visible intention and context recovery flows"
```

### Task 9: Tray icon and Settings-page background lifecycle

**Files:**
- Create: `src/Anchor.Desktop/Services/TrayIconService.cs`
- Create: `src/Anchor.Core/Services/AppLifecycleModel.cs`
- Modify: `src/Anchor.Desktop/Anchor.Desktop.csproj`
- Modify: `src/Anchor.Desktop/App.xaml.cs`
- Modify: `src/Anchor.Desktop/MainWindow.xaml.cs`
- Modify: `src/Anchor.Desktop/MainPage.xaml`
- Test: `tests/Anchor.Infrastructure.Tests/AppLifecycleModelTests.cs`

**Interfaces:**
- Produces: `ShowSettings`, `HideSettings`, `ReportDistracted`, `EmergencyRelease`, and `Exit` tray actions.
- Uses a pure `AppLifecycleModel` for testable transitions; WinForms `NotifyIcon` is a thin adapter.

- [ ] **Step 1: Write failing lifecycle transition tests**

Assert that closing during a running session hides Settings and keeps sensors; closing while idle exits only after explicit user choice; tray restore shows Settings; Exit stops recorder, session, bridge, worker, overlays, hotkeys, and icon in order.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement stationary tray icon and menus**

Enable Windows Forms integration, load the existing icon, never animate it, and marshal actions to the WinUI dispatcher. Menu items show current goal/subtask, open Settings, report distraction, release overlays, and exit.

- [ ] **Step 4: Rename foreground UI copy to Settings page and centralize shutdown**

Ensure close/hide does not unload or dispose the active ViewModel. Make one awaited shutdown path idempotent and log each component release.

- [ ] **Step 5: Run lifecycle tests and desktop build**

- [ ] **Step 6: Commit lifecycle**

```powershell
git add src/Anchor.Desktop tests/Anchor.Infrastructure.Tests
git commit -m "feat: add tray and settings background lifecycle"
```

### Task 10: Screen, gaze, and intervention study recorder

**Files:**
- Modify: `contracts/anchor.proto`
- Create: `src/Anchor.Core/Models/StudyRecording.cs`
- Create: `src/Anchor.Worker/anchor_worker/recording.py`
- Modify: `src/Anchor.Worker/anchor_worker/server.py`
- Create: `src/Anchor.Worker/tests/test_recording.py`
- Modify: `src/Anchor.Infrastructure/Worker/InferenceWorkerClient.cs`
- Create: `src/Anchor.Infrastructure/Recording/StudyRecordingClient.cs`
- Create: `src/Anchor.Desktop/Services/StudyRecordingService.cs`
- Test: `tests/Anchor.Infrastructure.Tests/StudyRecordingClientTests.cs`

**Interfaces:**
- RPCs: `StartRecording`, `AppendRecordingEvent`, `GetRecordingStatus`, and `StopRecording`.
- Produces: playable `.mp4`, `.events.jsonl`, `.samples.csv`, and `.summary.json` sharing one trial ID.

- [ ] **Step 1: Write failing recorder tests with generated frames**

```python
def test_records_playable_mp4_and_aligned_metrics(tmp_path, fake_screen, gaze_samples):
    recorder = StudyRecorder(fps=15, capture=fake_screen)
    manifest = recorder.start(tmp_path, trial_mode="anchor_enabled", participant_code="P01")
    recorder.append_event({"type": "intervention", "at_ms": 120})
    recorder.stop()
    assert read_video_frame_count(manifest.video_path) >= 3
    assert json.loads(manifest.summary_path.read_text())["trialMode"] == "anchor_enabled"

```

Add a baseline test with three gaze samples and one would-be intervention; assert the CSV contains all three samples, the summary says `interventionsEnabled: false`, and the event log contains no presented intervention. Add a capture-failure test whose frame source raises after three frames; assert status becomes `failed`, the error code is `screen_capture_failed`, and either OpenCV can read at least one finalized frame or the manifest explicitly marks `videoUsable: false` without claiming success.

- [ ] **Step 2: Run Python tests and verify RED**

- [ ] **Step 3: Implement bounded screen capture and MP4 writer**

Use MSS for the selected display, OpenCV `VideoWriter` with verified MP4 playback, a bounded frame queue that drops stale frames, and one writer thread. Composite gaze point/confidence, task/subtask, state/reasons, intervention markers, recording indicator, and optional consented webcam thumbnail. Audio remains off unless separately enabled; if reliable audio muxing cannot pass the acceptance test, leave the control disabled and labeled unavailable rather than generating silent/corrupt audio.

- [ ] **Step 4: Implement privacy-safe event and summary output**

Use monotonic-relative timestamps, pseudonymous participant code validation, redacted context, flush intervals, disk-space checks, atomic manifest writes, and finalization on cancellation/error.

- [ ] **Step 5: Add worker RPC and C# orchestration tests**

Test authenticated start, duplicate-start rejection, status, event append, normal stop, worker loss, and path validation restricted to the user-selected output folder.

- [ ] **Step 6: Integrate baseline mode with intervention policy**

Baseline mode keeps sensing/fusion/recording but forces policy output to `None`, records that interventions were disabled, and retains the manual stop/emergency controls.

- [ ] **Step 7: Run recorder, worker, infrastructure, and full suites**

- [ ] **Step 8: Commit recorder**

```powershell
git add contracts src/Anchor.Core src/Anchor.Worker src/Anchor.Infrastructure src/Anchor.Desktop tests
git commit -m "feat: record screen gaze and intervention study trials"
```

### Task 11: Settings recording controls and comparison button

**Files:**
- Create: `src/Anchor.Core/Services/RecordingComparison.cs`
- Create: `src/Anchor.Desktop/Views/RecordingsView.xaml`
- Create: `src/Anchor.Desktop/Views/RecordingsView.xaml.cs`
- Create: `src/Anchor.Desktop/ViewModels/RecordingsViewModel.cs`
- Modify: `src/Anchor.Desktop/MainPage.xaml`
- Test: `tests/Anchor.Core.Tests/RecordingComparisonTests.cs`

**Interfaces:**
- Produces `RecordingComparison.Compare(StudySummary baseline, StudySummary enabled)` and Settings commands `StartRecording`, `StopRecording`, `CompareRecordings`, and `OpenOutputFolder`.

- [ ] **Step 1: Write failing literal metric-difference tests**

```csharp
[Fact]
public void Compare_reports_directional_differences_without_clinical_claims()
{
    var baseline = new StudySummary("b", TrialMode.Baseline, 600, .60, 180, 240, 8, 40, 1);
    var enabled = new StudySummary("a", TrialMode.AnchorEnabled, 600, .85, 60, 90, 4, 15, 3);
    var result = RecordingComparison.Compare(baseline, enabled);
    Assert.Equal(0.25, result.UsableGazeCoverageDelta, 3);
    Assert.Equal(-120, result.GazeAwaySecondsDelta);
    Assert.Contains("observational", result.Disclaimer, StringComparison.OrdinalIgnoreCase);
}

```

Add three validation tests: two baseline summaries return `one_baseline_and_one_enabled_required`; a summary with duration `0` returns `invalid_duration`; schema versions `1` and `2` return `metric_schema_mismatch`. None may produce a comparison object.

- [ ] **Step 2: Run tests and verify RED**

- [ ] **Step 3: Implement pure comparison and validated loaders**

Never infer missing values as zero. Require one baseline and one Anchor-enabled summary, compatible metric schema versions, positive durations, and readable associated manifests.

- [ ] **Step 4: Add Settings-page recording area**

Show trial mode, participant code, display, webcam-consent toggle, output folder, recording indicator, timer, stop, latest files, and exact errors. Disable settings that cannot change during a recording.

- [ ] **Step 5: Add visible `Compare recordings` button and inline report**

The button exists only in the open Settings page, opens two file pickers/selectors, validates modes, and displays cards for gaze coverage, gaze-away duration, low-relevance time, interruptions, recovery latency, and subtasks completed plus the observational disclaimer.

- [ ] **Step 6: Run core tests and desktop build**

- [ ] **Step 7: Commit comparison UI**

```powershell
git add src/Anchor.Core src/Anchor.Desktop tests/Anchor.Core.Tests
git commit -m "feat: add settings recording comparison"
```

### Task 12: Release packaging with bundled worker and bridge

**Files:**
- Modify: `scripts/build.ps1`
- Create: `scripts/build-worker.ps1`
- Create: `scripts/verify-release.ps1`
- Modify: `src/Anchor.Desktop/Services/AppServices.cs`
- Modify: `README.md`
- Modify: `docs/privacy.md`
- Modify: `docs/demo-script.md`
- Modify: `.gitignore`

**Interfaces:**
- Produces `release/Anchor-win-x64/Anchor.exe`, `Anchor.VisionWorker.exe`, `Anchor.NativeBridge.exe`, extension, setup/removal scripts, README, and licenses.

- [ ] **Step 1: Write a failing release verifier before changing packaging**

`verify-release.ps1` must run the packaged worker health command, launch `Anchor.exe`, wait for a titled responsive Settings window, verify worker/bridge files and extension manifest, then request graceful shutdown. It returns nonzero for a missing sidecar, absent window, protocol mismatch, or unclean shutdown.

- [ ] **Step 2: Run the verifier against the current artifact and verify RED**

Expected: fails because no bundled vision worker/native bridge exists and the output layout is incomplete.

- [ ] **Step 3: Build the Python worker with PyInstaller**

Use a pinned `.spec` file collecting MediaPipe data/native libraries and OpenCV. Build one `Anchor.VisionWorker.exe`; run its `--health-json` and `--camera-list-json` commands from a directory without `.venv` on `PATH`.

- [ ] **Step 4: Publish and assemble exact release layout**

Publish desktop and bridge self-contained for `win-x64`, copy extension/setup files, generate native-host manifests at registration time so absolute paths are correct, include third-party licenses, and preserve the existing safe target-path validation before removing prior outputs.

- [ ] **Step 5: Make runtime path discovery release-first**

`AppServices` looks next to `Anchor.exe` for worker/bridge, validates protocol versions, logs capability state, and only uses repository `.venv` in explicit Development mode.

- [ ] **Step 6: Run `scripts\build.ps1` and release verifier**

The build order is restore, .NET tests, Python tests, browser tests, deterministic replays, worker build, desktop/bridge publish, release assembly, and verifier.

- [ ] **Step 7: Commit packaging and documentation**

```powershell
git add scripts src/Anchor.Desktop README.md docs .gitignore
git commit -m "build: package Anchor with vision worker and browser bridge"
```

### Task 13: Deterministic integration and failure-mode replays

**Files:**
- Modify: `src/Anchor.Core/Services/ReplayScenarioRunner.cs`
- Create: `demo/replay/gaze-away-detour.jsonl`
- Create: `demo/replay/relevant-research.jsonl`
- Create: `demo/replay/subtask-completion.jsonl`
- Create: `demo/replay/worker-and-llm-failure.jsonl`
- Modify: `tests/Anchor.Core.Tests/ReplayScenarioTests.cs`
- Modify: `src/Anchor.Demo/Program.cs`

**Interfaces:**
- Replays all new evidence types without camera/network dependence and asserts state, intervention, task step, and fallback source at each checkpoint.

- [ ] **Step 1: Write failing replay assertions**

Add literal expected checkpoints for sustained multimodal detour, relevant USACO research, adapter-confirmed step completion, gaze dropout, worker loss, DeepSeek timeout, and manual recovery.

- [ ] **Step 2: Run replays and verify RED for unsupported event fields**

- [ ] **Step 3: Extend replay parser and runner through public domain APIs**

Do not duplicate production state logic in the runner. Replays feed the same task manager, fusion engine, policy, and orchestrator used by the app.

- [ ] **Step 4: Run all replay and full automated suites**

- [ ] **Step 5: Commit replay coverage**

```powershell
git add demo/replay src/Anchor.Core src/Anchor.Demo tests/Anchor.Core.Tests
git commit -m "test: cover multimodal release scenarios"
```

### Task 14: Visible Windows, browser, document, gaze, and recording acceptance

**Files:**
- Create: `docs/release-acceptance.md`
- Modify: `docs/demo-script.md`
- Modify: `README.md`
- Update only if defects are found: owning production/test files from Tasks 1–13, using a new failing regression test before each fix.

**Interfaces:**
- Validates the assembled release as a user experiences it; no success claim may rely only on process existence or unit tests.

- [ ] **Step 1: Run the complete clean build and preserve logs**

Run `scripts\build.ps1`; record exact pass counts and release paths in `docs/release-acceptance.md`.

- [ ] **Step 2: Launch packaged `Anchor.exe` and verify the visible Settings page**

Use Windows UI automation/screenshot inspection to verify title, task controls, Test Gaze, toolkit, recording controls, Compare recordings, diagnostics, and tray lifecycle.

- [ ] **Step 3: Perform real camera calibration and parameter checks**

With explicit camera-permission confirmation, verify face/eye landmarks, nonconstant gaze, nine targets, corner validation, mirror/rotation/offset/smoothing changes, reset, and camera-off fallback. Record measured FPS and calibration error without storing unconsented frames.

- [ ] **Step 4: Install/register the unpacked browser adapter with action-time confirmation**

Register the native host, load the release extension in the user's chosen browser, and confirm Settings diagnostics report the connection. Unregister/remove only if the user requests cleanup after the test.

- [ ] **Step 5: Test listed sites**

Use public, non-authenticated pages from `usaco.guide`, `usaco.org`, `codeforces.com`, `luogu.com.cn`, `youtube.com`, `collegeboard.org`, and the public portion of `shs.blackboardchina.cn`. Verify relevant/ambiguous/detour judgments, immediate blur/mask/animation changes, user relevance correction, and safe exclusion. Do not log in, submit, like, comment, or alter accounts.

- [ ] **Step 6: Test a local PDF and local document**

Verify foreground relevance, beacon visibility, gaze spotlight, peripheral dim, window firewall, pointer release, manual recovery, and graceful labeling of unavailable paragraph-level semantics.

- [ ] **Step 7: Verify DeepSeek task progress end to end**

With a user-supplied key entered by the user or supplied through the approved local settings flow, start **Do 3 USACO questions**, inspect the generated plan, mark/confirm step completion, and verify the Settings page and Goal Beacon advance together. Confirm an unavailable-key run remains usable and labeled Local fallback.

- [ ] **Step 8: Verify beacon shake and context reminder visibly**

Trigger a deterministic diagnostic distraction and a live sustained detour. Confirm one bounded shake, no continuous motion, correct reduced-motion behavior, and visible recovery after both automatic detection and **I'm distracted**.

- [ ] **Step 9: Record and compare two short trials**

Record one baseline and one Anchor-enabled trial with screen and gaze overlay, stop both, play both MP4 files, validate JSONL/CSV/summary files, press **Compare recordings**, select the two trials, and inspect metric deltas/disclaimer in Settings.

- [ ] **Step 10: Fix every observed defect test-first, then repeat the affected visible test**

For each failure, capture root-cause evidence, add the smallest automated regression test, watch it fail, implement the fix, run the owning suite, and repeat the visible scenario.

- [ ] **Step 11: Run final verification and consistency review**

Run full build, release verifier, `git diff --check`, clean-status audit, package launch/shutdown, all acceptance rows, and verify README/privacy/demo documentation matches actual behavior and degraded modes.

- [ ] **Step 12: Commit acceptance evidence**

```powershell
git add docs/release-acceptance.md docs/demo-script.md README.md
git commit -m "test: verify Anchor release end to end"
```

## Plan self-review result

- **Spec coverage:** All release-spec sections map to Tasks 1–14, including Test Gaze adjustments, DeepSeek planning/relevance, dynamic beacon text/shake, immediate toolkit behavior, recovery, study recording, Settings-page comparison, packaging, and real-site/document testing.
- **Completeness scan:** Every implementation step names concrete inputs and observable results; optional audio has an explicit pass-or-disable rule.
- **Type consistency:** Task contracts originate in Task 1; DeepSeek, beacon, recovery, recording, and replay tasks consume those same names. Gaze contracts originate in Task 4 and flow through Task 5 into overlays and recording.
- **Review-focus coverage:** The five high-risk conditions each have named automated tests and a visible acceptance step.
- **Integration order:** Contracts precede adapters; real gaze precedes fusion; fusion precedes intervention/recording; packaging precedes visible acceptance.
