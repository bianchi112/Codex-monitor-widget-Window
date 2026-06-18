using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexMonitorWidget.Models;

namespace CodexMonitorWidget.Services;

public static class CodexSessionParser
{
    private static readonly int TailReadSize = 512_000;
    private static readonly int HeadReadSize = 1_048_576;
    private static readonly int MaxFullReadSize = 2_000_000;

    private static readonly string[] UserRequestMarkers =
    [
        "## My request for Codex:\n",
        "## My request for Codex:"
    ];

    public static string? LatestSessionFilePath(string sessionsPath)
    {
        if (!Directory.Exists(sessionsPath))
            return null;

        var files = CollectJsonlFiles(sessionsPath);
        if (files.Count == 0)
            return null;

        return files
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .First();
    }

    public static CodexUsageSnapshot? LatestSnapshot(string sessionsPath)
    {
        if (!Directory.Exists(sessionsPath))
            return null;

        var files = CollectJsonlFiles(sessionsPath);
        if (files.Count == 0)
            return null;

        var sorted = files
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .Take(12);

        CodexUsageSnapshot? best = null;
        foreach (var file in sorted)
        {
            var snapshot = LastTokenCount(file);
            if (snapshot == null)
                continue;

            if (best == null || snapshot.Timestamp > best.Timestamp)
                best = snapshot;
        }

        return best;
    }

    public static CodexUsageSnapshot? LastTokenCount(string filePath)
    {
        var context = ReadParseContext(filePath);
        if (context.TailLines.Count == 0)
            return null;

        var userTurn = ParseLastUserTurn(context.TailLines);
        var breakdown = ParseInputBreakdown(
            context.HeadLines,
            context.TailLines,
            userTurn?.InputTokens ?? 0,
            userTurn?.CachedInputTokens ?? 0);

        for (var i = context.TailLines.Count - 1; i >= 0; i--)
        {
            var line = context.TailLines[i];
            if (!line.Contains("token_count", StringComparison.Ordinal))
                continue;

            var snapshot = ParseTokenCountLine(line, filePath, userTurn, breakdown);
            if (snapshot != null)
                return snapshot;
        }

        return null;
    }

    public static string ExtractUserRequest(string message)
    {
        foreach (var marker in UserRequestMarkers)
        {
            var index = message.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
                return message[(index + marker.Length)..].Trim();
        }

        return message.Trim();
    }

    private sealed record ParseContext(IReadOnlyList<string> HeadLines, IReadOnlyList<string> TailLines);

    private sealed record UserTurn(
        string Text,
        string? Model,
        string? ReasoningEffort,
        int InputTokens,
        int CachedInputTokens,
        int OutputTokens,
        int ReasoningTokens,
        int TotalTokens);

    private sealed record TokenUsageTotals(
        int InputTokens,
        int CachedInputTokens,
        int OutputTokens,
        int ReasoningOutputTokens,
        int TotalTokens)
    {
        public static TokenUsageTotals Zero { get; } = new(0, 0, 0, 0, 0);

        public TokenUsageTotals DeltaFrom(TokenUsageTotals baseline) => new(
            InputTokens - baseline.InputTokens,
            CachedInputTokens - baseline.CachedInputTokens,
            OutputTokens - baseline.OutputTokens,
            ReasoningOutputTokens - baseline.ReasoningOutputTokens,
            TotalTokens - baseline.TotalTokens);
    }

    private sealed record TurnContextInfo(string? Model, string? Effort);

    private enum InputComponent
    {
        SystemPrompt,
        AgentsMd,
        Environment,
        IdeContext,
        UserInput,
        PreviousConversation,
        ToolResults
    }

    private static readonly Dictionary<InputComponent, string> ComponentLabels = new()
    {
        [InputComponent.SystemPrompt] = "기본 프롬프트",
        [InputComponent.AgentsMd] = "AGENTS.md",
        [InputComponent.Environment] = "환경 정보",
        [InputComponent.IdeContext] = "IDE 컨텍스트",
        [InputComponent.UserInput] = "내 입력",
        [InputComponent.PreviousConversation] = "이전 대화",
        [InputComponent.ToolResults] = "도구 결과"
    };

    private static readonly Dictionary<string, InputComponent> KeyToComponent = new()
    {
        ["systemPrompt"] = InputComponent.SystemPrompt,
        ["agentsMd"] = InputComponent.AgentsMd,
        ["environment"] = InputComponent.Environment,
        ["ideContext"] = InputComponent.IdeContext,
        ["userInput"] = InputComponent.UserInput,
        ["previousConversation"] = InputComponent.PreviousConversation,
        ["toolResults"] = InputComponent.ToolResults
    };

    private static ParseContext ReadParseContext(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var fileSize = fileInfo.Length;
        if (fileSize <= 0)
            return new ParseContext([], []);

        if (fileSize <= MaxFullReadSize)
        {
            try
            {
                var content = File.ReadAllText(filePath, Encoding.UTF8);
                var lines = SplitLines(content);
                return new ParseContext(lines, lines);
            }
            catch (IOException)
            {
                return ReadLargeFileContext(filePath, fileSize);
            }
        }

        return ReadLargeFileContext(filePath, fileSize);
    }

    private static ParseContext ReadLargeFileContext(string filePath, long fileSize)
    {
        var headLines = ReadHeadLines(filePath, fileSize);
        var tailLines = ReadTailChunk(filePath) ?? [];
        return new ParseContext(headLines, tailLines);
    }

    private static List<string> ReadHeadLines(string filePath, long fileSize)
    {
        var readSize = (int)Math.Min(HeadReadSize, fileSize);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[readSize];
        var bytesRead = stream.Read(buffer, 0, readSize);

        var chunk = DecodeUtf8Chunk(
            buffer.AsSpan(0, bytesRead),
            allowTrimLeadingBoundary: false,
            allowTrimTrailingBoundary: readSize < fileSize);

        if (chunk == null)
            return [];

        var lines = SplitLines(chunk);
        if (readSize < fileSize && lines.Count > 0)
            lines.RemoveAt(lines.Count - 1);

        return lines;
    }

    private static List<string>? ReadTailChunk(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var fileSize = fileInfo.Length;
        if (fileSize <= 0)
            return null;

        var readSize = (int)Math.Min(TailReadSize, fileSize);
        var offset = Math.Max(0, fileSize - readSize);

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[readSize];
        var bytesRead = stream.Read(buffer, 0, readSize);

        var chunk = DecodeUtf8Chunk(
            buffer.AsSpan(0, bytesRead),
            allowTrimLeadingBoundary: offset > 0,
            allowTrimTrailingBoundary: false);

        if (chunk == null)
            return null;

        var lines = SplitLines(chunk);
        if (offset > 0 && lines.Count > 0)
            lines.RemoveAt(0);

        return lines;
    }

    private static UserTurn? ParseLastUserTurn(IReadOnlyList<string> lines)
    {
        var userTurns = new List<(int LineIndex, string Text)>();
        var totalsByLineIndex = new Dictionary<int, TokenUsageTotals>();

        for (var index = 0; index < lines.Count; index++)
        {
            var text = ParseUserMessageText(lines[index]);
            if (text != null)
                userTurns.Add((index, text));

            var totals = ParseTotalUsage(lines[index]);
            if (totals != null)
                totalsByLineIndex[index] = totals;
        }

        if (userTurns.Count == 0)
            return null;

        var lastTurn = userTurns[^1];
        var turnContext = ParseTurnContext(lastTurn.LineIndex, lines);

        var baseline = TokenUsageTotals.Zero;
        for (var index = lastTurn.LineIndex - 1; index >= 0; index--)
        {
            if (totalsByLineIndex.TryGetValue(index, out var totals))
            {
                baseline = totals;
                break;
            }
        }

        var end = baseline;
        for (var index = lastTurn.LineIndex; index < lines.Count; index++)
        {
            if (index > lastTurn.LineIndex && ParseUserMessageText(lines[index]) != null)
                break;

            if (totalsByLineIndex.TryGetValue(index, out var totals))
                end = totals;
        }

        var delta = end.DeltaFrom(baseline);
        return new UserTurn(
            lastTurn.Text,
            turnContext?.Model,
            turnContext?.Effort,
            delta.InputTokens,
            delta.CachedInputTokens,
            delta.OutputTokens,
            delta.ReasoningOutputTokens,
            delta.TotalTokens);
    }

    private static TurnContextInfo? ParseTurnContext(int index, IReadOnlyList<string> lines)
    {
        if (index < 0 || index >= lines.Count)
            return null;

        for (var candidateIndex = index; candidateIndex >= 0; candidateIndex--)
        {
            var json = ParseJsonObject(lines[candidateIndex]);
            if (json == null)
                continue;

            if (!TryGetString(json, "type", out var type) || type != "turn_context")
                continue;

            if (!TryGetObject(json, "payload", out var payload))
                continue;

            TryGetString(payload, "model", out var model);
            var effort = ParseReasoningEffort(payload);

            if (!string.IsNullOrEmpty(model) || !string.IsNullOrEmpty(effort))
                return new TurnContextInfo(model, effort);
        }

        return null;
    }

    private static string? ParseReasoningEffort(Dictionary<string, object?> payload)
    {
        if (TryGetString(payload, "effort", out var effort) && !string.IsNullOrEmpty(effort))
            return effort;

        if (TryGetString(payload, "reasoning_effort", out var direct) && !string.IsNullOrEmpty(direct))
            return direct;

        if (TryGetObject(payload, "collaboration_mode", out var collaboration) &&
            TryGetObject(collaboration, "settings", out var settings) &&
            TryGetString(settings, "reasoning_effort", out var nested) &&
            !string.IsNullOrEmpty(nested))
        {
            return nested;
        }

        return null;
    }

    private static TokenUsageTotals? ParseTotalUsage(string line)
    {
        var json = ParseJsonObject(line);
        if (json == null)
            return null;

        if (!TryGetObject(json, "payload", out var payload))
            return null;

        if (!TryGetString(payload, "type", out var payloadType) || payloadType != "token_count")
            return null;

        if (!TryGetObject(payload, "info", out var info))
            return null;

        if (!TryGetObject(info, "total_token_usage", out var totalUsage))
            return null;

        return new TokenUsageTotals(
            IntValue(totalUsage.GetValueOrDefault("input_tokens")),
            IntValue(totalUsage.GetValueOrDefault("cached_input_tokens")),
            IntValue(totalUsage.GetValueOrDefault("output_tokens")),
            IntValue(totalUsage.GetValueOrDefault("reasoning_output_tokens")),
            IntValue(totalUsage.GetValueOrDefault("total_tokens")));
    }

    private static string? ParseUserMessageText(string line)
    {
        var json = ParseJsonObject(line);
        if (json == null)
            return null;

        if (!TryGetObject(json, "payload", out var payload))
            return null;

        if (!TryGetString(payload, "type", out var payloadType) || payloadType != "user_message")
            return null;

        if (!TryGetString(payload, "message", out var message))
            return null;

        return ExtractUserRequest(message);
    }

    private static CodexInputBreakdown? ParseInputBreakdown(
        IReadOnlyList<string> headLines,
        IReadOnlyList<string> tailLines,
        int turnInputTokens,
        int turnCachedTokens)
    {
        if (turnInputTokens <= 0)
            return null;

        var rawBuckets = new Dictionary<InputComponent, int>();
        var usesSinglePass = headLines.SequenceEqual(tailLines);

        if (usesSinglePass)
        {
            AccumulateUntilLastUserMessage(tailLines, rawBuckets);
        }
        else
        {
            foreach (var line in headLines)
                AccumulateStaticComponents(line, rawBuckets);

            AccumulateUntilLastUserMessage(tailLines, rawBuckets);
        }

        var scaled = ScaleBuckets(rawBuckets, turnInputTokens);
        var components = new List<CodexInputBreakdownItem>();

        foreach (var key in CodexInputBreakdown.ComponentOrder)
        {
            if (!KeyToComponent.TryGetValue(key, out var component))
                continue;

            if (!scaled.TryGetValue(component, out var tokens) || tokens <= 0)
                continue;

            components.Add(new CodexInputBreakdownItem
            {
                Id = key,
                Label = ComponentLabels[component],
                Tokens = tokens,
                IsExact = false
            });
        }

        return new CodexInputBreakdown
        {
            ExactCachedTokens = turnCachedTokens,
            ExactNewInputTokens = Math.Max(0, turnInputTokens - turnCachedTokens),
            Components = components
        };
    }

    private static void AccumulateUntilLastUserMessage(
        IReadOnlyList<string> lines,
        Dictionary<InputComponent, int> buckets)
    {
        var lastUserMessageIndex = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (ParseUserMessageText(lines[i]) != null)
            {
                lastUserMessageIndex = i;
                break;
            }
        }

        if (lastUserMessageIndex < 0)
        {
            foreach (var line in lines)
                AccumulateInputComponents(line, false, buckets);
            return;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var lastReached = index == lastUserMessageIndex;
            AccumulateInputComponents(lines[index], lastReached, buckets);
            if (lastReached)
                break;
        }
    }

    private static void AccumulateStaticComponents(string line, Dictionary<InputComponent, int> buckets)
    {
        var json = ParseJsonObject(line);
        if (json == null)
            return;

        if (!TryGetString(json, "type", out var type) || type != "response_item")
            return;

        if (!TryGetObject(json, "payload", out var payload))
            return;

        if (!TryGetString(payload, "type", out var payloadType) || payloadType != "message")
            return;

        var role = TryGetString(payload, "role", out var roleValue) ? roleValue : "";

        if (role == "developer")
        {
            foreach (var text in InputTexts(payload))
                AddText(text, InputComponent.SystemPrompt, buckets);
            return;
        }

        if (role == "user")
        {
            foreach (var text in InputTexts(payload))
            {
                if (text.Contains("AGENTS.md instructions", StringComparison.Ordinal))
                    AddText(text, InputComponent.AgentsMd, buckets);
                else if (text.Contains("<environment_context>", StringComparison.Ordinal))
                    AddText(text, InputComponent.Environment, buckets);
            }
        }
    }

    private static void AccumulateInputComponents(
        string line,
        bool lastUserMessageReached,
        Dictionary<InputComponent, int> buckets)
    {
        var json = ParseJsonObject(line);
        if (json == null)
            return;

        var type = TryGetString(json, "type", out var typeValue) ? typeValue : "";
        var payload = TryGetObject(json, "payload", out var payloadObj) ? payloadObj : [];
        var payloadType = TryGetString(payload, "type", out var payloadTypeValue) ? payloadTypeValue : "";

        if (type == "event_msg" && payloadType == "user_message")
        {
            if (TryGetString(payload, "message", out var message))
                AccumulateUserMessageText(message, buckets);
            return;
        }

        if (lastUserMessageReached)
            return;

        if (type == "event_msg" && payloadType == "agent_message")
        {
            if (TryGetString(payload, "message", out var message))
                AddText(message, InputComponent.PreviousConversation, buckets);
            return;
        }

        if (type != "response_item")
            return;

        switch (payloadType)
        {
            case "message":
            {
                var role = TryGetString(payload, "role", out var roleValue) ? roleValue : "";
                if (role == "developer")
                {
                    foreach (var text in InputTexts(payload))
                        AddText(text, InputComponent.SystemPrompt, buckets);
                }
                else if (role == "user")
                {
                    foreach (var text in InputTexts(payload))
                        AccumulateUserContentText(text, buckets);
                }
                else if (role == "assistant")
                {
                    foreach (var text in OutputTexts(payload))
                        AddText(text, InputComponent.PreviousConversation, buckets);
                }

                break;
            }
            case "function_call":
                if (TryGetString(payload, "arguments", out var arguments))
                    AddText(arguments, InputComponent.ToolResults, buckets);
                break;
            case "function_call_output":
                if (TryGetString(payload, "output", out var output))
                    AddText(output, InputComponent.ToolResults, buckets);
                break;
            case "reasoning":
                if (payload.TryGetValue("summary", out var summaryObj) &&
                    summaryObj is JsonElement summaryElement &&
                    summaryElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in summaryElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("text", out var textProp) &&
                            textProp.ValueKind == JsonValueKind.String)
                        {
                            AddText(textProp.GetString() ?? "", InputComponent.PreviousConversation, buckets);
                        }
                    }
                }

                break;
        }
    }

    private static void AccumulateUserMessageText(string message, Dictionary<InputComponent, int> buckets)
    {
        foreach (var marker in UserRequestMarkers)
        {
            var index = message.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var idePart = message[..index];
                var userPart = message[(index + marker.Length)..];
                AddText(idePart, InputComponent.IdeContext, buckets);
                AddText(userPart, InputComponent.UserInput, buckets);
                return;
            }
        }

        AddText(message, InputComponent.UserInput, buckets);
    }

    private static void AccumulateUserContentText(string text, Dictionary<InputComponent, int> buckets)
    {
        if (text.Contains("AGENTS.md instructions", StringComparison.Ordinal))
            AddText(text, InputComponent.AgentsMd, buckets);
        else if (text.Contains("<environment_context>", StringComparison.Ordinal))
            AddText(text, InputComponent.Environment, buckets);
        else if (text.Contains("# Context from my IDE setup", StringComparison.Ordinal) ||
                 text.Contains(UserRequestMarkers[0], StringComparison.Ordinal))
            AccumulateUserMessageText(text, buckets);
        else
            AddText(text, InputComponent.UserInput, buckets);
    }

    private static void AddText(string text, InputComponent component, Dictionary<InputComponent, int> buckets)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return;

        buckets.TryGetValue(component, out var current);
        buckets[component] = current + EstimateTokenCount(trimmed);
    }

    private static Dictionary<InputComponent, int> ScaleBuckets(
        Dictionary<InputComponent, int> buckets,
        int target)
    {
        var rawTotal = buckets.Values.Sum();
        if (rawTotal <= 0)
            return [];

        var scaled = buckets.ToDictionary(
            kv => kv.Key,
            kv => (int)Math.Round((double)kv.Value / rawTotal * target));

        var diff = target - scaled.Values.Sum();
        if (diff != 0)
        {
            var largest = scaled.MaxBy(kv => kv.Value).Key;
            scaled[largest] += diff;
        }

        return scaled;
    }

    private static List<string> InputTexts(Dictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("content", out var contentObj) || contentObj is not JsonElement content ||
            content.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "input_text" &&
                item.TryGetProperty("text", out var textProp) &&
                textProp.ValueKind == JsonValueKind.String)
            {
                var text = textProp.GetString();
                if (text != null)
                    result.Add(text);
            }
        }

        return result;
    }

    private static List<string> OutputTexts(Dictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("content", out var contentObj) || contentObj is not JsonElement content ||
            content.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "output_text" &&
                item.TryGetProperty("text", out var textProp) &&
                textProp.ValueKind == JsonValueKind.String)
            {
                var text = textProp.GetString();
                if (text != null)
                    result.Add(text);
            }
        }

        return result;
    }

    private static Dictionary<string, object?>? ParseJsonObject(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return JsonElementToDictionary(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
            dict[property.Name] = property.Value.Clone();

        return dict;
    }

    private static bool TryGetString(Dictionary<string, object?> dict, string key, out string value)
    {
        value = "";
        if (!dict.TryGetValue(key, out var obj))
            return false;

        if (obj is string s)
        {
            value = s;
            return true;
        }

        if (obj is JsonElement element && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? "";
            return true;
        }

        return false;
    }

    private static bool TryGetObject(Dictionary<string, object?> dict, string key, out Dictionary<string, object?> value)
    {
        value = [];
        if (!dict.TryGetValue(key, out var obj) || obj is not JsonElement element ||
            element.ValueKind != JsonValueKind.Object)
            return false;

        value = JsonElementToDictionary(element);
        return true;
    }

    private static string? DecodeUtf8Chunk(
        ReadOnlySpan<byte> data,
        bool allowTrimLeadingBoundary,
        bool allowTrimTrailingBoundary)
    {
        if (TryDecodeUtf8(data, out var direct))
            return direct;

        var maxLeadingTrim = allowTrimLeadingBoundary ? Math.Min(3, data.Length) : 0;
        var maxTrailingTrim = allowTrimTrailingBoundary ? Math.Min(3, data.Length) : 0;

        for (var leadingTrim = 0; leadingTrim <= maxLeadingTrim; leadingTrim++)
        {
            for (var trailingTrim = 0; trailingTrim <= maxTrailingTrim; trailingTrim++)
            {
                var upperBound = data.Length - trailingTrim;
                if (leadingTrim >= upperBound)
                    continue;

                if (TryDecodeUtf8(data.Slice(leadingTrim, upperBound - leadingTrim), out var candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> data, out string? result)
    {
        try
        {
            result = Encoding.UTF8.GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            result = null;
            return false;
        }
    }

    private static int EstimateTokenCount(string text)
    {
        if (text.Length == 0)
            return 0;

        var cjkCount = 0;
        var otherCount = 0;

        foreach (var ch in text)
        {
            if (IsCjk(ch))
                cjkCount++;
            else if (!char.IsWhiteSpace(ch))
                otherCount++;
        }

        var tokens = (int)Math.Ceiling(cjkCount / 1.5) + (int)Math.Ceiling(otherCount / 4.0);
        return Math.Max(1, tokens);
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uAC00' and <= '\uD7AF'
            or >= '\u3040' and <= '\u309F'
            or >= '\u30A0' and <= '\u30FF';
    }

    private static List<string> CollectJsonlFiles(string directory)
    {
        var result = new List<string>();
        if (!Directory.Exists(directory))
            return result;

        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.Hidden) != 0)
                continue;

            result.Add(file);
        }

        return result;
    }

    private static List<string> SplitLines(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static CodexUsageSnapshot? ParseTokenCountLine(
        string line,
        string sourceFile,
        UserTurn? userTurn,
        CodexInputBreakdown? breakdown)
    {
        var json = ParseJsonObject(line);
        if (json == null)
            return null;

        if (!TryGetObject(json, "payload", out var payload))
            return null;

        if (!TryGetString(payload, "type", out var payloadType) || payloadType != "token_count")
            return null;

        if (!TryGetObject(payload, "info", out var info))
            return null;

        if (!TryGetObject(info, "total_token_usage", out var totalUsage))
            return null;

        if (!TryGetObject(info, "last_token_usage", out var lastUsage))
            return null;

        if (!TryGetObject(payload, "rate_limits", out var rateLimits))
            return null;

        if (!TryGetObject(rateLimits, "primary", out var primary))
            return null;

        if (!TryGetObject(rateLimits, "secondary", out var secondary))
            return null;

        var fiveHourUsed = DoubleValue(primary.GetValueOrDefault("used_percent"));
        var weeklyUsed = DoubleValue(secondary.GetValueOrDefault("used_percent"));
        var fiveHourReset = UnixDate(primary.GetValueOrDefault("resets_at"));
        var weeklyReset = UnixDate(secondary.GetValueOrDefault("resets_at"));

        if (fiveHourUsed == null || weeklyUsed == null || fiveHourReset == null || weeklyReset == null)
            return null;

        var timestamp = DateTime.MinValue;
        if (TryGetString(json, "timestamp", out var timestampString) &&
            DateTime.TryParse(timestampString, null, DateTimeStyles.RoundtripKind, out var parsed))
        {
            timestamp = parsed;
        }

        var planType = TryGetString(rateLimits, "plan_type", out var plan) ? plan : "unknown";

        return new CodexUsageSnapshot
        {
            Timestamp = timestamp,
            TotalTokens = IntValue(totalUsage.GetValueOrDefault("total_tokens")),
            InputTokens = IntValue(totalUsage.GetValueOrDefault("input_tokens")),
            CachedInputTokens = IntValue(totalUsage.GetValueOrDefault("cached_input_tokens")),
            OutputTokens = IntValue(totalUsage.GetValueOrDefault("output_tokens")),
            ReasoningOutputTokens = IntValue(totalUsage.GetValueOrDefault("reasoning_output_tokens")),
            LastTotalTokens = IntValue(lastUsage.GetValueOrDefault("total_tokens")),
            LastInputTokens = IntValue(lastUsage.GetValueOrDefault("input_tokens")),
            LastCachedInputTokens = IntValue(lastUsage.GetValueOrDefault("cached_input_tokens")),
            LastOutputTokens = IntValue(lastUsage.GetValueOrDefault("output_tokens")),
            LastReasoningOutputTokens = IntValue(lastUsage.GetValueOrDefault("reasoning_output_tokens")),
            FiveHourUsedPercent = fiveHourUsed.Value,
            FiveHourResetAt = fiveHourReset.Value,
            WeeklyUsedPercent = weeklyUsed.Value,
            WeeklyResetAt = weeklyReset.Value,
            PlanType = planType,
            SourceFile = sourceFile,
            LastUserMessageText = userTurn?.Text,
            LastTurnModel = userTurn?.Model,
            LastTurnReasoningEffort = userTurn?.ReasoningEffort,
            LastTurnInputTokens = userTurn?.InputTokens ?? 0,
            LastTurnCachedInputTokens = userTurn?.CachedInputTokens ?? 0,
            LastTurnOutputTokens = userTurn?.OutputTokens ?? 0,
            LastTurnReasoningTokens = userTurn?.ReasoningTokens ?? 0,
            LastTurnTotalTokens = userTurn?.TotalTokens ?? 0,
            LastTurnInputBreakdown = breakdown
        };
    }

    private static int IntValue(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var i) => i,
        JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
        _ => 0
    };

    private static double? DoubleValue(object? value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
        _ => null
    };

    private static DateTime? UnixDate(object? value)
    {
        return value switch
        {
            int i => DateTimeOffset.FromUnixTimeSeconds(i).LocalDateTime,
            long l => DateTimeOffset.FromUnixTimeSeconds(l).LocalDateTime,
            double d => DateTimeOffset.FromUnixTimeSeconds((long)d).LocalDateTime,
            JsonElement { ValueKind: JsonValueKind.Number } element => DateTimeOffset
                .FromUnixTimeSeconds((long)element.GetDouble()).LocalDateTime,
            _ => null
        };
    }
}
