using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class UpdateStakeholdersCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var jiraTicketKey = options.Get("JiraTicketKey");
        var jiraTicketUrl = options.Get("JiraTicketUrl");
        var plannedStartDate = options.Get("PlannedStartDate");
        var plannedEndDate = options.Get("PlannedEndDate");
        var releaseTag = options.Get("ReleaseTag") ?? options.Get("PackageVersion");
        var webhookUrl = options.Require("WebhookUrl");
        var action = options.Get("action") ?? options.Get("Action") ?? "";
        var componentName = options.Get("ComponentName");

        Console.WriteLine($"Sending Teams notification for ticket: {jiraTicketKey}");
        Console.WriteLine($"Planned Start: {plannedStartDate}");
        Console.WriteLine($"Planned End: {plannedEndDate}");

        var body = new JsonObject
        {
            ["type"] = "Application/JSON",
            ["attachments"] = new JsonArray
            {
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["content"] = new JsonObject
                    {
                        ["type"] = "AdaptiveCard",
                        ["body"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "ColumnSet",
                                ["columns"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["type"] = "Column",
                                        ["items"] = new JsonArray
                                        {
                                            new JsonObject
                                            {
                                                ["type"] = "Image",
                                                ["style"] = "Person",
                                                ["url"] = "https://adaptivecards.io/content/cats/3.png",
                                                ["altText"] = "CAT",
                                                ["size"] = "small"
                                            }
                                        },
                                        ["width"] = "auto"
                                    },
                                    new JsonObject
                                    {
                                        ["type"] = "Column",
                                        ["items"] = new JsonArray
                                        {
                                            new JsonObject
                                            {
                                                ["type"] = "TextBlock",
                                                ["weight"] = "Bolder",
                                                ["text"] = $"A release has been {action}!",
                                                ["wrap"] = true
                                            },
                                            new JsonObject
                                            {
                                                ["type"] = "TextBlock",
                                                ["spacing"] = "None",
                                                ["text"] = $"[{jiraTicketKey}] {componentName} Release {releaseTag}",
                                                ["isSubtle"] = true,
                                                ["wrap"] = true
                                            }
                                        },
                                        ["width"] = "stretch"
                                    }
                                }
                            },
                            new JsonObject
                            {
                                ["type"] = "TextBlock",
                                ["text"] = $"Planned deployment of {componentName} has been {action}",
                                ["wrap"] = true
                            },
                            new JsonObject
                            {
                                ["type"] = "FactSet",
                                ["facts"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["title"] = "Planned start date:",
                                        ["value"] = plannedStartDate
                                    },
                                    new JsonObject
                                    {
                                        ["title"] = "Planned end date:",
                                        ["value"] = plannedEndDate
                                    }
                                }
                            }
                        },
                        ["actions"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "Action.OpenUrl",
                                ["title"] = "See Jira ticket",
                                ["url"] = jiraTicketUrl
                            }
                        },
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["version"] = "1.4"
                    }
                }
            }
        };

        using var client = new HttpClient();
        using var content = new StringContent(body.ToJsonString(JsonSupport.Compact), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(webhookUrl, content);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"Failed to send Teams notification: {(int)response.StatusCode} {response.ReasonPhrase} {responseBody}");
            return 1;
        }

        Console.WriteLine("Teams notification sent successfully!");
        return 0;
    }
}

