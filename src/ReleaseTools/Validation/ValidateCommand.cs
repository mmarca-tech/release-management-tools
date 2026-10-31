using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class ValidateCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var repoRoot = Path.GetFullPath(options.Get("RepoRoot", Directory.GetCurrentDirectory())!);
        var validationErrors = new List<string>();

        void AddError(string message)
        {
            validationErrors.Add(message);
            Console.WriteLine($"ERROR: {message}");
        }

        Console.WriteLine($"Validating repository: {repoRoot}");

        var yamlFiles = Directory.GetFiles(repoRoot, "*.yml")
            .Concat(Directory.GetFiles(repoRoot, "*.yaml"))
            .Concat(Directory.Exists(Path.Combine(repoRoot, "templates")) ? Directory.GetFiles(Path.Combine(repoRoot, "templates"), "*.yml") : [])
            .Concat(Directory.Exists(Path.Combine(repoRoot, "templates")) ? Directory.GetFiles(Path.Combine(repoRoot, "templates"), "*.yaml") : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"Checking YAML script references in {yamlFiles.Length} YAML file(s)...");
        foreach (var yamlFile in yamlFiles)
        {
            var content = await File.ReadAllTextAsync(yamlFile);
            if (Regex.IsMatch(content, @"scripts/[^'""\s)]+\.ps1", RegexOptions.IgnoreCase))
            {
                AddError($"PowerShell script reference found in '{yamlFile}'.");
            }

            foreach (Match match in Regex.Matches(content, @"scripts/(?<relativePath>[^'""\s)]+\.cs)", RegexOptions.IgnoreCase))
            {
                var relativePath = match.Groups["relativePath"].Value.Replace('/', Path.DirectorySeparatorChar);
                var scriptPath = Path.Combine(repoRoot, "scripts", relativePath);
                if (!File.Exists(scriptPath))
                {
                    AddError($"Missing script reference in '{yamlFile}' - scripts/{match.Groups["relativePath"].Value}");
                }
            }
        }

        var textFiles = Directory.GetFiles(repoRoot, "*.md")
            .Concat(Directory.Exists(Path.Combine(repoRoot, "docs")) ? Directory.GetFiles(Path.Combine(repoRoot, "docs"), "*.md") : [])
            .Concat(yamlFiles)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine("Checking for merge conflict markers...");
        foreach (var file in textFiles)
        {
            var lineNumber = 0;
            foreach (var line in await File.ReadAllLinesAsync(file))
            {
                lineNumber++;
                if (Regex.IsMatch(line, @"^(<<<<<<<|=======|>>>>>>>)"))
                {
                    AddError($"Merge conflict marker found in '{file}:{lineNumber}' - {line.Trim()}");
                }
            }
        }

        if (validationErrors.Count > 0)
        {
            Console.Error.WriteLine($"Validation failed with {validationErrors.Count} issue(s).");
            return 1;
        }

        Console.WriteLine("Validation completed successfully.");
        return 0;
    }
}
