namespace Echo.Ai;

/// <summary>
/// Per-provider/model context budget for the chat calls (CR, summary). Llama 3.3 on
/// Workers AI has a ~24 k token window: a 1 h 30 transcript (~90 k chars) sent whole is
/// refused, so long meetings go through a map-reduce sized from this table instead.
/// Token counts are ESTIMATED (characters / 3.5, conservative for French) since none of
/// the providers exposes a tokenizer client-side.
/// </summary>
public static class ContextBudget
{
    /// <summary>Average characters per token used for every estimate.</summary>
    public const double CharsPerToken = 3.5;

    /// <summary>Share of the window actually planned for: the estimate is approximate.</summary>
    public const double SafetyFactor = 0.9;

    /// <summary>Context window (tokens) of a chat model. Unknown Cloudflare models get the
    /// Llama 3.x figure by prudence; unknown Mistral models the 128 k of the current family.</summary>
    public static int ContextTokens(string? provider, string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        switch (LlmProvider.Normalize(provider))
        {
            case LlmProvider.Gemini:
                return 1_000_000;
            case LlmProvider.Cloudflare:
                return 24_000;
            default:
                // Legacy open-weight 7B / Mixtral endpoints are 32 k; large/medium/small/ministral/
                // magistral/nemo are 128 k.
                if (m.Contains("7b") || m.Contains("mixtral")) return 32_000;
                return 128_000;
        }
    }

    /// <summary>Tokens kept free for the answer. Workers AI counts max_tokens inside the
    /// window, so it is kept tight there (a CR is 3 to 5 k tokens).</summary>
    public static int OutputReserveTokens(string? provider)
        => LlmProvider.Normalize(provider) == LlmProvider.Cloudflare ? 4_096 : 8_192;

    /// <summary>Largest prompt (system + user, tokens) planned for this model.</summary>
    public static int InputBudgetTokens(string? provider, string? model)
        => Math.Max(2_000, (int)(ContextTokens(provider, model) * SafetyFactor) - OutputReserveTokens(provider));

    public static int EstimateTokens(string? text)
        => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>Estimated tokens of a whole message list (+4 per message for the role framing).</summary>
    public static int EstimateTokens(IEnumerable<ChatMessage> messages)
        => messages.Sum(m => EstimateTokens(m.Content) + 4);

    public static bool Fits(string? provider, string? model, IEnumerable<ChatMessage> messages)
        => EstimateTokens(messages) <= InputBudgetTokens(provider, model);

    /// <summary>Characters of DATA (transcript, notes) that fit next to a prompt whose fixed part
    /// is <paramref name="overheadChars"/> characters long. Never below 4 000.</summary>
    public static int DataBudgetChars(string? provider, string? model, int overheadChars)
        => Math.Max(4_000, (int)(InputBudgetTokens(provider, model) * CharsPerToken) - overheadChars);

    /// <summary>
    /// Split a speaker-attributed transcript into parts of at most <paramref name="maxChars"/>,
    /// cutting only BETWEEN speaker turns (a turn starts on a "[mm:ss] Name :" header line) so a
    /// part never starts mid-sentence with no speaker. A single turn larger than the budget is
    /// split at its line boundaries (then hard-split) as a last resort.
    /// </summary>
    public static List<string> SplitAtTurns(string text, int maxChars)
    {
        var blocks = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (IsTurnHeader(line) && cur.Length > 0)
            {
                blocks.Add(cur.ToString().TrimEnd('\n'));
                cur.Clear();
            }
            cur.Append(line).Append('\n');
        }
        if (cur.Length > 0) blocks.Add(cur.ToString().TrimEnd('\n'));

        var parts = new List<string>();
        var part = new System.Text.StringBuilder();
        foreach (var b in blocks)
        {
            if (part.Length > 0 && part.Length + b.Length + 1 > maxChars)
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            if (b.Length > maxChars)
            {
                parts.AddRange(Summarizer.SplitAtLines(b, maxChars));
                continue;
            }
            if (part.Length > 0) part.Append('\n');
            part.Append(b);
        }
        if (part.Length > 0) parts.Add(part.ToString());
        if (parts.Count == 0) parts.Add(text ?? "");
        return parts;
    }

    private static readonly System.Text.RegularExpressions.Regex TurnHeaderRx =
        new(@"^\[(\d{1,3}:\d{2}(:\d{2})?|\?\?:\?\?)\]\s", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool IsTurnHeader(string line) => TurnHeaderRx.IsMatch(line);
}
