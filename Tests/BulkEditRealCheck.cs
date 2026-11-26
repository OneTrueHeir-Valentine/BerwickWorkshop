using Berwick.Core;

/// <summary>
/// Applies an example bow recipe to a real original disc, exports it, and reopens the export: every bow must have the new
/// range in both item tables, and every other item must be unchanged. The exported image is deleted afterwards.
/// Run with --bulk-real &lt;original iso&gt; &lt;empty folder&gt;. The recipe is a test, not a balance change.
/// </summary>
public static class BulkEditRealCheck
{
    public static int Run(string iso, string folder)
    {
        var disc = Inspector.Inspect(iso);
        if (!disc.IsVerifiedOriginal) { Console.WriteLine($"{iso} is not a verified original disc."); return 2; }
        var session = new EditSession(disc);
        var recipe = new BulkRecipe(BulkRecipe.CurrentVersion, "Example bow range", EditSession.Items,
            new BulkSelection([], [], [BulkGroups.Bows], []), [new BulkStep("minRange", BulkOperation.Set, 2), new BulkStep("maxRange", BulkOperation.Set, 3)]);
        var preview = BulkEdit.Preview(session, recipe);
        Console.WriteLine($"{disc.Edition}: {preview.ChangedRecords} bows change, {preview.BlockedRecords} blocked, {preview.Rows.Count(r => r.Status == BulkRowStatus.Unchanged)} fields already set");
        foreach (var row in preview.Rows) Console.WriteLine($"  {row.Id} {row.Record}: {row.FieldLabel} {row.Current} -> {row.Proposed} {row.Status} {row.Reason}");
        if (!BulkEdit.Apply(session, preview)) { Console.WriteLine("nothing changed"); return 1; }
        var original = new EditSession(disc);
        var bows = disc.Items.Where(i => original.Value(EditSession.Items, i.Id, "type") == 8 && !i.IsUnused && !BulkGroups.IsBrokenPlaceholder(original, i.Id) &&
                                         !BulkGroups.IsPersonal(original, i.Id)).Select(i => i.Id).ToHashSet();
        foreach (var s in preview.Skipped) Console.WriteLine($"  left out {s.Id} {s.Record}: {s.Reason}");
        bool personalKept = disc.Items.Where(i => BulkGroups.IsPersonal(original, i.Id) && original.Value(EditSession.Items, i.Id, "type") == 8)
            .All(i => preview.Skipped.Any(s => s.Id == i.Id) && preview.Edits.All(e => e.Id != i.Id));
        Console.WriteLine($"  {(personalKept ? "ok  " : "FAIL")} personal bows (Serenia's among them) are left out of the group and not changed");

        string output = Path.Combine(folder, $"bulk-{(disc.Edition.Contains("English") ? "EN" : "JP")}.iso");
        var result = Exporter.Export(iso, disc.Sha256, output, Exporter.PatchesFor(session, disc));
        try
        {
            var exported = Inspector.Inspect(output);
            bool ok = personalKept && exported.IsModified && exported.DifferentTableRecords == disc.DifferentTableRecords;
            foreach (var item in disc.Items)
            {
                var before = disc.Items.Single(i => i.Id == item.Id);
                var after = exported.Items.Single(i => i.Id == item.Id);
                bool expected = bows.Contains(item.Id)
                    ? after.MinRange == 2 && after.MaxRange == 3 && after with { MinRange = before.MinRange, MaxRange = before.MaxRange } == before
                    : after == before && exported.ItemBytes[item.Id].Span.SequenceEqual(disc.ItemBytes[item.Id].Span);
                if (!expected) { ok = false; Console.WriteLine($"  unexpected item {item.Id} {item.Name}"); }
            }
            Console.WriteLine($"export {result.Sha256} patches {result.PatchCount} changed bytes {result.ChangedBytes}; " +
                              $"table copies differing {disc.DifferentTableRecords} -> {exported.DifferentTableRecords}; bows {bows.Count}");
            Console.WriteLine(ok ? "bulk edit real disc check passed" : "bulk edit real disc check FAILED");
            return ok ? 0 : 1;
        }
        finally { File.Delete(output); }
    }
}
