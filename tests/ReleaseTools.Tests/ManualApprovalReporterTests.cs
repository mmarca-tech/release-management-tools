using System.Text.Json.Nodes;
using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

[Collection("Release notes Git environment")]
public sealed class ManualApprovalReporterTests
{
    private const string ApprovalId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Timeline_scopes_manual_validation_to_creation_job_and_stage_attempt()
    {
        var timeline = Timeline();
        timeline["records"]!.AsArray().Add(JsonNode.Parse(
            """{"id":"other-task","parentId":"other-job","type":"Task","attempt":1,"state":"completed","result":"succeeded","identifier":"22222222-2222-2222-2222-222222222222","task":{"name":"ManualValidation"}}"""));

        Assert.Equal(ApprovalId, ManualApprovalReporter.ResolveApprovalId(timeline, 1));
        Assert.Throws<CliException>(() => ManualApprovalReporter.ResolveApprovalId(timeline, 2));
    }

    [Fact]
    public void Timeline_uses_latest_job_attempt_not_an_earlier_success()
    {
        var timeline = Timeline();
        timeline["records"]!.AsArray().Add(JsonNode.Parse(
            """{"id":"retry-phase","parentId":"stage","type":"Phase","identifier":"release_process_create_ticket.approve_release_ticket","attempt":2,"result":"failed"}"""));

        Assert.Throws<CliException>(() => ManualApprovalReporter.ResolveApprovalId(timeline, 1));
    }

    [Fact]
    public void Timeline_successful_retry_uses_its_own_approval()
    {
        var timeline = Timeline();
        var records = timeline["records"]!.AsArray();
        foreach (var original in records.Skip(1).ToArray())
        {
            var retry = original!.DeepClone();
            retry["attempt"] = 2;
            retry["id"] = "retry-" + retry["id"]!.GetValue<string>();
            if (retry["parentId"]!.GetValue<string>() != "stage")
            {
                retry["parentId"] = "retry-" + retry["parentId"]!.GetValue<string>();
            }
            if (retry["type"]!.GetValue<string>() == "Task")
            {
                retry["identifier"] = "22222222-2222-2222-2222-222222222222";
            }
            records.Add(retry);
        }

        Assert.Equal("22222222-2222-2222-2222-222222222222",
            ManualApprovalReporter.ResolveApprovalId(timeline, 1));
    }

    [Fact]
    public void Duplicate_manual_validations_are_not_guessed()
    {
        var timeline = Timeline();
        timeline["records"]!.AsArray().Add(timeline["records"]![3]!.DeepClone());

        Assert.Throws<CliException>(() => ManualApprovalReporter.ResolveApprovalId(timeline, 1));
    }

    [Fact]
    public void Approval_with_no_actual_human_is_rejected()
    {
        var approvals = Approvals();
        approvals["value"]![0]!["steps"]![0]!["actualApprover"] = null;

        Assert.Throws<CliException>(() => ManualApprovalReporter.ResolveApproverEmail(approvals, ApprovalId));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    public void Unapproved_records_are_rejected(string status)
    {
        var approvals = Approvals();
        approvals["value"]![0]!["status"] = status;

        Assert.Throws<CliException>(() => ManualApprovalReporter.ResolveApproverEmail(approvals, ApprovalId));
    }

    [Theory]
    [InlineData("failed", "completed")]
    [InlineData("succeeded", "inProgress")]
    public void Incomplete_or_failed_manual_validation_is_rejected(string result, string state)
    {
        var timeline = Timeline();
        timeline["records"]![3]!["result"] = result;
        timeline["records"]![3]!["state"] = state;

        Assert.Throws<CliException>(() => ManualApprovalReporter.ResolveApprovalId(timeline, 1));
    }

    [Fact]
    public void Actual_approver_is_used_not_assigned_approver()
    {
        var approvals = Approvals();
        approvals["value"]![0]!["steps"]![0]!["assignedApprover"] =
            JsonNode.Parse("""{"uniqueName":"assigned@example.invalid"}""");

        Assert.Equal("human@example.invalid", ManualApprovalReporter.ResolveApproverEmail(approvals, ApprovalId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Human Name")]
    public void Missing_or_non_email_actual_identity_is_rejected(string email)
    {
        var approvals = Approvals();
        approvals["value"]![0]!["steps"]![0]!["actualApprover"]!["uniqueName"] = email;

        Assert.Throws<CliException>(() => ManualApprovalReporter.ResolveApproverEmail(approvals, ApprovalId));
    }

    [Fact]
    public void Multiple_actual_approvers_are_rejected()
    {
        var approvals = Approvals();
        approvals["value"]![0]!["steps"]!.AsArray().Add(JsonNode.Parse(
            """{"status":"approved","actualApprover":{"uniqueName":"second@example.invalid"}}"""));

        Assert.Throws<CliException>(() => ManualApprovalReporter.ResolveApproverEmail(approvals, ApprovalId));
    }

    [Fact]
    public void Jira_match_requires_exact_case_insensitive_email_and_active_account()
    {
        var users = JsonNode.Parse("""
            [
              {"active":true,"accountId":"partial","emailAddress":"other-human@example.invalid"},
              {"active":false,"accountId":"inactive","emailAddress":"human@example.invalid"},
              {"active":true,"accountId":"matched","emailAddress":"HUMAN@example.invalid"}
            ]
            """)!;

        Assert.Equal("matched", ManualApprovalReporter.ResolveJiraAccount(users, "human@example.invalid"));
    }

    [Fact]
    public void Jira_match_accepts_single_active_human_with_hidden_email()
    {
        var users = JsonNode.Parse("""
            [
              {"active":true,"accountId":"other","accountType":"atlassian","emailAddress":"human@example.invalid.other"},
              {"active":false,"accountId":"inactive","accountType":"atlassian"},
              {"active":true,"accountId":"bot","accountType":"app"},
              {"active":true,"accountId":"hidden","accountType":"atlassian"}
            ]
            """)!;

        Assert.Equal("hidden", ManualApprovalReporter.ResolveJiraAccount(users, "human@example.invalid"));
    }

    [Fact]
    public void Visible_email_match_wins_over_hidden_email_accounts()
    {
        var users = JsonNode.Parse("""
            [
              {"active":true,"accountId":"hidden"},
              {"active":true,"accountId":"matched","emailAddress":"human@example.invalid"}
            ]
            """)!;

        Assert.Equal("matched", ManualApprovalReporter.ResolveJiraAccount(users, "human@example.invalid"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"active":true,"accountId":"one"},{"active":true,"accountId":"two"}]""")]
    [InlineData("""[{"active":true,"accountId":"bot","accountType":"app"}]""")]
    [InlineData("""[{"active":true,"accountId":"other","emailAddress":"other-human@example.invalid"}]""")]
    [InlineData("""[{"active":false,"accountId":"inactive","emailAddress":"human@example.invalid"}]""")]
    [InlineData("""[{"active":true,"accountId":"one","emailAddress":"human@example.invalid"},{"active":true,"accountId":"two","emailAddress":"human@example.invalid"}]""")]
    public void Missing_mismatched_inactive_non_human_or_ambiguous_Jira_match_fails(string json)
    {
        Assert.Throws<CliException>(() =>
            ManualApprovalReporter.ResolveJiraAccount(JsonNode.Parse(json)!, "human@example.invalid"));
    }

    [Fact]
    public async Task Resolution_queries_exact_approval_and_Jira_without_forwarding_Ado_credentials()
    {
        var previousToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
        try
        {
            Environment.SetEnvironmentVariable("SYSTEM_ACCESSTOKEN", "test-ado-token");
            var calls = new List<string>();
            Task<HttpJsonResult> Get(string url, IReadOnlyDictionary<string, string> headers)
            {
                calls.Add(url);
                if (url.Contains("/timeline?"))
                {
                    Assert.Equal("Bearer test-ado-token", headers["Authorization"]);
                    return Task.FromResult(new HttpJsonResult(true, Timeline(), null, null));
                }
                if (url.Contains("/approvals?"))
                {
                    Assert.Contains($"approvalIds={ApprovalId}&$expand=steps", url);
                    return Task.FromResult(new HttpJsonResult(true, Approvals(), null, null));
                }

                Assert.StartsWith("https://api.atlassian.com/ex/jira/cloud/rest/api/3/user/assignable/search?project=ITSM&", url);
                Assert.Contains("query=human%40example.invalid", url);
                Assert.StartsWith("Basic ", headers["Authorization"]);
                return Task.FromResult(new HttpJsonResult(true,
                    JsonNode.Parse("""[{"active":true,"accountId":"jira-human","emailAddress":"human@example.invalid"}]"""), null, null));
            }

            var result = await ManualApprovalReporter.ResolveAsync(
                "https://dev.azure.com/example/project", "42", 1, "cloud", "bot", "jira-token", "ITSM", Get);

            Assert.Equal("jira-human", result);
            Assert.Equal(3, calls.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYSTEM_ACCESSTOKEN", previousToken);
        }
    }

    [Fact]
    public async Task Api_failure_blocks_resolution_without_fallback()
    {
        var previousToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
        try
        {
            Environment.SetEnvironmentVariable("SYSTEM_ACCESSTOKEN", "test-ado-token");

            var error = await Assert.ThrowsAsync<CliException>(() => ManualApprovalReporter.ResolveAsync(
                "https://dev.azure.com/example/project", "42", 1, "cloud", "bot", "token", "ITSM",
                (_, _) => Task.FromResult(new HttpJsonResult(false, null, "private response body", 403))));

            Assert.Contains("HTTP 403", error.Message);
            Assert.DoesNotContain("private response body", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYSTEM_ACCESSTOKEN", previousToken);
        }
    }

    private static JsonNode Timeline() => JsonNode.Parse($$$"""
        {"records":[
          {"id":"stage","type":"Stage","identifier":"release_process_create_ticket","attempt":1},
          {"id":"phase","parentId":"stage","type":"Phase","identifier":"release_process_create_ticket.approve_release_ticket","attempt":1,"result":"succeeded"},
          {"id":"job","parentId":"phase","type":"Job","identifier":"release_process_create_ticket.approve_release_ticket.__default","attempt":1,"result":"succeeded"},
          {"id":"task","parentId":"job","type":"Task","identifier":"{{{ApprovalId}}}","attempt":1,"state":"completed","result":"succeeded","task":{"name":"ManualValidation"}}
        ]}
        """)!;

    private static JsonNode Approvals() => JsonNode.Parse($$$"""
        {"value":[{"id":"{{{ApprovalId}}}","status":"approved","steps":[
          {"status":"approved","actualApprover":{"uniqueName":"human@example.invalid"}}
        ]}]}
        """)!;
}
