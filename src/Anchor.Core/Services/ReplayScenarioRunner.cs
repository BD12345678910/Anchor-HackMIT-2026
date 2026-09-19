using System.Text.Json;
using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed record ReplayStepResult(
    int AtSeconds,
    AttentionState State,
    InterventionKind Intervention,
    bool ExpectationMatched,
    string ExpectationMessage);

public sealed record ReplayReport(
    string Scenario,
    AttentionState FinalState,
    IReadOnlyList<InterventionKind> Interventions,
    ProgressSnapshot Progress,
    ContextCapsule? LastContextCapsule,
    IReadOnlyList<ReplayStepResult> Steps);

public static class ReplayScenarioRunner
{
    public static async Task<ReplayReport> RunFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var steps = await ReadStepsAsync(path, cancellationToken);
        if (steps.Count == 0)
        {
            throw new InvalidDataException("Replay scenario contains no events.");
        }

        var now = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
        var store = new ReplayStore();
        var inference = new ReplayInference();
        var presenter = new ReplayPresenter();
        var orchestrator = new SessionOrchestrator(
            store,
            inference,
            new ReplaySensors(),
            presenter,
            () => now);
        var title = steps[0].TaskTitle ?? Path.GetFileNameWithoutExtension(path);
        await orchestrator.StartAsync(title, cancellationToken);

        var results = new List<ReplayStepResult>(steps.Count);
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            now = DateTimeOffset.Parse("2026-09-20T00:00:00Z").AddSeconds(step.AtSeconds);
            orchestrator.ObserveContext(new ContextObservation(
                Application: step.Application ?? "Replay Reader",
                DocumentIdentity: step.Document ?? "Demo document",
                Location: step.Location ?? $"Replay second {step.AtSeconds}",
                LastAction: step.LastAction ?? "Read the current section",
                NextAction: step.NextAction ?? "Continue with the next sentence",
                SelectedText: step.SelectedText,
                RestoreTarget: null,
                Confidence: 0.9,
                IsSensitiveField: false));

            var interventionCount = presenter.Interventions.Count;
            AttentionPrediction prediction;
            if (string.Equals(step.Action, "manual", StringComparison.OrdinalIgnoreCase))
            {
                prediction = await orchestrator.ReportDistractedAsync(cancellationToken);
            }
            else
            {
                prediction = await orchestrator.ProcessAsync(
                    SensorWindow.Create(
                        step.KeyCount,
                        step.MouseDistance,
                        step.IdleSeconds,
                        step.AppRelevance,
                        step.GazePresence,
                        step.AppSwitchCount,
                        step.ScrollReversalCount,
                        step.IsSecureWindow,
                        step.WorkerAvailable,
                        timestamp: now),
                    cancellationToken);
            }

            var intervention = presenter.Interventions.Count > interventionCount
                ? presenter.Interventions[^1]
                : InterventionKind.None;
            var stateMatches = string.IsNullOrWhiteSpace(step.ExpectedState)
                || Enum.TryParse<AttentionState>(step.ExpectedState, true, out var expectedState)
                    && expectedState == prediction.State;
            var interventionMatches = string.IsNullOrWhiteSpace(step.ExpectedIntervention)
                || Enum.TryParse<InterventionKind>(step.ExpectedIntervention, true, out var expectedIntervention)
                    && expectedIntervention == intervention;
            results.Add(new ReplayStepResult(
                step.AtSeconds,
                prediction.State,
                intervention,
                stateMatches && interventionMatches,
                $"Expected {step.ExpectedState}/{step.ExpectedIntervention}, got {prediction.State}/{intervention}."));
        }

        var finalState = orchestrator.LastPrediction?.State ?? AttentionState.Unknown;
        var progress = orchestrator.Progress ?? new ProgressSnapshot(
            orchestrator.CurrentSession!.Id,
            0,
            0,
            0,
            now);
        await orchestrator.StopAsync(cancellationToken);
        return new ReplayReport(
            Path.GetFileNameWithoutExtension(path),
            finalState,
            presenter.Interventions,
            progress,
            presenter.LastCapsule,
            results);
    }

    private static async Task<IReadOnlyList<ReplayStep>> ReadStepsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = new List<ReplayStep>();
        var lineNumber = 0;
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            try
            {
                result.Add(JsonSerializer.Deserialize<ReplayStep>(line, options)
                    ?? throw new JsonException("Replay step was empty."));
            }
            catch (JsonException error)
            {
                throw new InvalidDataException($"Invalid replay JSON on line {lineNumber}.", error);
            }
        }

        return result;
    }

    private sealed record ReplayStep
    {
        public int AtSeconds { get; init; }
        public string Action { get; init; } = "sensor";
        public string? TaskTitle { get; init; }
        public int KeyCount { get; init; }
        public double MouseDistance { get; init; }
        public double IdleSeconds { get; init; }
        public double AppRelevance { get; init; } = 0.5;
        public double GazePresence { get; init; } = 0.5;
        public int AppSwitchCount { get; init; }
        public int ScrollReversalCount { get; init; }
        public bool IsSecureWindow { get; init; }
        public bool WorkerAvailable { get; init; }
        public string? Application { get; init; }
        public string? Document { get; init; }
        public string? Location { get; init; }
        public string? LastAction { get; init; }
        public string? NextAction { get; init; }
        public string? SelectedText { get; init; }
        public string? ExpectedState { get; init; }
        public string? ExpectedIntervention { get; init; }
    }

    private sealed class ReplayStore : IEventStore
    {
        private readonly List<DerivedEvent> _events = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendAsync(DerivedEvent item, CancellationToken cancellationToken = default)
        {
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

    private sealed class ReplayInference : IInferenceEngine
    {
        private readonly AttentionStateMachine _machine = AttentionStateMachine.CreateDefault();
        public bool IsAvailable => true;
        public Task<bool> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<AttentionPrediction> PredictAsync(SensorWindow window, CancellationToken cancellationToken = default) =>
            Task.FromResult(_machine.Update(window));
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReplaySensors : ISensorCoordinator
    {
        public Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReplayPresenter : IInterventionPresenter
    {
        public List<InterventionKind> Interventions { get; } = [];
        public ContextCapsule? LastCapsule { get; private set; }
        public Task PresentAsync(InterventionDecision decision, ContextCapsule? capsule, CancellationToken cancellationToken = default)
        {
            Interventions.Add(decision.Kind);
            LastCapsule = capsule ?? LastCapsule;
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
