using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class StringExtensions
{
    public static IEnumerable<string> SplitLines(this string value) =>
        Regex.Split(value, @"\r?\n").Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim());
}
