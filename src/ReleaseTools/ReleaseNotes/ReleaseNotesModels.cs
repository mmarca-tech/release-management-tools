using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

sealed record ReleaseNotesResult(int ExitCode, IReadOnlyList<string> OutputLines);


sealed record TagCandidate(string TagName, string Commit, long CommitEpoch);

