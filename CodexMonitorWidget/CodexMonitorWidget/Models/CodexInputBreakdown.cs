namespace CodexMonitorWidget.Models;

public sealed class CodexInputBreakdownItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public int Tokens { get; init; }
    public bool IsExact { get; init; }

    public string FormattedTokens => CodexUsageSnapshot.Format(Tokens);
}

public sealed class CodexInputBreakdown
{
    public int ExactCachedTokens { get; init; }
    public int ExactNewInputTokens { get; init; }
    public IReadOnlyList<CodexInputBreakdownItem> Components { get; init; } = [];

    public bool HasComponents => Components.Count > 0;

    public static readonly string[] ComponentOrder =
    [
        "systemPrompt",
        "agentsMd",
        "environment",
        "ideContext",
        "previousConversation",
        "toolResults",
        "userInput"
    ];
}
