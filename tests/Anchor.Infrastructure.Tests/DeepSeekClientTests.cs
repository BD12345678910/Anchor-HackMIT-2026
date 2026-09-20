using System.Net;
using System.Text;
using Anchor.Core.Models;
using Anchor.Core.Services;
using Anchor.Infrastructure.DeepSeek;

namespace Anchor.Infrastructure.Tests;

public sealed class DeepSeekClientTests
{
    [Fact]
    public async Task PlanTaskAsync_sends_minimized_text_and_parses_valid_json()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """{"goal":"Solve three problems","steps":[{"id":"q1","title":"Solve problem 1","completionCriterion":"Accepted submission"},{"id":"q2","title":"Solve problem 2","completionCriterion":"Accepted submission"},{"id":"q3","title":"Solve problem 3","completionCriterion":"Accepted submission"}]}"""));
        var client = CreateClient(handler);

        var result = await client.PlanTaskAsync("Do 3 USACO questions", CancellationToken.None);

        Assert.False(result.IsFallback);
        Assert.Equal("DeepSeek", result.Source);
        Assert.Equal(3, result.Plan.Steps.Count);
        Assert.Equal("Solve problem 1", result.Plan.Steps[0].Title);
        Assert.Contains("Do 3 USACO questions", handler.LastBody);
        Assert.DoesNotContain("screenshot", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gaze", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keystroke", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Bearer test-key", handler.LastAuthorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Transient_http_failures_return_explicit_fallback_after_one_retry(HttpStatusCode status)
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(status));
        var client = CreateClient(handler);

        var result = await client.PlanTaskAsync("Read chapter 4", CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("Local fallback", result.Source);
        Assert.Equal("deepseek_unavailable", result.ErrorCode);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(
            LocalTaskPlanner.Plan("Read chapter 4").Steps.Select(step => step.Title),
            result.Plan.Steps.Select(step => step.Title));
    }

    [Fact]
    public async Task Malformed_json_never_creates_fake_llm_plan()
    {
        var handler = new CapturingHandler(_ => JsonResponse("{not-json"));
        var client = CreateClient(handler);

        var result = await client.PlanTaskAsync("Read chapter 4", CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("invalid_deepseek_json", result.ErrorCode);
        Assert.Equal("Local fallback", result.Source);
        Assert.All(result.Plan.Steps, step => Assert.DoesNotContain("{", step.Title));
        Assert.Contains("chapter 4", result.Plan.Steps[0].Title);
    }

    [Fact]
    public async Task Timeout_returns_explicit_fallback_without_retrying_forever()
    {
        var handler = new CapturingHandler(_ => throw new TaskCanceledException("timeout"));
        var client = CreateClient(handler);

        var result = await client.PlanTaskAsync("Write essay", CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("deepseek_timeout", result.ErrorCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Relevance_is_cached_for_normalized_context_until_expiry()
    {
        var now = DateTimeOffset.Parse("2026-09-20T02:00:00Z");
        var handler = new CapturingHandler(_ => JsonResponse(
            "{\"score\":0.91,\"classification\":\"relevant\",\"reason\":\"The site contains the active programming problem.\"}"));
        var client = CreateClient(handler, () => now);
        var firstContext = new TaskContext(
            "Solve USACO",
            "Solve problem 1",
            "msedge",
            "USACO Guide",
            "https://usaco.guide",
            []);
        var sameNormalizedContext = firstContext with
        {
            Goal = "  solve usaco  ",
            WindowTitle = " usaco guide "
        };

        var first = await client.JudgeRelevanceAsync(firstContext, CancellationToken.None);
        var cached = await client.JudgeRelevanceAsync(sameNormalizedContext, CancellationToken.None);
        now = now.AddMinutes(6);
        var refreshed = await client.JudgeRelevanceAsync(firstContext, CancellationToken.None);

        Assert.Equal(RelevanceClass.Relevant, first.Classification);
        Assert.Equal(first, cached);
        Assert.Equal(2, handler.RequestCount);
        Assert.True(refreshed.ExpiresAt > first.ExpiresAt);
    }

    [Fact]
    public async Task Missing_key_returns_local_fallback_without_network_request()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Network must not run."));
        var client = new DeepSeekClient(new HttpClient(handler), "", clock: () => DateTimeOffset.UnixEpoch);

        var result = await client.PlanTaskAsync("Read paper", CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("deepseek_not_configured", result.ErrorCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task JudgeProgressAsync_uses_deepseek_verdict_and_sends_only_bounded_screen_text()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """{"stepCompleted":true,"confidence":0.93,"evidence":"The page shows 'Accepted' for Bronze Problem 1."}"""));
        var client = CreateClient(handler);
        var step = new TaskStep("s1", "Solve 2021 December Bronze problem 1", "Accepted submission");
        var evidence = new ProgressEvidence(
            "do 3 usaco problems", [step], 0, step, "chrome", "USACO Results",
            new string('z', 20_000) + " Accepted", ActivityKind.ProblemSolving, []);

        var judgment = await client.JudgeProgressAsync(evidence, CancellationToken.None);

        Assert.True(judgment.StepCompleted);
        Assert.False(judgment.IsFallback);
        Assert.Equal("DeepSeek", judgment.Source);
        Assert.Equal(0.93, judgment.Confidence, 2);
        Assert.Contains("Accepted", judgment.Evidence);
        Assert.True(handler.LastBody.Length < 8_000);
        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", handler.LastBody);
        Assert.DoesNotContain("screenshot", handler.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JudgeProgressAsync_falls_back_to_local_rules_on_bad_json_or_missing_key()
    {
        var step = new TaskStep("s1", "Solve 2021 December Bronze problem 1", "Accepted submission");
        var evidence = new ProgressEvidence(
            "do 3 usaco problems", [step], 0, step, "chrome", "USACO",
            "Problem 1 Bronze December 2021 — Accepted!", ActivityKind.ProblemSolving, []);

        var badJson = await CreateClient(new CapturingHandler(_ => JsonResponse("nope"))).JudgeProgressAsync(evidence, CancellationToken.None);
        var noKeyHandler = new CapturingHandler(_ => throw new InvalidOperationException("must not be called"));
        var noKey = await new DeepSeekClient(new HttpClient(noKeyHandler), string.Empty).JudgeProgressAsync(evidence, CancellationToken.None);

        Assert.True(badJson.IsFallback);
        Assert.Equal("Local rules", badJson.Source);
        Assert.Equal(LocalProgressJudge.Judge(evidence), noKey);
        Assert.Equal(0, noKeyHandler.RequestCount);
        Assert.True(noKey.Confidence < ProgressJudgment.AutoCompleteThreshold);
    }

    [Fact]
    public async Task ComposeReminderAsync_returns_deepseek_wording_and_skips_estimated_context()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            """{"headline":"You were mid-loop in solve.cpp","whereYouWere":"Your cursor was on the for-loop over cows.","resumeWith":"Finish the loop body, then compile."}"""));
        var client = CreateClient(handler);
        var capsule = new ContextCapsule(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "do 3 usaco problems", "Code", "solve.cpp", "line 14",
            "Coding · cursor at: for (int i", "Compile and submit", null, null, DistractionReason.AppSwitch,
            "Solve problem 1", "matched", DateTimeOffset.UtcNow, false,
            ActivityKind.Coding, "for (int i = 0; i < n; i++) {", FocusSource.Caret, "int n; cin >> n;", 30, 0);

        var reminder = await client.ComposeReminderAsync(capsule, CancellationToken.None);
        var estimated = await client.ComposeReminderAsync(capsule with { IsEstimatedContext = true }, CancellationToken.None);

        Assert.False(reminder.IsFallback);
        Assert.Equal("DeepSeek", reminder.Source);
        Assert.Equal("You were mid-loop in solve.cpp", reminder.Headline);
        Assert.Contains("coding", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(estimated.IsFallback);
        Assert.Equal(1, handler.RequestCount);
    }

    private static DeepSeekClient CreateClient(
        CapturingHandler handler,
        Func<DateTimeOffset>? clock = null) =>
        new(
            new HttpClient(handler),
            "test-key",
            clock: clock ?? (() => DateTimeOffset.Parse("2026-09-20T02:00:00Z")),
            retryDelay: TimeSpan.Zero);

    private static HttpResponseMessage JsonResponse(string content)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        public string LastAuthorization { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            return responseFactory(request);
        }
    }
}
