using Lumina.Text.ReadOnly;
using System.Text;
using System.Text.RegularExpressions;

namespace Lifestream;

/// <summary>
/// Full-text match against sheet templates: text payloads must appear verbatim and in order, each macro payload
/// stands for any run of characters. Whitespace, soft hyphens and letter case are ignored on both sides.
/// </summary>
/// <remarks>Only depends on Lumina so the offline tests can compile this file as is.</remarks>
internal sealed class GameTextPattern
{
    private readonly Regex[] templates;

    internal GameTextPattern(IEnumerable<ReadOnlySeString> rows)
    {
        templates = rows.Select(Build).Where(x => x != null).ToArray();
    }

    internal bool Matches(string text)
    {
        if(text == null || templates.Length == 0) return false;
        var actual = Normalize(text);
        foreach(var template in templates)
        {
            if(template.IsMatch(actual)) return true;
        }
        return false;
    }

    /// <summary>A template without a letter in its fixed text would match nearly anything, so such rows are never used.</summary>
    internal static bool HasFixedText(ReadOnlySeString row) => Normalize(TextOnly(row)).Any(char.IsLetter);

    /// <returns>null when <see cref="HasFixedText"/> is false.</returns>
    internal static Regex Build(ReadOnlySeString row)
    {
        if(!HasFixedText(row)) return null;
        var pattern = new StringBuilder(@"\A");
        var wildcard = false;
        foreach(var payload in row)
        {
            if(payload.Type == ReadOnlySePayloadType.Text)
            {
                var literal = Normalize(Encoding.UTF8.GetString(payload.Body.Span));
                if(literal.Length == 0) continue;
                pattern.Append(Regex.Escape(literal));
                wildcard = false;
            }
            else if(!wildcard)
            {
                pattern.Append(".*");
                wildcard = true;
            }
        }
        return new Regex(pattern.Append(@"\z").ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    internal static string Normalize(string value) => new(value
        .Where(c => !char.IsWhiteSpace(c) && c != '\u00ad').Select(char.ToUpperInvariant).ToArray());

    /// <summary>The text payloads only, which is what the addon readers are left with once macros are dropped.</summary>
    internal static string TextOnly(ReadOnlySeString row)
    {
        var sb = new StringBuilder();
        foreach(var payload in row)
        {
            if(payload.Type == ReadOnlySePayloadType.Text) sb.Append(Encoding.UTF8.GetString(payload.Body.Span));
        }
        return sb.ToString();
    }
}
