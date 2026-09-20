using Anchor.Core.Models;

namespace Anchor.Core.Services;

/// <summary>
/// Infers what the user is doing from the same signals the attention model already sees:
/// which process is in front, its title, how much they typed and how they scrolled.
/// </summary>
public static class ActivityClassifier
{
    private static readonly string[] EditorProcesses =
        ["code", "devenv", "idea64", "pycharm64", "clion64", "rider64", "sublime_text", "notepad++", "vim", "nvim", "cursor", "windowsterminal", "cmd", "powershell", "pwsh"];
    private static readonly string[] WriterProcesses =
        ["winword", "notepad", "wordpad", "obsidian", "notion", "typora", "onenote"];
    private static readonly string[] ReaderProcesses =
        ["acrord32", "acrobat", "sumatrapdf", "foxitreader", "foxitpdfreader", "msedge_pdf", "okular", "calibre", "kindle", "zotero"];
    private static readonly string[] BrowserProcesses = ["chrome", "msedge", "firefox", "brave", "opera", "arc"];
    private static readonly string[] VideoTitleHints = ["youtube", "netflix", "bilibili", "twitch", "vimeo", "video", "tiktok"];
    private static readonly string[] ProblemTitleHints =
        ["usaco", "leetcode", "codeforces", "atcoder", "problem", "exercise", "homework", "quiz", "khan academy", "brilliant", "aops", "worksheet"];
    private static readonly string[] EditorTitleHints =
        ["visual studio", "vs code", "jupyter", "colab", "replit", "codesandbox", "stackblitz", "overleaf"];
    private static readonly string[] DocumentTitleHints = ["google docs", "docs.google", "word", ".docx", "overleaf", "notion", "- notes"];
    private static readonly string[] ArticleTitleHints =
        ["wikipedia", "arxiv", "britannica", "medium.com", "substack", "documentation", "docs.", "tutorial", "guide", "chapter", "lecture", "textbook", "article", "paper", "encyclopedia", "readthedocs", "mdn", "stack overflow", "pubmed", "jstor"];
    private static readonly string[] SearchTitleHints = ["google search", "- search", "bing", "duckduckgo", "search results", "new tab", "google.com"];

    public static ActivityKind Infer(
        string? processName,
        string? windowTitle,
        int keyCount,
        int scrollReversalCount,
        double mouseDistance)
    {
        var process = (processName ?? string.Empty).Trim().ToLowerInvariant();
        var title = (windowTitle ?? string.Empty).ToLowerInvariant();
        var typing = keyCount >= 6;
        var scrolling = scrollReversalCount > 0 || mouseDistance > 120;

        if (VideoTitleHints.Any(title.Contains))
        {
            return ActivityKind.Watching;
        }

        if (ProblemTitleHints.Any(title.Contains))
        {
            return ActivityKind.ProblemSolving;
        }

        if (EditorProcesses.Any(process.Contains) || EditorTitleHints.Any(title.Contains))
        {
            return ActivityKind.Coding;
        }

        if (WriterProcesses.Any(process.Contains) || DocumentTitleHints.Any(title.Contains))
        {
            return typing || !scrolling ? ActivityKind.Writing : ActivityKind.Reading;
        }

        if (ReaderProcesses.Any(process.Contains) || title.Contains(".pdf"))
        {
            return ActivityKind.Reading;
        }

        if (BrowserProcesses.Any(process.Contains))
        {
            if (typing)
            {
                return ActivityKind.Writing;
            }

            if (SearchTitleHints.Any(title.Contains))
            {
                return ActivityKind.Browsing;
            }

            return scrolling || ArticleTitleHints.Any(title.Contains) ? ActivityKind.Reading : ActivityKind.Browsing;
        }

        if (typing)
        {
            return ActivityKind.Writing;
        }

        return scrolling ? ActivityKind.Reading : ActivityKind.Unknown;
    }

    public static string Describe(ActivityKind kind) => kind switch
    {
        ActivityKind.Reading => "reading",
        ActivityKind.Writing => "writing",
        ActivityKind.Coding => "coding",
        ActivityKind.ProblemSolving => "solving a problem",
        ActivityKind.Browsing => "browsing",
        ActivityKind.Watching => "watching",
        _ => "working"
    };
}
