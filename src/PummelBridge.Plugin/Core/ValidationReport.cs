using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PummelBridge.Core
{
    /// <summary>One ModIssueInfo as the editor's validator produced it.</summary>
    public sealed class ValidationIssue
    {
        /// <summary>ModIssueSeverity name: Error, Warning, Information.</summary>
        public string Severity;
        public string Title;
        public string Description;
        public string HelpLink;
        public bool Fixable;
    }

    /// <summary>JSON for validate_mod and the start_playtest refusal (pure; unit-tested).</summary>
    public static class ValidationReport
    {
        public static string SeverityName(string gameSeverity)
        {
            switch (gameSeverity)
            {
                case "Error": return "error";
                case "Warning": return "warning";
                case "Information": return "info";
                default: return gameSeverity?.ToLowerInvariant() ?? "unknown";
            }
        }

        /// <summary>Issues in the popup's order (errors, warnings, info; each in the validator's order).</summary>
        public static JArray Issues(IEnumerable<ValidationIssue> issues)
        {
            JArray arr = new JArray();
            foreach (ValidationIssue i in Ordered(issues))
            {
                JObject j = new JObject
                {
                    ["severity"] = SeverityName(i.Severity),
                    ["message"] = i.Title ?? "",
                };
                if (!string.IsNullOrEmpty(i.Description) && i.Description != i.Title)
                {
                    j["description"] = i.Description;
                }
                if (!string.IsNullOrEmpty(i.HelpLink))
                {
                    j["helpLink"] = i.HelpLink;
                }
                j["fixable"] = i.Fixable;
                arr.Add(j);
            }
            return arr;
        }

        public static List<ValidationIssue> Ordered(IEnumerable<ValidationIssue> issues)
        {
            List<ValidationIssue> list = issues?.Where(i => i != null).ToList() ?? new List<ValidationIssue>();
            return list.Where(i => i.Severity == "Error")
                .Concat(list.Where(i => i.Severity == "Warning"))
                .Concat(list.Where(i => i.Severity != "Error" && i.Severity != "Warning"))
                .ToList();
        }

        public static int Count(IEnumerable<ValidationIssue> issues, string severity) => issues?.Count(i => i != null && i.Severity == severity) ?? 0;

        /// <summary>"2 validator errors: A; B" (first <paramref name="max"/> titles).</summary>
        public static string ErrorSummary(IEnumerable<ValidationIssue> issues, int max = 5)
        {
            List<string> titles = (issues ?? Enumerable.Empty<ValidationIssue>()).Where(i => i != null && i.Severity == "Error").Select(i => i.Title).ToList();
            if (titles.Count == 0)
            {
                return "no validator errors";
            }
            string head = titles.Count == 1 ? "1 validator error" : $"{titles.Count} validator errors";
            string list = string.Join("; ", titles.Take(max).ToArray());
            return head + ": " + list + (titles.Count > max ? $"; ... (+{titles.Count - max} more)" : "");
        }

        /// <summary>Counts + issues block shared by validate_mod and start_playtest.</summary>
        public static JObject Summary(IList<ValidationIssue> issues)
        {
            int errors = Count(issues, "Error");
            return new JObject
            {
                ["valid"] = errors == 0,
                ["errorCount"] = errors,
                ["warningCount"] = Count(issues, "Warning"),
                ["infoCount"] = Count(issues, "Information"),
                ["issues"] = Issues(issues),
            };
        }
    }
}
