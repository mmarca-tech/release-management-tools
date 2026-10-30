using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

public sealed class CliOptions
{
    private readonly Dictionary<string, string?> values;
    private readonly HashSet<string> switches;

    private CliOptions(Dictionary<string, string?> values, HashSet<string> switches)
    {
        this.values = values;
        this.switches = switches;
    }

    public static CliOptions Parse(IEnumerable<string> rawArgs)
    {
        var args = rawArgs.ToArray();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!IsOptionToken(token))
            {
                throw new CliException($"Unexpected argument '{token}'. Options must use -Name value syntax.");
            }

            var name = token.TrimStart('-');
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CliException("Empty option name.");
            }

            if (index + 1 < args.Length && !IsOptionToken(args[index + 1]))
            {
                values[name] = Unquote(args[index + 1]);
                index++;
            }
            else
            {
                switches.Add(name);
                values[name] = null;
            }
        }

        return new CliOptions(values, switches);
    }

    public string? Get(string name, string? defaultValue = null) =>
        values.TryGetValue(name, out var value) ? value ?? defaultValue : defaultValue;

    public string Require(string name)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliException($"Missing required parameter '{name}'.");
        }

        return value;
    }

    public bool HasSwitch(string name) => switches.Contains(name);

    public int GetInt(string name, int defaultValue)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new CliException($"Invalid integer value for '{name}': '{value}'.");
        }

        return parsed;
    }

    private static bool IsOptionToken(string value) => value.StartsWith('-') && value.Length > 1;

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '\'' && last == '\'') || (first == '"' && last == '"'))
            {
                return value[1..^1];
            }
        }

        return value;
    }
}
