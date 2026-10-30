using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class AzurePipelines
{
    public static void SetOutputVariable(string name, string? value, bool isSecret = false)
    {
        var escaped = EscapeValue(value ?? "");
        var secretFlag = isSecret ? ";issecret=true" : "";
        Console.WriteLine($"##vso[task.setvariable variable={name};isOutput=true{secretFlag}]{escaped}");
    }

    internal static string EscapeValue(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal);
}
