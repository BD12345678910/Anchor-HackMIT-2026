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

    /// <summary>The only DeepSeek model that accepts image parts; used to look at the pictures themselves.</summary>
    public const string VisionModel = "deepseek-v4-flash-vision-exp";

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _visionModel;
    private readonly Uri _endpoint;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly ConcurrentDictionary<string, RelevanceJudgment> _relevanceCache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (PictureRelevance Verdict, DateTimeOffset ExpiresAt)> _pictureCache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (TextRelevance Verdict, DateTimeOffset ExpiresAt)> _textCache =
        new(StringComparer.Ordinal);

    public DeepSeekClient(
        HttpClient httpClient,
        string apiKey,
        string model = "deepseek-flash",
        Uri? endpoint = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? retryDelay = null,
        string? visionModel = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(model) ? "deepseek-flash" : model.Trim();
        _visionModel = string.IsNullOrWhiteSpace(visionModel) ? VisionModel : visionModel.Trim();
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
            Judge whether what the user is looking at supports the active step or the overall goal. Decide from the
            on-screen text first, the window title second. Anything on the same subject as the goal (the article being
            studied, any of its sections or sub-articles, linked references, a search for it, the same problem site,
            documentation for the same tool) is "relevant" even if it does not match the active step word for word.
            Only content clearly unrelated to the goal (entertainment, social feeds, shopping, news, unrelated topics)
            is "likely_detour". Use "ambiguous" when the evidence cannot tell. Do not infer private content not provided.
            Goal: {{Bound(context.Goal, 240)}}
            Active step: {{Bound(context.CurrentSubtask, 240)}}
            Process: {{Bound(context.ProcessName, 120)}}
            Window: {{Bound(context.WindowTitle, 240)}}
            Domain: {{Bound(context.Domain, 240)}}
            User-approved targets: {{string.Join(", ", context.UserRelevantTargets.Select(item => Bound(item, 120)))}}
            On-screen text (OCR, may be noisy): {{(string.IsNullOrWhiteSpace(context.ScreenExcerpt) ? "(not yet read)" : Bound(context.ScreenExcerpt, 900))}}
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

    public async Task<PictureGrading> GradePicturesAsync(
        PictureGradingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _clock();
        var pageKey = CreatePageKey(request);
        var verdicts = new Dictionary<string, PictureRelevance>(StringComparer.Ordinal);
        var missing = new List<PictureDescriptor>();
        foreach (var picture in request.Pictures.DistinctBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (_pictureCache.TryGetValue(pageKey + '|' + picture.Key, out var cached) && cached.ExpiresAt > now)
            {
                verdicts[picture.Key] = cached.Verdict;
            }
            else
            {
                missing.Add(picture);
            }
        }
        if (missing.Count == 0)
        {
            return new PictureGrading(verdicts, "DeepSeek", false);
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return Merge(verdicts, PictureTreatmentPlanner.LocalGrade(request with { Pictures = missing }), "Local rules · DeepSeek is not configured");
        }

        var listing = string.Join('\n', missing.Select((picture, index) =>
            $"{index + 1}. size {picture.Width}x{picture.Height}px{(picture.AdShaped ? " (banner/rail shaped)" : string.Empty)}; " +
            $"text next to it: {(picture.NearbyText.Length == 0 ? "(no caption)" : '"' + Bound(picture.NearbyText, PictureTreatmentPlanner.MaxNearbyChars) + '"')}"));
        var withPixels = missing.Where(static p => !string.IsNullOrEmpty(p.ThumbnailDataUrl)).ToList();
        var prompt = $$"""
            Return JSON with this exact shape:
            {"pictures":[{"index":1,"verdict":"illustrates|unrelated|bait"}]}
            The user studies with a tool that lowers the resolution of distracting pictures. For each picture on the
            page decide: "illustrates" when it depicts or supports the subject the user is working on (judge by what
            the picture shows when an image is attached, then by the caption/adjacent text and the page text; a
            picture with no caption on a page about the subject is "illustrates"), "unrelated" when it shows or is
            about something else, "bait" when it is an advert, promo, recommendation, thumbnail feed or other attention
            bait. When the picture shows the same subject as the goal (for example any cat picture when the goal is
            about cats), it is always "illustrates". Give one entry per index.
            Goal: {{Bound(request.Goal, 240)}}
            Active step: {{Bound(request.CurrentSubtask, 240)}}
            Process: {{Bound(request.ProcessName, 120)}}
            Window: {{Bound(request.WindowTitle, 240)}}
            Page text (OCR, may be noisy): {{(string.IsNullOrWhiteSpace(request.ScreenExcerpt) ? "(not read)" : Bound(request.ScreenExcerpt, 900))}}
            Pictures{{(withPixels.Count > 0 ? " (each attached image is labelled with its index)" : string.Empty)}}:
            {{listing}}
            """;

        CompletionResult response;
        var source = "DeepSeek";
        if (withPixels.Count > 0)
        {
            // Vision grading: the pictures themselves, downsampled, with their index labels.
            var parts = new List<object> { new { type = "text", text = prompt } };
            foreach (var picture in withPixels)
            {
                parts.Add(new { type = "text", text = $"Picture {missing.IndexOf(picture) + 1}:" });
                parts.Add(new { type = "image_url", image_url = new { url = picture.ThumbnailDataUrl, detail = "low" } });
            }
            response = await CompleteAsync(_visionModel, parts, jsonMode: false, cancellationToken);
            source = "DeepSeek vision";
            if (response.ErrorCode is not null)
            {
                // Vision model unavailable: fall back to caption/page-text grading before local rules.
                response = await CompleteAsync(prompt, cancellationToken);
                source = $"DeepSeek text (vision {response.ErrorCode ?? "unavailable"})";
            }
        }
        else
        {
            response = await CompleteAsync(prompt, cancellationToken);
        }
        if (response.ErrorCode is not null)
        {
            return Merge(verdicts, PictureTreatmentPlanner.LocalGrade(request with { Pictures = missing }), $"Local rules · DeepSeek {response.ErrorCode}");
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PicturesDto>(ExtractJsonObject(response.Content!), JsonOptions)
                ?? throw new JsonException("Missing pictures object.");
            var graded = new Dictionary<string, PictureRelevance>(StringComparer.Ordinal);
            foreach (var entry in dto.Pictures ?? [])
            {
                if (entry.Index < 1 || entry.Index > missing.Count)
                {
                    continue;
                }
                graded[missing[entry.Index - 1].Key] = ParsePictureVerdict(entry.Verdict);
            }
            if (graded.Count == 0)
            {
                throw new JsonException("No picture verdicts.");
            }
            foreach (var (key, verdict) in graded)
            {
                _pictureCache[pageKey + '|' + key] = (verdict, now.AddMinutes(10));
                verdicts[key] = verdict;
            }
            foreach (var picture in missing.Where(p => !graded.ContainsKey(p.Key)))
            {
                verdicts[picture.Key] = PictureRelevance.Illustrates;
            }
            return new PictureGrading(verdicts, source, false);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return Merge(verdicts, PictureTreatmentPlanner.LocalGrade(request with { Pictures = missing }), "Local rules · DeepSeek invalid_deepseek_json");
        }
    }

    public async Task<TextGrading> GradeTextBlocksAsync(
        TextGradingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _clock();
        var pageKey = CreatePageKey(request.Goal, request.CurrentSubtask, request.ProcessName, request.WindowTitle);
        var verdicts = new Dictionary<string, TextRelevance>(StringComparer.Ordinal);
        var missing = new List<TextBlockDescriptor>();
        foreach (var block in request.Blocks.DistinctBy(static b => b.Key, StringComparer.Ordinal))
        {
            if (_textCache.TryGetValue(pageKey + '|' + block.Key, out var cached) && cached.ExpiresAt > now)
            {
                verdicts[block.Key] = cached.Verdict;
            }
            else
            {
                missing.Add(block);
            }
        }
        if (missing.Count == 0)
        {
            return new TextGrading(verdicts, "DeepSeek", false);
        }
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return MergeText(verdicts, TextBlockPlanner.LocalGrade(request with { Blocks = missing }), "Local rules · DeepSeek is not configured");
        }

        var listing = string.Join('\n', missing.Select((block, index) => $"{index + 1}. \"{Bound(block.Text, 320)}\""));
        var prompt = $$"""
            Return JSON with this exact shape:
            {"blocks":[{"index":1,"verdict":"on_task|off_task"}]}
            The user studies with a tool that dims on-screen passages that are not part of what they are working on.
            For each passage (OCR of one paragraph, list, card or navigation block) decide: "on_task" when it is part
            of the material for the goal or active step (the article body, its headings, code, problem statements,
            figures' captions, the tool's own UI needed to work), "off_task" when it is navigation to other topics,
            "related/recommended/trending" lists, comments, adverts, promos, cookie/login banners, or content about
            another subject. Passages on the same subject as the goal are always "on_task". Give one entry per index.
            Goal: {{Bound(request.Goal, 240)}}
            Active step: {{Bound(request.CurrentSubtask, 240)}}
            Process: {{Bound(request.ProcessName, 120)}}
            Window: {{Bound(request.WindowTitle, 240)}}
            Passages:
            {{listing}}
            """;
        var response = await CompleteAsync(prompt, cancellationToken);
        if (response.ErrorCode is not null)
        {
            return MergeText(verdicts, TextBlockPlanner.LocalGrade(request with { Blocks = missing }), $"Local rules · DeepSeek {response.ErrorCode}");
        }
        try
        {
            var dto = JsonSerializer.Deserialize<BlocksDto>(response.Content!, JsonOptions)
                ?? throw new JsonException("Missing blocks object.");
            var graded = new Dictionary<string, TextRelevance>(StringComparer.Ordinal);
            foreach (var entry in dto.Blocks ?? [])
            {
                if (entry.Index < 1 || entry.Index > missing.Count)
                {
                    continue;
                }
                graded[missing[entry.Index - 1].Key] = entry.Verdict?.Trim().ToLowerInvariant() switch
                {
                    "on_task" => TextRelevance.OnTask,
                    "off_task" => TextRelevance.OffTask,
                    _ => throw new JsonException("Unknown passage verdict.")
                };
            }
            if (graded.Count == 0)
            {
                throw new JsonException("No passage verdicts.");
            }
            foreach (var (key, verdict) in graded)
            {
                _textCache[pageKey + '|' + key] = (verdict, now.AddMinutes(10));
                verdicts[key] = verdict;
            }
            foreach (var block in missing.Where(b => !graded.ContainsKey(b.Key)))
            {
                verdicts[block.Key] = TextRelevance.OnTask;
            }
            return new TextGrading(verdicts, "DeepSeek", false);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return MergeText(verdicts, TextBlockPlanner.LocalGrade(request with { Blocks = missing }), "Local rules · DeepSeek invalid_deepseek_json");
        }
    }

    private static TextGrading MergeText(Dictionary<string, TextRelevance> cached, TextGrading local, string source)
    {
        foreach (var (key, verdict) in local.Verdicts)
        {
            cached[key] = verdict;
        }
        return new TextGrading(cached, source, true);
    }

    /// <summary>Vision replies are not JSON-mode constrained; take the outermost object if the model wrapped it in prose or a code fence.</summary>
    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    private static PictureGrading Merge(Dictionary<string, PictureRelevance> cached, PictureGrading local, string source)
    {
        foreach (var (key, verdict) in local.Verdicts)
        {
            cached[key] = verdict;
        }
        return new PictureGrading(cached, source, true);
    }

    private static PictureRelevance ParsePictureVerdict(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "illustrates" => PictureRelevance.Illustrates,
            "unrelated" => PictureRelevance.Unrelated,
            "bait" => PictureRelevance.Bait,
            _ => throw new JsonException("Unknown picture verdict.")
        };

    private static string CreatePageKey(PictureGradingRequest request) =>
        CreatePageKey(request.Goal, request.CurrentSubtask, request.ProcessName, request.WindowTitle);

    private static string CreatePageKey(string goal, string subtask, string process, string? title)
    {
        static string Normalize(string? value) =>
            string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.Join('|', Normalize(goal), Normalize(subtask), Normalize(process), Normalize(title));
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
            completion criterion is visibly satisfied (an accepted verdict, a submitted answer, a finished document,
            a reached page, a section read through). For "read/study/review X" steps the screen cannot show
            comprehension, so use the screen trail: the step is done (confidence >= 0.85) once the trail shows the
            student moved through the section from its start to its end (the section heading appeared earlier and the
            current screen shows the following heading or the section's final paragraphs), or when they have dwelt on
            the section for a duration plausible for reading it. A first glance at the section's opening is NOT
            completion. If unsure, stepCompleted=false with a low confidence. Never invent text that is not on screen.
            Goal: {{Bound(evidence.Goal, 240)}}
            Plan:
            {{steps}}
            ACTIVE step: {{Bound(evidence.CurrentStep.Title, 200)}} — done when: {{Bound(evidence.CurrentStep.CompletionCriterion, 200)}}
            Time spent on the active step in task-relevant windows: {{Math.Round(evidence.TimeOnStep.TotalSeconds)}} s
            Activity: {{ActivityClassifier.Describe(evidence.Activity)}}
            Process: {{Bound(evidence.ProcessName, 120)}}
            Window: {{Bound(evidence.WindowTitle, 240)}}
            Already used as evidence for earlier steps (do not count again): {{string.Join(" | ", evidence.RecentlyCompletedEvidence.Select(item => Bound(item, 160)))}}
            Screen trail since the step became active (oldest first, one line per screen seen):
            {{Bound(string.Join('\n', evidence.Trail.Select(item => "- " + Bound(item, 160))), 1_500)}}
            Screen text now (OCR, top to bottom):
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
        var scrollNote = capsule.Reason switch
        {
            DistractionReason.ScrollBurst => """
              They lost the thread by scrolling through this document fast and erratically, so the document is still
              the right one: name the page/position they were on before the scrolling and send them back to it.

              """,
            DistractionReason.GibberishTyping => """
              The characters they just typed are not words (keyboard mashing or a held key), so they are still in the
              right document: tell them to clear the stray characters and name the line/thought they were writing.

              """,
            DistractionReason.PointerFidget => """
              They drifted by fidgeting with the mouse — the cursor circling or clicking with nothing being read or
              written — so the window is still the right one: name the exact spot they were working at and send the
              pointer back to it.

              """,
            _ => string.Empty
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
            {{scrollNote}}Task: {{Bound(capsule.TaskTitle, 240)}}
            Active step: {{Bound(capsule.CurrentSubtask, 240)}}
            Activity: {{ActivityClassifier.Describe(capsule.Activity)}}
            Application: {{Bound(capsule.Application, 120)}}
            Window/document: {{Bound(capsule.DocumentIdentity, 240)}}
            Location: {{Bound(capsule.Location, 240)}}
            Position in document before the distraction: {{Bound(capsule.DocumentPosition, 60)}}
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

    private Task<CompletionResult> CompleteAsync(
        string prompt,
        CancellationToken cancellationToken) =>
        CompleteAsync(_model, prompt, jsonMode: true, cancellationToken);

    /// <param name="userContent">Either a prompt string or an array of OpenAI-style content parts (text + image_url).</param>
    private async Task<CompletionResult> CompleteAsync(
        string model,
        object userContent,
        bool jsonMode,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = "Return valid JSON only and follow the requested shape." },
                    new { role = "user", content = userContent }
                },
                ["thinking"] = new { type = "disabled" },
                ["stream"] = false,
                ["temperature"] = 0.1
            };
            if (jsonMode)
            {
                body["response_format"] = new { type = "json_object" };
            }
            request.Content = JsonContent.Create(body);

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
            $"{context.WindowTitle} {context.Domain} {context.ScreenExcerpt}");
        // Title/vocabulary rules cannot understand a page; without DeepSeek they may confirm
        // relevance but never declare a detour on their own.
        return new RelevanceJudgment(
            Math.Max(localScore, 0.5),
            localScore >= 0.65 ? RelevanceClass.Relevant : RelevanceClass.Ambiguous,
            $"Local rules only · DeepSeek {reason}",
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
            string.IsNullOrWhiteSpace(context.ScreenExcerpt) ? "no-text" : "text",
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
    private sealed record PicturesDto(IReadOnlyList<PictureVerdictDto>? Pictures);
    private sealed record PictureVerdictDto(int Index, string? Verdict);
    private sealed record BlocksDto(IReadOnlyList<BlockVerdictDto>? Blocks);
    private sealed record BlockVerdictDto(int Index, string? Verdict);
    private sealed record ProgressDto(bool StepCompleted, double Confidence, string? Evidence);
    private sealed record ReminderDto(string? Headline, string? WhereYouWere, string? ResumeWith);
}
