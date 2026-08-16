using System.Text.RegularExpressions;

namespace Pfandwerk;

public sealed record LintFinding(string Rule, string Check, string Message);

public sealed record LintDocument(string Verdict, List<LintFinding> Findings);

/// <summary>
/// The offline self-check an author (human or agent) runs before opening a rules MR
/// (D-B3). No database: rule schema, ScriptDom AST of gap and invariant, fix-key
/// existence (datasets included), threshold sanity against the surveyed counts, and the
/// D-C2 test conventions. Reports every finding, not the first — an author iterates on
/// the whole list, not one error per push.
/// </summary>
public static class Linter
{
    public static LintDocument Run(string rulesPath, string? surveyPath, string? testsDir)
    {
        var findings = new List<LintFinding>();

        var datasets = DatasetStore.Empty;
        try { datasets = DatasetStore.ForRulesFile(rulesPath); }
        catch (RulesLoadException ex) { findings.Add(new LintFinding("", "datasets", ex.Message)); }

        GapRulesFile? file = null;
        try { file = RulesLoader.Deserialize<GapRulesFile>(RulesLoader.ReadFile(rulesPath)); }
        catch (RulesLoadException ex) { findings.Add(new LintFinding("", "schema", ex.Message)); }

        if (file is not null)
        {
            findings.AddRange(GapRulesLoader.Lint(file, datasets)
                .Select(m => new LintFinding(RuleIdOf(m), "schema", m)));

            var survey = LoadSurvey(surveyPath);
            foreach (var rule in file.Rules)
            {
                if (survey is not null) CheckThreshold(rule, survey, findings);
                CheckTestConventions(rule, testsDir, findings);
            }
        }

        return new LintDocument(findings.Count == 0 ? "pass" : "fail", findings);
    }

    private static string RuleIdOf(string message) =>
        Regex.Match(message, "^Rule '([^']+)'") is { Success: true } m ? m.Groups[1].Value : "";

    private static SurveyDocument? LoadSurvey(string? path) =>
        path is not null && File.Exists(path) ? Json.Read<SurveyDocument>(path) : null;

    /// <summary>
    /// A rule whose gap already matches more rows than its threshold will be BLOCKED at
    /// the gate. The survey cannot run the predicate, but a NULL-shaped gap is countable
    /// from the per-column NULL counts it already carries.
    /// </summary>
    private static void CheckThreshold(GapRule rule, SurveyDocument survey, List<LintFinding> findings)
    {
        var table = survey.Tables.FirstOrDefault(t =>
            string.Equals(Unbracket(t.Table), Unbracket(rule.Table), StringComparison.OrdinalIgnoreCase));
        var column = table?.Columns.FirstOrDefault(c =>
            string.Equals(c.Name, rule.Column, StringComparison.OrdinalIgnoreCase));
        if (column is null) return;

        var gap = Unbracket(rule.Gap);
        if (!gap.Contains($"{rule.Column} IS NULL", StringComparison.OrdinalIgnoreCase)) return;

        if (column.Nulls > rule.Threshold)
            findings.Add(new LintFinding(rule.Id, "threshold",
                $"Rule '{rule.Id}': threshold {rule.Threshold} is below the surveyed " +
                $"{column.Nulls} NULL rows of {rule.Table}.{rule.Column} — the gate would BLOCK this rule."));
    }

    private static string Unbracket(string sql) => sql.Replace("[", "").Replace("]", "");

    /// <summary>
    /// D-C2 for derived code generators: a boundary-value [Theory] and a refuse-NULL test
    /// must exist, and the rule must ship an independently written SQL invariant. Dataset
    /// fixes carry no logic, so no tests are demanded of them.
    /// </summary>
    private static void CheckTestConventions(GapRule rule, string? testsDir, List<LintFinding> findings)
    {
        RuleKind kind;
        try { kind = rule.ParsedKind; }
        catch (RulesLoadException) { return; }   // already a schema finding

        if (kind != RuleKind.Derived || DatasetStore.IsDatasetKey(rule.Fix)) return;

        if (string.IsNullOrWhiteSpace(rule.Invariant))
            findings.Add(new LintFinding(rule.Id, "invariant",
                $"Rule '{rule.Id}': derived rule ships no SQL invariant. Generator and invariant " +
                "are written independently so VERIFY can ring when they disagree (D-C2)."));

        if (testsDir is null || !Directory.Exists(testsDir))
        {
            findings.Add(new LintFinding(rule.Id, "tests",
                $"Rule '{rule.Id}': test directory '{testsDir}' not found, so the D-C2 test " +
                "conventions cannot be checked."));
            return;
        }

        var mentioning = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .Where(text => text.Contains(rule.Fix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mentioning.Count == 0)
        {
            findings.Add(new LintFinding(rule.Id, "tests",
                $"Rule '{rule.Id}': no test mentions generator '{rule.Fix}'. Ship a boundary-value " +
                "theory and a refuse-NULL test (D-C2)."));
            return;
        }

        var all = string.Join("\n", mentioning);
        if (!all.Contains("[Theory]"))
            findings.Add(new LintFinding(rule.Id, "tests",
                $"Rule '{rule.Id}': tests mention '{rule.Fix}' but no [Theory] covers its " +
                "boundary values (D-C2)."));
        if (!Regex.IsMatch(all, @"(?i)refuse\w*null|null\w*refuse"))
            findings.Add(new LintFinding(rule.Id, "tests",
                $"Rule '{rule.Id}': tests mention '{rule.Fix}' but none refuses a NULL input — " +
                "name it so the refusal is visible, e.g. Refuses_a_null_input (D-C2)."));
    }
}
