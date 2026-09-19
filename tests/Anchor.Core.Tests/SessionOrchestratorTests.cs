using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Core.Tests;

public sealed class SessionOrchestratorTests
{
    [Fact]
    public async Task Start_creates_one_active_session_and_starts_dependencies()
    {
        var fixture = new Fixture();

        var session = await fixture.Orchestrator.StartAsync("  Read research paper  ");

        Assert.True(fixture.Orchestrator.IsRunning);
        Assert.Equal("Read research paper", session.Title);
        Assert.Equal(1, fixture.Sensors.StartCalls);
        Assert.Equal(1, fixture.Inference.StartCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.StartAsync("Second task"));
    }

    [Fact]
    public async Task Manual_distraction_always_presents_recovery_card()
    {
        var fixture = new Fixture();
        await fixture.Orchestrator.StartAsync("Compare results");
        fixture.Orchestrator.ObserveContext(new ContextObservation(
            "PDF Reader", "paper.pdf", "section 3", "Read result",
            "Compare control group", null, "paper.pdf#page=8", 0.9, false));

        await fixture.Orchestrator.ReportDistractedAsync();

        var presentation = Assert.Single(fixture.Presenter.Presentations);
        Assert.Equal(InterventionKind.RecoveryCard, presentation.Decision.Kind);
        Assert.Equal("section 3", presentation.Capsule?.Location);
    }

    [Fact]
    public async Task Manual_report_without_anchor_still_shows_current_subtask()
    {
        var fixture = new Fixture();
        await fixture.Orchestrator.StartAsync("Solve three USACO problems");
        fixture.Orchestrator.UpdateTaskContext("Solve problem 2", "Read the statement and identify inputs");

        await fixture.Orchestrator.ReportDistractedAsync();

        var presentation = Assert.Single(fixture.Presenter.Presentations);
        Assert.Equal("manual_report", presentation.Decision.ReasonCode);
        Assert.Equal("Solve problem 2", presentation.Capsule?.CurrentSubtask);
        Assert.True(presentation.Capsule?.IsEstimatedContext);
    }

    [Fact]
    public async Task Missing_worker_keeps_session_in_deterministic_mode()
    {
        var fixture = new Fixture(workerAvailable: false);
        await fixture.Orchestrator.StartAsync("Write tests");

        var prediction = await fixture.Orchestrator.ProcessAsync(SensorWindow.Create(
            keyCount: 6,
            mouseDistance: 20,
            idleSeconds: 0,
            appRelevance: 0.9));

        Assert.True(fixture.Orchestrator.IsRunning);
        Assert.Contains("worker_unavailable", prediction.ReasonCodes);
        Assert.Equal("Deterministic", fixture.Orchestrator.CapabilityStatus);
    }

    [Fact]
    public async Task Repeated_dismissals_reduce_future_intervention_sensitivity()
    {
        var fixture = new Fixture();
        await fixture.Orchestrator.StartAsync("Write tests");
        var initial = fixture.Orchestrator.InterventionThreshold;

        fixture.Orchestrator.RecordInterventionResponse(InterventionResponse.Dismissed);
        fixture.Orchestrator.RecordInterventionResponse(InterventionResponse.Dismissed);

        Assert.True(fixture.Orchestrator.InterventionThreshold > initial);
    }

    [Fact]
    public async Task Stop_clears_presentations_and_stops_all_dependencies()
    {
        var fixture = new Fixture();
        await fixture.Orchestrator.StartAsync("Write tests");

        await fixture.Orchestrator.StopAsync();

        Assert.False(fixture.Orchestrator.IsRunning);
        Assert.Equal(1, fixture.Presenter.ClearCalls);
        Assert.Equal(1, fixture.Sensors.StopCalls);
        Assert.Equal(1, fixture.Inference.StopCalls);
    }

    [Fact]
    public async Task Cancelled_start_does_not_leave_a_partial_session()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Orchestrator.StartAsync("Write tests", cancellation.Token));

        Assert.False(fixture.Orchestrator.IsRunning);
        Assert.Equal(0, fixture.Sensors.StartCalls);
    }

    [Fact]
    public async Task Secure_window_immediately_clears_existing_interventions()
    {
        var fixture = new Fixture();
        await fixture.Orchestrator.StartAsync("Read a paper");

        await fixture.Orchestrator.ProcessAsync(SensorWindow.Create(
            keyCount: 0,
            mouseDistance: 0,
            idleSeconds: 0,
            appRelevance: 0.5,
            isSecureWindow: true));

        Assert.Equal(1, fixture.Presenter.ClearCalls);
        Assert.Empty(fixture.Presenter.Presentations);
    }

    [Fact]
    public async Task Stop_releases_sensors_and_worker_even_when_presenter_cleanup_fails()
    {
        var fixture = new Fixture();
        await fixture.Orchestrator.StartAsync("Read a paper");
        fixture.Presenter.ThrowOnClear = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.StopAsync());

        Assert.False(fixture.Orchestrator.IsRunning);
        Assert.Equal(1, fixture.Sensors.StopCalls);
        Assert.Equal(1, fixture.Inference.StopCalls);
    }

    private sealed class Fixture
    {
        public Fixture(bool workerAvailable = true)
        {
            Store = new MemoryEventStore();
            Inference = new FakeInference(workerAvailable);
            Sensors = new FakeSensors();
            Presenter = new FakePresenter();
            Orchestrator = new SessionOrchestrator(
                Store,
                Inference,
                Sensors,
                Presenter,
                () => DateTimeOffset.UnixEpoch);
        }

        public MemoryEventStore Store { get; }
        public FakeInference Inference { get; }
        public FakeSensors Sensors { get; }
        public FakePresenter Presenter { get; }
        public SessionOrchestrator Orchestrator { get; }
    }

    private sealed class FakeInference(bool available) : IInferenceEngine
    {
        private readonly AttentionStateMachine _fallback = AttentionStateMachine.CreateDefault();
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public bool IsAvailable => available;

        public Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            return Task.FromResult(available);
        }

        public Task<AttentionPrediction> PredictAsync(SensorWindow window, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (window.IsManualReport)
            {
                return Task.FromResult(AttentionPrediction.Create(
                    AttentionState.Distracted, 1, 1, ["manual_report"]));
            }

            var result = _fallback.Update(window with { IsWorkerAvailable = available });
            return Task.FromResult(result);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSensors : ISensorCoordinator
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePresenter : IInterventionPresenter
    {
        public List<(InterventionDecision Decision, ContextCapsule? Capsule)> Presentations { get; } = [];
        public int ClearCalls { get; private set; }
        public bool ThrowOnClear { get; set; }

        public Task PresentAsync(
            InterventionDecision decision,
            ContextCapsule? capsule,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Presentations.Add((decision, capsule));
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCalls++;
            if (ThrowOnClear)
            {
                throw new InvalidOperationException("Presenter cleanup failed.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryEventStore : IEventStore
    {
        private readonly List<DerivedEvent> _events = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task AppendAsync(DerivedEvent item, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add(item);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DerivedEvent>> QuerySessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DerivedEvent>>(_events.Where(item => item.SessionId == sessionId).ToArray());

        public Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            _events.RemoveAll(item => item.SessionId == sessionId);
            return Task.CompletedTask;
        }

        public Task DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            _events.Clear();
            return Task.CompletedTask;
        }
    }
}
