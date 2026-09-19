using System.Net;
using System.Text;
using Anchor.Core.Models;
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
        Assert.Single(result.Plan.Steps);
    }

    [Fact]
    public async Task Malformed_json_never_creates_fake_llm_plan()
    {
        var handler = new CapturingHandler(_ => JsonResponse("{not-json"));
        var client = CreateClient(handler);

        var result = await client.PlanTaskAsync("Read chapter 4", CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("invalid_deepseek_json", result.ErrorCode);
        Assert.Equal("Read chapter 4", result.Plan.Steps[0].Title);
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
