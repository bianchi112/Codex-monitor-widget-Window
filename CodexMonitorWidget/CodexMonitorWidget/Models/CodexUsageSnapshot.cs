using System.Globalization;

namespace CodexMonitorWidget.Models;

public sealed class CodexUsageSnapshot
{
    public required DateTime Timestamp { get; init; }
    public int TotalTokens { get; init; }
    public int InputTokens { get; init; }
    public int CachedInputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int ReasoningOutputTokens { get; init; }
    public int LastTotalTokens { get; init; }
    public int LastInputTokens { get; init; }
    public int LastCachedInputTokens { get; init; }
    public int LastOutputTokens { get; init; }
    public int LastReasoningOutputTokens { get; init; }
    public double FiveHourUsedPercent { get; init; }
    public DateTime FiveHourResetAt { get; init; }
    public double WeeklyUsedPercent { get; init; }
    public DateTime WeeklyResetAt { get; init; }
    public string PlanType { get; init; } = "unknown";
    public required string SourceFile { get; init; }
    public string? LastUserMessageText { get; init; }
    public string? LastTurnModel { get; init; }
    public string? LastTurnReasoningEffort { get; init; }
    public int LastTurnInputTokens { get; init; }
    public int LastTurnCachedInputTokens { get; init; }
    public int LastTurnOutputTokens { get; init; }
    public int LastTurnReasoningTokens { get; init; }
    public int LastTurnTotalTokens { get; init; }
    public CodexInputBreakdown? LastTurnInputBreakdown { get; init; }

    public double FiveHourRemainingPercent => Math.Max(0, 100 - FiveHourUsedPercent);
    public double WeeklyRemainingPercent => Math.Max(0, 100 - WeeklyUsedPercent);
    public bool HasLastUserTurn => LastUserMessageText != null;
    public int LastTurnNewInputTokens => Math.Max(0, LastTurnInputTokens - LastTurnCachedInputTokens);
    public string FormattedTotalTokens => Format(TotalTokens);
    public string FormattedLastTurnTotalTokens => Format(LastTurnTotalTokens);
    public string LastTurnModelDisplay => LastTurnModel ?? "알 수 없음";
    public string LastTurnReasoningEffortDisplay => LocalizedReasoningEffort(LastTurnReasoningEffort);

    public string LastUserMessagePreview
    {
        get
        {
            var text = LastUserMessageText?.Trim();
            if (string.IsNullOrEmpty(text))
                return "—";

            var oneLine = string.Join(' ',
                text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            if (oneLine.Length <= 48)
                return oneLine;

            return oneLine[..45] + "…";
        }
    }

    public static string Format(int value) =>
        value.ToString("N0", CultureInfo.CurrentCulture);

    private static string LocalizedReasoningEffort(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "low" => "낮음",
            "medium" => "중간",
            "high" => "높음",
            "very_high" => "매우높음",
            _ => "알 수 없음"
        };
}

public enum CodexFolderAccessError
{
    MissingSessionsFolder,
    ActivationFailed
}

public static class CodexFolderAccessErrorMessages
{
    public static string GetMessage(CodexFolderAccessError error) => error switch
    {
        CodexFolderAccessError.MissingSessionsFolder =>
            "선택한 폴더에 `sessions` 폴더가 없습니다. `.codex` 폴더를 선택해 주세요.",
        CodexFolderAccessError.ActivationFailed =>
            "선택한 폴더에 접근할 수 없습니다. 다시 선택해 주세요.",
        _ => "폴더 접근에 실패했습니다."
    };
}
