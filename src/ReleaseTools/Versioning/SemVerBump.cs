using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

/// <summary>
/// Detects semantic version bump type from commit messages.
/// Highest severity wins: major &gt; minor &gt; patch (fix/patch).
/// </summary>
public static class SemVerBump
{
    // Line prefix: optional bullets / Merged PR, then conventional keyword.
    // Also accept #keyword, [keyword], and semver:keyword anywhere in the text.
    private static readonly string LinePrefix =
        @"(?m)^(?:\s*(?:[-*•]|\d+[.)])?\s*)(?:Merged PR \d+:\s*)?";

    private static readonly Regex MajorPattern = new(
        @"semver:\s*major|\#major\b|\[major\]|" + LinePrefix + @"major(?:\s|:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MinorPattern = new(
        @"semver:\s*minor|\#minor\b|\[minor\]|" + LinePrefix + @"minor(?:\s|:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PatchPattern = new(
        @"semver:\s*patch|\#patch\b|\[patch\]|" + LinePrefix + @"(?:fix|patch)(?:\s|:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Resolve(IEnumerable<string> commitMessages, string fallback = "patch")
    {
        var normalizedFallback = NormalizeBump(fallback);
        var highest = 0; // 0=none, 1=patch, 2=minor, 3=major

        foreach (var message in commitMessages)
        {
            // Evaluate the whole message and each line so squash bodies with listed
            // original subjects still contribute (e.g. "- minor: add feature").
            highest = Math.Max(highest, BumpRank(DetectInMessage(message)));
            foreach (var line in SplitMeaningfulLines(message))
            {
                highest = Math.Max(highest, BumpRank(DetectInMessage(line)));
            }
        }

        return highest switch
        {
            3 => "major",
            2 => "minor",
            1 => "patch",
            _ => normalizedFallback
        };
    }

    public static string DetectInMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "";
        }

        if (MajorPattern.IsMatch(message))
        {
            return "major";
        }

        if (MinorPattern.IsMatch(message))
        {
            return "minor";
        }

        if (PatchPattern.IsMatch(message))
        {
            return "patch";
        }

        return "";
    }

    private static IEnumerable<string> SplitMeaningfulLines(string message)
    {
        foreach (var raw in Regex.Split(message, @"\r?\n"))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // Strip a leading bullet if present so " - minor: ..." still matches prefixes.
            line = Regex.Replace(line, @"^(?:[-*•]|\d+[.)])\s+", "");
            yield return line;
        }
    }

    private static int BumpRank(string bump) => bump switch
    {
        "major" => 3,
        "minor" => 2,
        "patch" => 1,
        _ => 0
    };

    private static string NormalizeBump(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "patch" or "minor" or "major" ? normalized : "patch";
    }
}
