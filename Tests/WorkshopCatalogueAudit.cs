using Berwick.Core;

internal static class WorkshopCatalogueAudit
{
    private static string Cell(object? value) => (value?.ToString() ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    private static string Row(params object?[] values) => string.Join("\t", values.Select(Cell));
    internal static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var mechanics = new List<string> { Row("Section", "Mechanic", "Original formula", "Evidence", "Source", "Native rule links", "Data-only links", "Explicit outstanding work", "Description") };
        foreach (var m in Mechanics.All)
            mechanics.Add(Row(m.Section, m.Name, m.Formula, m.Status, m.Source,
                string.Join("; ", m.Changes.Where(l => l.RuleId.HasValue).Select(l => { var r = GameRules.ById(l.RuleId!.Value); return $"{r.Id}:{r.Key} ({(r.Confirmed ? "confirmed" : r.EditorVisible ? "experimental" : "not exposed")})"; })),
                string.Join("; ", m.Changes.Where(l => !l.RuleId.HasValue).Select(l => $"{l.Label} ({l.Page})")), m.NotYetEditable, m.Explanation));
        var skills = new List<string> { Row("Skill", "Kind", "Evidence", "Confirmed controls", "Experimental controls", "Hidden bindings", "Known grant sources", "Description", "Review status") };
        foreach (var s in SkillCatalog.All)
        {
            var rules = GameRules.All.Where(r => r.Section == GameRules.SkillsSection && r.Group == s.Name).ToArray();
            string ids(IEnumerable<GameRule> r) => string.Join("; ", r.Select(x => $"{x.Id}:{x.Key}"));
            skills.Add(Row(s.Name, s.Kind, s.Source, ids(rules.Where(r => r.Confirmed)), ids(rules.Where(r => r.ExperimentalEditor)),
                ids(rules.Where(r => !r.EditorVisible)), string.Join("; ", SkillGrantBindings.All.Where(g => g.Skill == s.Name).Select(g => $"{g.Source}:{g.Code}")),
                s.Summary + " " + s.Details, rules.Length == 0 ? "No bound controls; assess behavior, not automatically a missing formula" : "Compare every described term against rule list; not full parity"));
        }
        var ruleRows = new List<string> { Row("ID", "Key", "Group", "Section", "Default", "Minimum", "Maximum", "Evidence", "Sites") };
        foreach (var r in GameRules.All)
            ruleRows.Add(Row(r.Id, r.Key, r.Group, r.Section, r.Default, r.Min, r.Max, r.Confirmed ? "Confirmed" : r.EditorVisible ? "Experimental" : "Hidden",
                string.Join("; ", r.Sites.Select((site, i) => $"{site.Address:X8}:{r.Original[i]:X8}"))));
        File.WriteAllLines(Path.Combine(folder, "mechanics-catalogue.tsv"), mechanics);
        File.WriteAllLines(Path.Combine(folder, "skills-catalogue.tsv"), skills);
        File.WriteAllLines(Path.Combine(folder, "rules-catalogue.tsv"), ruleRows);
        Console.WriteLine($"Catalogue audit: {mechanics.Count - 1} mechanics; {skills.Count - 1} skills; {ruleRows.Count - 1} rules. Coverage inventory, not gameplay acceptance.");
        return 0;
    }
}
