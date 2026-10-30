using System.Text.Json.Nodes;
using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

public class ApprovalGateTests
{
    [Theory]
    [InlineData("Standard", "standard")]
    [InlineData("Standard - Routine change", "standard")]
    [InlineData("Emergency", "emergency")]
    [InlineData("Normal", "normal")]
    [InlineData("Normal - Controlled change with approval", "normal")]
    [InlineData("Major", "major")]
    [InlineData("Major - requires Bank CAB", "major")]
    public void ResolveChangeTypeInfo_maps_known_values(string raw, string expectedCategory)
    {
        var node = JsonNode.Parse($$"""{"value":"{{raw}}"}""");
        var info = ApprovalGate.ResolveChangeTypeInfo(node);
        Assert.Equal(expectedCategory, info.Category);
        Assert.Equal(raw, info.RawValue);
    }

    [Fact]
    public void ResolveChangeTypeInfo_unknown_returns_null_category()
    {
        var node = JsonNode.Parse("""{"value":"Unknown"}""");
        var info = ApprovalGate.ResolveChangeTypeInfo(node);
        Assert.Null(info.Category);
    }

    [Fact]
    public void Standard_and_Emergency_do_not_require_approvals_for_gate_logic()
    {
        Assert.False(RequiresApproval("standard"));
        Assert.False(RequiresApproval("emergency"));
        Assert.True(RequiresApproval("normal"));
        Assert.True(RequiresApproval("major"));
    }

    [Fact]
    public void GetApprovalSummary_missing_approvers_has_no_approved_decision()
    {
        var summary = ApprovalGate.GetApprovalSummary(null);
        Assert.Equal(0, summary.ApproverCount);
        Assert.False(summary.HasApprovedDecision);
    }

    [Fact]
    public void GetApprovalSummary_with_approved_approver_passes()
    {
        var node = JsonNode.Parse("""
        {
          "values": [
            {
              "approvers": [
                { "approverDecision": "approved" }
              ]
            }
          ]
        }
        """);

        var summary = ApprovalGate.GetApprovalSummary(node);
        Assert.Equal(1, summary.ApproverCount);
        Assert.Equal(1, summary.ApprovedApproverCount);
        Assert.True(summary.HasApprovedDecision);
    }

    [Fact]
    public void GetApprovalSummary_with_final_decision_approved_passes()
    {
        var node = JsonNode.Parse("""
        {
          "values": [
            {
              "approvers": [
                { "approverDecision": "pending" }
              ],
              "finalDecision": "approve"
            }
          ]
        }
        """);

        var summary = ApprovalGate.GetApprovalSummary(node);
        Assert.True(summary.HasApprovedDecision);
        Assert.Equal(1, summary.ApprovedDecisionCount);
    }

    [Fact]
    public void GetApprovalSummary_pending_only_does_not_pass()
    {
        var node = JsonNode.Parse("""
        {
          "values": [
            {
              "approvers": [
                { "approverDecision": "pending" },
                { "approverDecision": "rejected" }
              ]
            }
          ]
        }
        """);

        var summary = ApprovalGate.GetApprovalSummary(node);
        Assert.Equal(2, summary.ApproverCount);
        Assert.False(summary.HasApprovedDecision);
    }

    private static bool RequiresApproval(string category) =>
        category is "normal" or "major";
}
