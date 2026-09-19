using System.Globalization;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

public interface IInferenceEngine
{
    bool IsAvailable { get; }
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    Task<AttentionPrediction> PredictAsync(SensorWindow window, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface ISensorCoordinator
{
    Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IInterventionPresenter
{
    Task PresentAsync(
        InterventionDecision decision,
        ContextCapsule? capsule,
        CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class SessionOrchestrator
{
    private readonly IEventStore _store;
    private readonly IInferenceEngine _inference;
    private readonly ISensorCoordinator _sensors;
    private readonly IInterventionPresenter _presenter;
    private readonly Func<DateTimeOffset> _clock;
    private readonly InterventionPolicy _policy;
    private ContextCapsuleManager? _capsules;
    private ProgressTracker? _progress;

    public SessionOrchestrator(
        IEventStore store,
        IInferenceEngine inference,
        ISensorCoordinator sensors,
        IInterventionPresenter presenter,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inference = inference ?? throw new ArgumentNullException(nameof(inference));
        _sensors = sensors ?? throw new ArgumentNullException(nameof(sensors));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _policy = new InterventionPolicy(_clock);
    }

    public GoalSession? CurrentSession { get; private set; }
    public AttentionPrediction? LastPrediction { get; private set; }
    public ProgressSnapshot? Progress { get; private set; }
    public bool IsRunning => CurrentSession is not null;
    public string CapabilityStatus => _inference.IsAvailable ? "Multimodal" : "Deterministic";
    public double InterventionThreshold => _policy.CurrentThreshold;

    public async Task<GoalSession> StartAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CurrentSession is not null)
        {
            throw new InvalidOperationException("A task session is already active.");
        }

        var session = GoalSession.Create(title, _clock());
        await _store.InitializeAsync(cancellationToken);
        try
        {
            await _inference.StartAsync(cancellationToken);
            await _sensors.StartAsync(session.Id, cancellationToken);
        }
        catch
        {
            await StopDependenciesAfterFailedStartAsync();
            throw;
        }

        CurrentSession = session;
        _capsules = new ContextCapsuleManager(session.Id, session.Title);
        _progress = new ProgressTracker(session.Id);
        await _store.AppendAsync(
            DerivedEvent.Create(session.Id, session.StartedAt, "session", "started"),
            cancellationToken);
        return session;
    }

    public ContextCapsule? ObserveContext(ContextObservation observation)
    {
        EnsureRunning();
        return _capsules!.Observe(observation);
    }

    public async Task<AttentionPrediction> ProcessAsync(
        SensorWindow window,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        cancellationToken.ThrowIfCancellationRequested();

        var prediction = await _inference.PredictAsync(window, cancellationToken);
        LastPrediction = prediction;
        var attentionEvent = DerivedEvent.Create(
            CurrentSession!.Id,
            window.Timestamp,
            "attention",
            prediction.State.ToString().ToLowerInvariant(),
            new Dictionary<string, string>
            {
                ["confidence"] = prediction.Confidence.ToString("0.000", CultureInfo.InvariantCulture),
                ["distraction_probability"] = prediction.DistractionProbability.ToString("0.000", CultureInfo.InvariantCulture),
                ["reasons"] = string.Join(',', prediction.ReasonCodes)
            });
        await _store.AppendAsync(attentionEvent, cancellationToken);
        Progress = _progress!.Apply(attentionEvent);

        var decision = _policy.Decide(prediction, UserPreferences.Default);
        if (decision.Kind != InterventionKind.None)
        {
            ContextCapsule? capsule = null;
            if (decision.Kind == InterventionKind.RecoveryCard)
            {
                try
                {
                    capsule = _capsules!.Freeze(
                        prediction.ReasonCodes.Contains("manual_report", StringComparer.Ordinal)
                            ? DistractionReason.ManualReport
                            : DistractionReason.IdleReturn);
                }
                catch (InvalidOperationException)
                {
                    // Recovery still works with the task title when no safe anchor exists.
                }
            }

            await _presenter.PresentAsync(decision, capsule, cancellationToken);
            await _store.AppendAsync(
                DerivedEvent.Create(
                    CurrentSession.Id,
                    window.Timestamp,
                    "intervention",
                    decision.Kind.ToString().ToLowerInvariant(),
                    new Dictionary<string, string> { ["reason"] = decision.ReasonCode }),
                cancellationToken);
        }

        return prediction;
    }

    public Task<AttentionPrediction> ReportDistractedAsync(CancellationToken cancellationToken = default) =>
        ProcessAsync(
            SensorWindow.Create(
                keyCount: 0,
                mouseDistance: 0,
                idleSeconds: 0,
                appRelevance: 1,
                isWorkerAvailable: _inference.IsAvailable,
                isManualReport: true,
                timestamp: _clock()),
            cancellationToken);

    public void RecordInterventionResponse(InterventionResponse response) =>
        _policy.RecordResponse(response);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentSession is null)
        {
            return;
        }

        var session = CurrentSession;
        await _presenter.ClearAsync(cancellationToken);
        await _sensors.StopAsync(cancellationToken);
        await _inference.StopAsync(cancellationToken);
        await _store.AppendAsync(
            DerivedEvent.Create(session.Id, _clock(), "session", "stopped"),
            cancellationToken);

        CurrentSession = null;
        _capsules = null;
        _progress = null;
        LastPrediction = null;
        Progress = null;
    }

    private void EnsureRunning()
    {
        if (CurrentSession is null)
        {
            throw new InvalidOperationException("No task session is active.");
        }
    }

    private async Task StopDependenciesAfterFailedStartAsync()
    {
        try
        {
            await _sensors.StopAsync();
        }
        finally
        {
            await _inference.StopAsync();
        }
    }
}
