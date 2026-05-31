using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CardioSimulator.Core.Domain;

internal static class ParserHelpers
{
    public static (string Key, string Value)? SplitKeyValue(string line)
    {
        var i = line.IndexOf(':');
        if (i <= 0) return null;
        return (line.Substring(0, i).Trim(), line.Substring(i + 1));
    }

    public static Dictionary<string, string> ParseSemicolonFields(string line)
    {
        var map = new Dictionary<string, string>();
        foreach (var field in line.Split(';'))
        {
            var kv = SplitKeyValue(field);
            if (kv is null) continue;
            map[kv.Value.Key.Trim()] = kv.Value.Value.Trim();
        }
        return map;
    }

    public static Dictionary<string, string> ParseKeyValueLines(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var kv = SplitKeyValue(line);
            if (kv is null) continue;
            map[kv.Value.Key.Trim()] = kv.Value.Value.Trim();
        }
        return map;
    }

    public static (Dictionary<string, string> Header, List<string> Body) SplitHeader(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var header = new Dictionary<string, string>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; break; }
            var kv = SplitKeyValue(line);
            if (kv is null) { i++; continue; }
            header[kv.Value.Key] = kv.Value.Value.Trim();
            i++;
        }
        var body = lines.Skip(i).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return (header, body);
    }

    public static string? Get(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;

    public static int? ToIntOrNull(string? s) =>
        s is not null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
