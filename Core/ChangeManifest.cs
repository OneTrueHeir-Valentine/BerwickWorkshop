namespace Berwick.Core;

public sealed record ChangeEntry(string Table, int Id, string Record, string Key, string Field,
    string Original, string Current, string File, string Kind);

public static class ChangeManifest
{
    public static IReadOnlyList<ChangeEntry> Build(EditSession session)
    {
        var rows = new List<ChangeEntry>();
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
        return rows.AsReadOnly();
    }

    private static string FileFor(EditSession session, string table, int id) => table switch
    {
        EditSession.Rules or EditSession.Mercenaries or EditSession.Recipes or EditSession.Dishes or EditSession.Tastes or EditSession.MapSprites =>
            session.Disc.ExecutableFile?.Name ?? "Game program",
        _ => "DATA3.DAT"
    };
}
