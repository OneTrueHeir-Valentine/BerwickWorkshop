namespace Berwick.Core;

public sealed record ChangeEntry(string Table, int Id, string Record, string Key, string Field,
    string Original, string Current, string File, string Kind, ChangeEvidence? Evidence = null);

public sealed record ChangeManifestResult(
    IReadOnlyList<ChangeEntry> Changes, IReadOnlyList<ReviewIssue> Warnings);

public static class ChangeManifest
{
    public static IReadOnlyList<ChangeEntry> Build(EditSession session) => Inspect(session).Changes;

    public static ChangeManifestResult Inspect(EditSession session)
    {
        var rows = new List<ChangeEntry>();
        var warnings = new List<ReviewIssue>();
        // Promotion edits may live in individual script numbers, not whole-event overrides.
        // Emit semantic labels once per affected event regardless of the storage form.
        var touchedEvents = session.EditedRecords
            .Where(r => r.Table is EditSession.Scripts or EditSession.ScriptValues)
            .Select(r => r.Table == EditSession.Scripts ? r.Id : ScriptValues.Split(r.Id).Event)
            .Distinct().ToArray();
        foreach (int eventNumber in touchedEvents)
        {
            var analysis = PromotionChanges.Analyze(session, eventNumber);
            rows.AddRange(analysis.Changes);
            warnings.AddRange(analysis.Warnings);
        }
        foreach (var (table, id) in session.EditedRecords)
        {
            string name = session.RecordName(table, id) ?? $"{table} {id}";
            void Add(string key, string field, string before, string after, string file, string kind) =>
                rows.Add(new(table, id, name, key, field, before, after, file, kind));
            if (table == EditSession.Scripts)
            {
                Add("script", "Event script", ScriptText.Format(ScriptParser.Parse(session.OriginalEventScript(id)!)),
                    ScriptText.Format(ScriptParser.Parse(session.EventScript(id)!)), "DATA3.DAT", "Script");
                continue;
            }
            if (table == EditSession.Texts)
            {
                Add("text", "Text", session.OriginalText(id), session.Text(id), session.TextList[id].Archive.Path, "Text");
                continue;
            }
            string file = FileFor(session, table, id);
            if (session.CopiedFrom(table, id) is { } source)
                Add("copy", $"Copied from {session.RecordName(table, source) ?? source.ToString()} ({source})",
                    Convert.ToHexString(session.Table(table).Records[id].Span), Convert.ToHexString(session.EditedBytes(table, id)), file, "Copy");
            if (session.CustomName(table, id) is { } label)
                Add("name", "Project name", session.RecordName(table, id, original: true) ?? $"{table} {id}", label, "Project only", "Name");
            IEnumerable<TableField> fields = table == EditSession.Rules ? [GameRules.ById(id).Field] : session.Table(table).Fields;
            foreach (var field in fields)
            {
                int before = session.OriginalValue(table, id, field.Key), after = session.Value(table, id, field.Key);
                if (before == after) continue;
                string Format(int value) => table == EditSession.Rules ? GameRules.ById(id).Describe(value) : FieldCodec.Format(field, value);
                Add(field.Key, field.Group.Length > 0 ? $"{field.Group}: {field.Label}" : field.Label, Format(before), Format(after), file,
                    table == EditSession.Rules ? "Rule" : "Field");
            }
        }
        return new(rows.Select(row => row with { Evidence = session.EvidenceFor(row) }).ToList().AsReadOnly(),
            warnings.AsReadOnly());
    }

    private static string FileFor(EditSession session, string table, int id) => table switch
    {
        EditSession.Rules or EditSession.Mercenaries or EditSession.Recipes or EditSession.Dishes or EditSession.Tastes or EditSession.MapSprites =>
            session.Disc.ExecutableFile?.Name ?? "Game program",
        _ => "DATA3.DAT"
    };
}
