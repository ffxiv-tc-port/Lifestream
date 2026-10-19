using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using System.Threading;

namespace Lifestream;

// Only depends on Lumina so the offline tests can compile this file as is; Lang wires Excel and Log.

/// <summary>One place a piece of game text is read from. <see cref="Read"/> returns null when the row does not exist.</summary>
internal sealed class GameTextSource
{
    internal readonly string Label;
    private readonly Func<ExcelModule, ReadOnlySeString?> read;

    private GameTextSource(string label, Func<ExcelModule, ReadOnlySeString?> read)
    {
        Label = label;
        this.read = read;
    }

    internal ReadOnlySeString? Read(ExcelModule excel) => read(excel);

    /// <summary>Quest dialogue sub-sheets are looked up by key: their row ids shift between game versions.</summary>
    internal static GameTextSource Dialogue(string sheet, string key) => new($"{sheet}:{key}", excel =>
    {
        foreach(var row in excel.GetSheet<RawRow>(null, sheet))
        {
            if(GameTextPattern.TextOnly(row.ReadStringColumn(0)) == key) return row.ReadStringColumn(1);
        }
        return null;
    });

    internal static GameTextSource AddonRow(uint row) => new($"Addon#{row}", excel => excel.GetSheet<Addon>().GetRowOrDefault(row)?.Text);
    internal static GameTextSource LobbyRow(uint row) => new($"Lobby#{row}", excel => excel.GetSheet<Lobby>().GetRowOrDefault(row)?.Text);
    internal static GameTextSource EObjNameRow(uint row) => new($"EObjName#{row}", excel => excel.GetSheet<EObjName>().GetRowOrDefault(row)?.Singular);
    internal static GameTextSource WarpQuestion(uint row) => new($"Warp#{row}", excel => excel.GetSheet<Warp>().GetRowOrDefault(row)?.Question);
}

/// <summary>
/// Game text first, the built-in strings second. <see cref="Legacy"/> keeps the literals exactly as they were, and
/// every call site passes the comparison it used on them before, so anything that matched before still matches.
/// Unusable rows, and the first time a group needs the built-in strings, are logged once per group (Information).
/// </summary>
internal sealed class GameTextMatcher
{
    internal static Func<ExcelModule> Excel;
    internal static Action<string> Log;

    private enum Kind { Exact, Contains, Template }

    private sealed class Resolved
    {
        internal string[] Texts = [];
        internal GameTextPattern Pattern = new([]);
        internal string[] Used = [];
        internal bool Empty => Used.Length == 0;
    }

    internal readonly string Name;
    internal readonly string[] Legacy;
    private readonly Kind kind;
    private readonly GameTextSource[] sources;
    private readonly Lazy<Resolved> resolved;
    private int fallbackLogged;

    private GameTextMatcher(string name, Kind kind, GameTextSource[] sources, string[] legacy)
    {
        Name = name;
        this.kind = kind;
        this.sources = sources;
        Legacy = legacy;
        resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Whole text equal to the sheet text (menu entries, object names).</summary>
    internal static GameTextMatcher Exact(string name, GameTextSource[] sources, string[] legacy) => new(name, Kind.Exact, sources, legacy);

    /// <summary>Sheet text appearing anywhere in the text.</summary>
    internal static GameTextMatcher Contains(string name, GameTextSource[] sources, string[] legacy) => new(name, Kind.Contains, sources, legacy);

    /// <summary>Sheet rows used as <see cref="GameTextPattern"/> templates (dialogs with names, numbers or places in them).</summary>
    internal static GameTextMatcher Template(string name, GameTextSource[] sources, string[] legacy) => new(name, Kind.Template, sources, legacy);

    internal bool MatchesSheet(string text)
    {
        if(text == null) return false;
        var r = resolved.Value;
        if(r.Empty) return false;
        if(kind == Kind.Template) return r.Pattern.Matches(text);
        var actual = GameTextPattern.Normalize(text);
        foreach(var t in r.Texts)
        {
            if(kind == Kind.Exact ? actual == t : actual.Contains(t, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    internal bool Matches(string text, Func<string, string[], bool> legacy)
    {
        if(MatchesSheet(text)) return true;
        if(text == null || !legacy(text, Legacy)) return false;
        NoteFallback(text);
        return true;
    }

    /// <summary>First entry matching the game text; only when none does, the first one the built-in strings match.</summary>
    internal int FindIndex(IReadOnlyList<string> entries, Func<string, string[], bool> legacy)
    {
        for(var i = 0; i < entries.Count; i++)
        {
            if(MatchesSheet(entries[i])) return i;
        }
        for(var i = 0; i < entries.Count; i++)
        {
            if(entries[i] != null && legacy(entries[i], Legacy))
            {
                NoteFallback(entries[i]);
                return i;
            }
        }
        return -1;
    }

    internal string Describe()
    {
        var r = resolved.Value;
        var game = r.Empty ? "(no game text)" : kind == Kind.Template ? string.Join(" | ", r.Used) : string.Join(" | ", r.Texts);
        return $"{game} | built-in: {string.Join(", ", Legacy)}";
    }

    public override string ToString() => Name;

    private void NoteFallback(string text)
    {
        if(Interlocked.Exchange(ref fallbackLogged, 1) != 0) return;
        var r = resolved.Value;
        Log?.Invoke(r.Empty
            ? $"[GameText] {Name}: \"{text}\" was matched by the built-in strings (no game text available for this group)."
            : $"[GameText] {Name}: game text from {string.Join(", ", r.Used)} did not match \"{text}\"; the built-in strings did, so they were used.");
    }

    private Resolved Resolve()
    {
        var texts = new List<string>();
        var rows = new List<ReadOnlySeString>();
        var used = new List<string>();
        var problems = new List<string>();
        foreach(var source in sources)
        {
            try
            {
                var excel = Excel?.Invoke() ?? throw new InvalidOperationException("game data is not available");
                var value = source.Read(excel);
                if(value == null)
                {
                    problems.Add($"{source.Label} (row not found)");
                    continue;
                }
                // Also guards Exact/Contains: an empty text would make Contains match every string.
                if(!GameTextPattern.HasFixedText(value.Value))
                {
                    problems.Add($"{source.Label} (no fixed text)");
                    continue;
                }
                if(kind == Kind.Template) rows.Add(value.Value);
                else texts.Add(GameTextPattern.Normalize(GameTextPattern.TextOnly(value.Value)));
                used.Add(source.Label);
            }
            catch(Exception e)
            {
                problems.Add($"{source.Label} ({e.GetType().Name}: {e.Message})");
            }
        }
        var r = new Resolved();
        try
        {
            r.Texts = [.. texts.Distinct()];
            r.Pattern = new GameTextPattern(rows);
            r.Used = [.. used];
        }
        catch(Exception e)
        {
            r = new Resolved();
            problems.Add($"building the matcher failed ({e.GetType().Name}: {e.Message})");
        }
        if(problems.Count > 0)
        {
            Log?.Invoke(r.Empty
                ? $"[GameText] {Name}: no usable game text ({string.Join("; ", problems)}); matching with the built-in strings only."
                : $"[GameText] {Name}: skipped {string.Join("; ", problems)}; still using {string.Join(", ", r.Used)} before the built-in strings.");
        }
        return r;
    }
}

/// <summary>The comparisons the call sites applied to the built-in strings before game text was read; unchanged on purpose.</summary>
internal static class LegacyMatch
{
    internal static bool EqualsAny(string text, string[] legacy) => legacy.Any(x => x.Equals(text));
    internal static bool EqualsIgnoreCaseAny(string text, string[] legacy) => legacy.Any(x => x.Equals(text, StringComparison.OrdinalIgnoreCase));
    internal static bool ContainsAny(string text, string[] legacy) => legacy.Any(x => text.Contains(x));
    internal static bool ContainsAnyIgnoreCase(string text, string[] legacy) => legacy.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));

    internal static bool SpacelessContainsAny(string text, string[] legacy)
    {
        var t = text.Replace(" ", "");
        return legacy.Any(x => t.Contains(x.Replace(" ", "")));
    }

    internal static bool SpacelessEqualsAny(string text, string[] legacy)
    {
        var t = text.Replace(" ", "");
        return legacy.Any(x => x.Replace(" ", "").Equals(t));
    }
}
