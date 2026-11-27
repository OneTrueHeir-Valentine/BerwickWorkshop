using Berwick.Core;

/// <summary>Exercise class and army recipes against a verified original, then compare the exported records byte for byte.</summary>
public static class BulkArmyRealCheck
{
    public static int Run(string iso, string folder)
    {
        var disc = Inspector.Inspect(iso);
        if (!disc.IsVerifiedOriginal) { Console.WriteLine("Expected a verified original disc."); return 2; }
        var session = new EditSession(disc);
        var units = disc.ArmyUnitBytes.Keys.Order().Where(id =>
            disc.ClassBytes.ContainsKey(session.Value(EditSession.ArmyUnits, id, "class")) &&
            session.Value(EditSession.ArmyUnits, id, "level") > 0 &&
            session.Value(EditSession.ArmyUnits, id, "hpBonus") < 127).Take(2).ToArray();
        if (units.Length != 2) throw new InvalidDataException("Could not find two army records for the test.");
        var classes = units.Select(id => session.Value(EditSession.ArmyUnits, id, "class")).Distinct().ToArray();
        var original = new EditSession(disc);
        void Apply(string table, int[] ids, string field, decimal amount)
        {
            var recipe = new BulkRecipe(BulkRecipe.CurrentVersion, "Export test", table,
                new BulkSelection(ids, [], [], []), [new BulkStep(field, BulkOperation.Add, amount)]);
            var preview = BulkEdit.Preview(session, recipe);
            foreach (string warning in preview.Warnings) Console.WriteLine(warning);
            if (!BulkEdit.Apply(session, preview)) throw new InvalidDataException("Test recipe made no changes.");
            session.Undo();
            if (ids.Any(id => session.Value(table, id, field) != original.Value(table, id, field)))
                throw new InvalidDataException("Undo did not restore the selected field.");
            session.Redo();
            if (ids.Any(id => session.Value(table, id, field) != original.Value(table, id, field) + amount))
                throw new InvalidDataException("Redo did not restore the recipe change.");
        }
        // Test a bounded one-point adjustment in each class, in either direction.
        foreach (int id in classes)
            Apply(EditSession.Classes, [id], "hp", session.Value(EditSession.Classes, id, "hp") == 31 ? -1 : 1);
        Apply(EditSession.ArmyUnits, units, "hpBonus", 1);
        string output = Path.Combine(folder, $"bulk-army-{Guid.NewGuid():N}.iso");
        var result = Exporter.Export(iso, disc.Sha256, output, Exporter.PatchesFor(session, disc));
        try
        {
            var exportedDisc = Inspector.Inspect(output);
            var exported = new EditSession(exportedDisc);
            bool ok = exportedDisc.IsModified && exportedDisc.DifferentTableRecords == disc.DifferentTableRecords;
            foreach (string table in new[] { EditSession.Classes, EditSession.ArmyUnits, EditSession.Characters, EditSession.Items })
            {
                var ids = session.Table(table).Records.Keys;
                if (!ids.ToHashSet().SetEquals(exported.Table(table).Records.Keys)) ok = false;
                foreach (int id in ids)
                    if (!exported.Table(table).Records.ContainsKey(id) ||
                        !session.EditedBytes(table, id).SequenceEqual(exported.EditedBytes(table, id)))
                    { Console.WriteLine($"Unexpected record: {table} {id}"); ok = false; }
            }
            Console.WriteLine($"{disc.Edition}: {classes.Length} classes, {units.Length} army records; {result.PatchCount} patches; {result.ChangedBytes} changed bytes.");
            Console.WriteLine(ok ? "Class and army export passed" : "Class and army export FAILED");
            return ok ? 0 : 1;
        }
        finally { File.Delete(output); }
    }
}
