namespace Hilke.Ant.Cli;

/// <summary>Tab-completion for the command line (commands, scan args, device tokens).</summary>
public static class Completion
{
    public static readonly IReadOnlyList<string> Commands = new[]
    {
        "help", "scan", "devices", "connect", "disconnect", "forget",
        "info", "alias", "power", "resistance", "calibrate", "quit", "exit",
    };

    private static readonly HashSet<string> DeviceArgCommands = new(StringComparer.Ordinal)
    {
        "connect", "disconnect", "forget", "info", "alias", "power", "resistance", "calibrate",
    };

    /// <summary>
    /// Complete <paramref name="input"/> against commands, scan args, or device
    /// <paramref name="tokens"/>. Returns the input with the final word replaced by the longest
    /// common prefix of the candidates (a sole candidate gets a trailing space). When more than one
    /// candidate matches, <paramref name="candidates"/> holds them for display.
    /// </summary>
    public static string Complete(string input, IReadOnlyList<string> tokens, out IReadOnlyList<string> candidates)
    {
        candidates = Array.Empty<string>();
        input ??= "";
        bool trailingSpace = input.Length > 0 && char.IsWhiteSpace(input[^1]);
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int wordIndex = trailingSpace ? parts.Length : Math.Max(0, parts.Length - 1);
        string prefix = trailingSpace || parts.Length == 0 ? "" : parts[^1];

        IReadOnlyList<string> pool;
        if (wordIndex == 0)
        {
            pool = Commands;
        }
        else
        {
            string cmd = parts.Length > 0 ? parts[0] : "";
            if (cmd == "scan" && wordIndex == 1)
                pool = new[] { "on", "off" };
            else if (DeviceArgCommands.Contains(cmd) && wordIndex == 1)
                pool = tokens;
            else
                return input; // nothing to complete in this slot
        }

        var matches = pool
            .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
            return input;

        string replacement = matches.Count == 1 ? matches[0] + " " : LongestCommonPrefix(matches);
        if (matches.Count > 1)
            candidates = matches;

        var head = parts.Take(wordIndex);
        string headStr = string.Join(" ", head);
        return headStr.Length > 0 ? headStr + " " + replacement : replacement;
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return "";
        string first = values[0];
        int len = first.Length;
        foreach (var v in values)
        {
            int i = 0;
            int max = Math.Min(len, v.Length);
            while (i < max && char.ToLowerInvariant(first[i]) == char.ToLowerInvariant(v[i]))
                i++;
            len = i;
            if (len == 0)
                break;
        }
        return first.Substring(0, len);
    }
}
