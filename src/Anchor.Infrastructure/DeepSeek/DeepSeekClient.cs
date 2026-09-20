using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Anchor.Core.Models;
using Anchor.Core.Services;

namespace Anchor.Infrastructure.DeepSeek;

public sealed class DeepSeekClient : ITaskIntelligence
{
    public static readonly Uri DefaultEndpoint =
        new("https://api.deepseek.com/chat/completions", UriKind.Absolute);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly Uri _endpoint;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly ConcurrentDictionary<string, RelevanceJudgment> _relevanceCache =
        new(StringComparer.Ordinal);

    public DeepSeekClient(
        HttpClient httpClient,
        string apiKey,
        string model = "deepseek-flash",
        Uri? endpoint = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? retryDelay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(model) ? "deepseek-flash" : model.Trim();
        _endpoint = endpoint ?? DefaultEndpoint;
        if (_endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("DeepSeek endpoint must use HTTPS.", nameof(endpoint));
        }

        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(8);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public TaskIntelligenceAvailability Availability => string.IsNullOrWhiteSpace(_apiKey)
        ? new(false, false, "DeepSeek is not configured")
        : new(true, true, "DeepSeek configured");

    public async Task<TaskPlanningResult> PlanTaskAsync(
        string goal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var normalizedGoal = Bound(goal, 240);
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return FallbackPlan(normalizedGoal, "deepseek_not_configured");
        }

        var prompt = $$"""
            Return JSON with this exact shape:
            {"goal":"concise goal","steps":[{"id":"step-1","title":"concrete action","completionCriterion":"observable completion"}]}
            Create 2 to 8 ordered, concrete steps. Preserve quantities in the user's goal.
            User goal: {{normalizedGoal}}
            """;
        var response = await CompleteAsync(prompt, cancellationToken);
        if (response.ErrorCode is not null)
        {
            return FallbackPlan(normalizedGoal, response.ErrorCode);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PlanDto>(response.Content!, JsonOptions)
                ?? throw new JsonException("Missing plan object.");
            var steps = dto.Steps?
                .Select(step => new TaskStep(step.Id ?? string.Empty, step.Title ?? string.Empty,
                    step.CompletionCriterion ?? string.Empty))
                .ToArray() ?? [];
            var plan = new TaskPlan(dto.Goal ?? normalizedGoal, steps);
            var validated = new TaskPlanManager().Start(plan).Plan;
            if (validated.Steps.Count < 2)
            {
                throw new JsonException("DeepSeek plans require at least two steps.");
            }

            return new TaskPlanningResult(validated, false, "DeepSeek", null);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return FallbackPlan(normalizedGoal, "invalid_deepseek_json");
        }
    }

    public async Task<RelevanceJudgment> JudgeRelevanceAsync(
        TaskContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = _clock();
        var key = CreateContextKey(context);
        if (_relevanceCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return LocalRelevance(context, "DeepSeek is not configured.", now);
        }

        var prompt = $$"""
            Return JSON with this exact shape:
            {"score":0.0,"classification":"relevant|ambiguous|likely_detour","reason":"one short sentence"}
            Judge whether the current context supports the active step. Do not infer private content not provided.
            Goal: {{Bound(context.Goal, 240)}}
            Active step: {{Bound(context.CurrentSubtask, 240)}}
            Process: {{Bound(context.ProcessName, 120)}}
            Window: {{Bound(context.WindowTitle, 240)}}
            Domain: {{Bound(context.Domain, 240)}}
            User-approved targets: {{string.Join(", ", context.UserRelevantTargets.Select(item => Bound(item, 120)))}}
            """;
        var response = await CompleteAsync(prompt, cancellationToken);
        RelevanceJudgment judgment;
        if (response.ErrorCode is not null)
        {
            judgment = LocalRelevance(context, response.ErrorCode, now);
        }
        else
        {
            try
            {
                var dto = JsonSerializer.Deserialize<RelevanceDto>(response.Content!, JsonOptions)
                    ?? throw new JsonException("Missing relevance object.");
                judgment = new RelevanceJudgment(
                    Math.Clamp(dto.Score, 0, 1),
                    ParseClassification(dto.Classification),
                    Bound(dto.Reason, 300),
                    false,
                    now.AddMinutes(5));
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                judgment = LocalRelevance(context, "invalid_deepseek_json", now);
            }
        }

        _relevanceCache[key] = judgment;
        return judgment;
    }

    public async Task<TaskStep> BreakDownStepAsync(
        TaskContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return LocalBreakdown(context.CurrentSubtask);
        }

        var prompt = $$"""
            Return JSON with this exact shape:
            {"id":"smaller-step","title":"one immediately actionable step","completionCriterion":"observable completion"}
            Break this active step into the smallest useful next action.
            Goal: {{Bound(context.Goal, 240)}}
            Active step: {{Bound(context.CurrentSubtask, 240)}}
            """;
        var response = await CompleteAsync(prompt, cancellationToken);
        if (response.ErrorCode is not null)
        {
            return LocalBreakdown(context.CurrentSubtask);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<StepDto>(response.Content!, JsonOptions)
                ?? throw new JsonException("Missing step object.");
            var plan = new TaskPlan(context.Goal, [new TaskStep(
                dto.Id ?? string.Empty,
                dto.Title ?? string.Empty,
                dto.CompletionCriterion ?? string.Empty)]);
            return new TaskPlanManager().Start(plan).CurrentStep!;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return LocalBreakdown(context.CurrentSubtask);
        }
    }

    public async Task<ProgressJudgment> JudgeProgressAsync(
        ProgressEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var local = LocalProgressJudge.Judge(evidence);
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return local;
        }

        var steps = string.Join('\n', evidence.Steps.Select((step, index) =>
            $"{index + 1}. [{(index < evidence.CompletedCount ? "done" : "todo")}] {Bound(step.Title, 160)} — done when: {Bound(step.CompletionCriterion, 160)}"));
        var prompt = $$"""
            Return JSON with this exact shape:
            {"stepCompleted":false,"confidence":0.0,"evidence":"one short sentence quoting what on screen proves it"}
            You watch a student's screen to detect progress on a study plan. Decide ONLY whether the ACTIVE step's
            completion criterion is visibly satisfied by the screen text below (an accepted verdict, a submitted answer,
            a finished document, a reached page). Being on the right page is NOT completion. If unsure, stepCompleted=false
            with a low confidence. Never invent text that is not on screen.
            Goal: {{Bound(evidence.Goal, 240)}}
            Plan:
            {{steps}}
            ACTIVE step: {{Bound(evidence.CurrentStep.Title, 200)}} — done when: {{Bound(evidence.CurrentStep.CompletionCriterion, 200)}}
            Activity: {{ActivityClassifier.Describe(evidence.Activity)}}
            Process: {{Bound(evidence.ProcessName, 120)}}
            Window: {{Bound(evidence.WindowTitle, 240)}}
            Already used as evidence for earlier steps (do not count again): {{string.Join(" | ", evidence.RecentlyCompletedEvidence.Select(item => Bound(item, 160)))}}
            Screen text (OCR, top to bottom):
            {{Bound(evidence.ScreenText, 3_000)}}
            """;
        var response = await CompleteAsync(prompt, cancellationToken);
        if (response.ErrorCode is not null)
        {
            return local with { Evidence = $"{local.Evidence} ({response.ErrorCode})" };
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ProgressDto>(response.Content!, JsonOptions)
                ?? throw new JsonException("Missing progress object.");
            return new ProgressJudgment(
                dto.StepCompleted,
                Math.Clamp(dto.Confidence, 0, 1),
                Bound(dto.Evidence, 300),
                false,
                "DeepSeek");
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return local;
        }
    }

    public async Task<ContextReminder> ComposeReminderAsync(
        ContextCapsule capsule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capsule);
        var local = LocalContextReminder.Compose(capsule);
        if (string.IsNullOrWhiteSpace(_apiKey) || capsule.IsEstimatedContext)
        {
            return local;
        }

        var focusSource = capsule.FocusSource switch
        {
            FocusSource.Gaze => "the line the camera saw their eyes on",
            FocusSource.Caret => "the line at their text cursor",
            FocusSource.Pointer => "the line under their mouse pointer",
            FocusSource.Viewport => "the middle of the visible page (no gaze/caret available)",
            _ => "unknown"
        };
        var prompt = $$"""
            Return JSON with this exact shape:
            {"headline":"<= 60 chars, 'You were ...'","whereYouWere":"1-2 sentences, concrete","resumeWith":"one imperative sentence"}
            A student with ADHD got distracted. The facts below describe the task-relevant work they were doing
            BEFORE the distraction (not the distraction itself); the reminder must bring them back to exactly that spot.
            Adapt to the activity: for reading quote the last sentence they were on; for coding name the file and the
            code they were editing; for writing quote their last sentence; for problem solving name the problem and
            sub-step; for browsing say which page and what they were looking for; for a video or lecture name it and
            the timestamp to resume from. Use only the facts below, quote screen text verbatim when you quote, never
            invent content, never tell them to close the application or page they were working in. Warm, brief, no emojis.
            Task: {{Bound(capsule.TaskTitle, 240)}}
            Active step: {{Bound(capsule.CurrentSubtask, 240)}}
            Activity: {{ActivityClassifier.Describe(capsule.Activity)}}
            Application: {{Bound(capsule.Application, 120)}}
            Window/document: {{Bound(capsule.DocumentIdentity, 240)}}
            Location: {{Bound(capsule.Location, 240)}}
            Keys typed in last window: {{capsule.KeyCount}}; scroll reversals: {{capsule.ScrollReversalCount}}
            Focus line ({{focusSource}}): {{Bound(capsule.FocusText, 400)}}
            Screen excerpt around the focus line (» marks it):
            {{Bound(capsule.ScreenExcerpt, 1_600)}}
            Planned next action: {{Bound(capsule.NextAction, 240)}}
            """;
        var response = await CompleteAsync(prompt, cancellationToken);
        if (response.ErrorCode is not null)
        {
            return local;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ReminderDto>(response.Content!, JsonOptions)
                ?? throw new JsonException("Missing reminder object.");
            if (string.IsNullOrWhiteSpace(dto.WhereYouWere))
            {
                throw new JsonException("Empty reminder.");
            }
            return new ContextReminder(
                string.IsNullOrWhiteSpace(dto.Headline) ? local.Headline : Bound(dto.Headline, 90),
                Bound(dto.WhereYouWere, 400),
                string.IsNullOrWhiteSpace(dto.ResumeWith) ? local.ResumeWith : Bound(dto.ResumeWith, 240),
                "DeepSeek",
                false);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return local;
        }
    }

    private async Task<CompletionResult> CompleteAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = JsonContent.Create(new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = "Return valid JSON only and follow the requested shape." },
                    new { role = "user", content = prompt }
                },
                response_format = new { type = "json_object" },
                thinking = new { type = "disabled" },
                stream = false,
                temperature = 0.1
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(null, "deepseek_timeout");
            }
            catch (HttpRequestException)
            {
                return new(null, "deepseek_unavailable");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        var document = await JsonDocument.ParseAsync(
                            await response.Content.ReadAsStreamAsync(cancellationToken),
                            cancellationToken: cancellationToken);
                        using (document)
                        {
                            var content = document.RootElement
                                .GetProperty("choices")[0]
                                .GetProperty("message")
                                .GetProperty("content")
                                .GetString();
                            return string.IsNullOrWhiteSpace(content)
                                ? new(null, "invalid_deepseek_json")
                                : new(content, null);
                        }
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
                    {
                        return new(null, "invalid_deepseek_json");
                    }
                }

                var transient = response.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500;
                if (!transient)
                {
                    return new(null, $"deepseek_http_{(int)response.StatusCode}");
                }
            }

            if (attempt == 0 && _retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        return new(null, "deepseek_unavailable");
    }

    private static TaskPlanningResult FallbackPlan(string goal, string errorCode) =>
        new(
            new TaskPlanManager().Start(LocalTaskPlanner.Plan(goal)).Plan,
            true,
            "Local fallback",
            errorCode);

    private static TaskStep LocalBreakdown(string currentStep) =>
        new("smaller-step", $"Open the task and begin: {Bound(currentStep, 180)}", "First concrete action completed");

    private static RelevanceJudgment LocalRelevance(
        TaskContext context,
        string reason,
        DateTimeOffset now)
    {
        var localScore = TaskRelevanceScorer.Score(
            $"{context.Goal} {context.CurrentSubtask}",
            context.ProcessName,
            $"{context.WindowTitle} {context.Domain}");
        return new RelevanceJudgment(
            localScore,
            localScore >= 0.65
                ? RelevanceClass.Relevant
                : localScore < 0.35
                    ? RelevanceClass.LikelyDetour
                    : RelevanceClass.Ambiguous,
            $"Local fallback: {reason}",
            true,
            now.AddSeconds(30));
    }

    private static RelevanceClass ParseClassification(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "relevant" => RelevanceClass.Relevant,
            "ambiguous" => RelevanceClass.Ambiguous,
            "likely_detour" => RelevanceClass.LikelyDetour,
            _ => throw new JsonException("Unknown relevance classification.")
        };

    private static string CreateContextKey(TaskContext context)
    {
        static string Normalize(string? value) =>
            string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return string.Join('|',
            Normalize(context.Goal),
            Normalize(context.CurrentSubtask),
            Normalize(context.ProcessName),
            Normalize(context.WindowTitle),
            Normalize(context.Domain),
            string.Join(',', context.UserRelevantTargets.Select(Normalize).Order(StringComparer.Ordinal)));
    }

    private static string Bound(string? value, int maximumScalars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(value.Trim().EnumerateRunes()
            .Take(maximumScalars)
            .Select(static rune => rune.ToString()));
    }

    private sealed record CompletionResult(string? Content, string? ErrorCode);
    private sealed record PlanDto(string? Goal, IReadOnlyList<StepDto>? Steps);
    private sealed record StepDto(string? Id, string? Title, string? CompletionCriterion);
    private sealed record RelevanceDto(double Score, string? Classification, string? Reason);
    private sealed record ProgressDto(bool StepCompleted, double Confidence, string? Evidence);
    private sealed record ReminderDto(string? Headline, string? WhereYouWere, string? ResumeWith);
}
