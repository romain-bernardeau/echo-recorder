using System.Text.RegularExpressions;

namespace Echo.Ai;

/// <summary>
/// Prompt-injection hygiene for the chat prompts (CR, summary, postprocess): every piece of
/// meeting DATA (transcript, partial notes, meeting name, segments) travels between explicit
/// tags, and the system prompt states that tagged content is data, never an instruction.
/// A literal closing tag inside the data is neutralised so a participant (or a crafted
/// meeting title) cannot "close" the block and speak to the model.
/// </summary>
public static class PromptData
{
    /// <summary>"&lt;tag&gt;\n…\n&lt;/tag&gt;" with any &lt;tag&gt; / &lt;/tag&gt; inside the content defused.</summary>
    public static string Wrap(string tag, string? content)
        => $"<{tag}>\n{Defuse(tag, content)}\n</{tag}>";

    /// <summary>Single-line variant for short values (meeting name).</summary>
    public static string Inline(string tag, string? content)
        => $"<{tag}>{Defuse(tag, (content ?? "").Replace('\r', ' ').Replace('\n', ' '))}</{tag}>";

    private static string Defuse(string tag, string? content)
        => Regex.Replace(content ?? "", $@"<\s*(/?)\s*{Regex.Escape(tag)}\b[^>]*>", "[$1" + tag + "]", RegexOptions.IgnoreCase);
}
