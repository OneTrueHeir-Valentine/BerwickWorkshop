using Berwick.Core;

/// <summary>Horse HP in starting character and army unit records: the field layout, its checks, and a real-disc round trip.</summary>
public static class HorseHpTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run(Action<string, Action> test)
    {
        test("Horse HP fields match the running game's layout and are offered for characters and army units", () =>
        {
            var hp = CharacterFields.Record.Single(f => f.Key == "horseHp");
            var most = CharacterFields.Record.Single(f => f.Key == "horseMaxHp");
            Check((hp.Offset, hp.Bit, hp.Width) == (LiveHorse.Hp.Offset, LiveHorse.Hp.Bit, LiveHorse.Hp.Width), "Horse HP is not where the game keeps it.");
            Check((most.Offset, most.Bit, most.Width) == (LiveHorse.MaxHp.Offset, LiveHorse.MaxHp.Bit, LiveHorse.MaxHp.Width), "Horse max HP is not where the game keeps it.");
            Check(ArmyData.Fields.Any(f => f.Key == "horseHp") && ArmyData.Fields.Any(f => f.Key == "horseMaxHp"), "Army units do not offer horse HP.");
            // The fields must not share a bit with any other field of the record.
            var bits = new Dictionary<int, string>();
            foreach (var f in CharacterFields.Record)
                for (int i = 0; i < f.Width; i++)
                {
                    int at = f.Offset * 8 + f.Bit + i;
                    Check(!bits.TryGetValue(at, out var other) || (other != "horseHp" && other != "horseMaxHp" && f.Key is not ("horseHp" or "horseMaxHp")),
                        $"{f.Key} overlaps {other}.");
                    bits.TryAdd(at, f.Key);
                }
            var record = new byte[256];
            FieldCodec.Write(record, hp, 120); FieldCodec.Write(record, most, 120);
            Check(Convert.ToHexString(record, 0x1C, 3) == "78E001", "120/120 is not stored as the discs store it (78 E0 01).");
        });
        test("The Characters and Armies editors show every field group of their records, including Horse", () =>
        {
            string code = File.ReadAllText(AccessKeyTests.SourceFile("App", "MainWindow.xaml.cs"));
            string Section(string start, string end) { int i = code.IndexOf(start, StringComparison.Ordinal); return code[i..code.IndexOf(end, i, StringComparison.Ordinal)]; }
            string characters = Section("private void BeginCharacterEdit", "],");
            string armies = Section("OpenEditor(new(View.Armies", "],");
            foreach (string group in CharacterFields.Record.Select(f => f.Group).Where(g => g.Length > 0).Distinct())
                Check(characters.Contains($"f.Group==\"{group}\"", StringComparison.Ordinal), $"The Characters editor leaves out the {group} fields.");
            // Army units have their own item and drop groups and no equipped slots or item flags.
            foreach (string group in ArmyData.Fields.Select(f => f.Group).Where(g => g.Length > 0 && !g.Contains("(experimental)")).Distinct())
                Check(armies.Contains($"f.Group==\"{group}\"", StringComparison.Ordinal), $"The Armies editor leaves out the {group} fields.");
        });
    }

    /// <summary>
    /// Real disc: reads Reese's horse, gives an on-foot army unit a mounted class (the review warns about its horse), sets horse HP,
    /// refuses HP above the most, exports, and reads the values back from the new image. Usage: --horse-hp &lt;iso&gt; &lt;empty folder&gt;
    /// </summary>
    public static int Real(string iso, string folder)
    {
        var disc = Inspector.Inspect(iso);
        if (!disc.IsVerifiedOriginal) { Console.WriteLine($"{iso} is not a verified original disc."); return 2; }
        bool ok = true;
        void Expect(bool condition, string what) { Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}"); ok &= condition; }
        var session = new EditSession(disc);
        int reese = disc.Characters.First(c => c.Name == "Reese").Id;
        Expect(session.Value(EditSession.Characters, reese, "horseHp") == 120 && session.Value(EditSession.Characters, reese, "horseMaxHp") == 120,
            "Reese's horse reads 120 of 120");
        var mounted = disc.Classes.First(c => c.Mount == 1 && disc.ArmyUnitBytes.Values.Any(b => b.Span[19] == c.Id));
        int unit = disc.ArmyUnitBytes.Keys.Order().First(k => disc.Classes.FirstOrDefault(c => c.Id == disc.ArmyUnitBytes[k].Span[19]) is { Mount: 0 }
            && session.Value(EditSession.ArmyUnits, k, "horseMaxHp") == 0);
        string name = session.RecordName(EditSession.ArmyUnits, unit) ?? unit.ToString();
        session.Apply(EditSession.ArmyUnits, unit, new Dictionary<string, int> { ["class"] = mounted.Id });
        Expect(DependencyChecks.Analyze(session).Any(i => i.Code == "mounted-without-horse-hp" && i.Id == unit),
            $"{name} given {mounted.Name}: the review warns that its horse has no HP");
        bool refused;
        try { refused = !session.Apply(EditSession.ArmyUnits, unit, new Dictionary<string, int> { ["horseHp"] = 90, ["horseMaxHp"] = 80 }); }
        catch (EditException) { refused = true; }
        Expect(refused && session.Value(EditSession.ArmyUnits, unit, "horseMaxHp") == 0, "horse HP above the max HP is refused");
        session.Apply(EditSession.ArmyUnits, unit, new Dictionary<string, int> { ["horseHp"] = 80, ["horseMaxHp"] = 80 });
        Expect(!DependencyChecks.Analyze(session).Any(i => i.Code == "mounted-without-horse-hp"), "with horse HP set, the warning is gone");
        session.Apply(EditSession.Characters, reese, new Dictionary<string, int> { ["horseHp"] = 90, ["horseMaxHp"] = 150 });
        var before = session.EditedBytes(EditSession.ArmyUnits, unit).ToArray();
        string output = Path.Combine(folder, $"horse-hp-{(disc.Edition.Contains("English") ? "EN" : "JP")}.iso");
        var result = Exporter.Export(iso, disc.Sha256, output, Exporter.PatchesFor(session, disc));
        try
        {
            var exported = new EditSession(Inspector.Inspect(output));
            Expect(exported.Value(EditSession.ArmyUnits, unit, "class") == mounted.Id && exported.Value(EditSession.ArmyUnits, unit, "horseHp") == 80 &&
                   exported.Value(EditSession.ArmyUnits, unit, "horseMaxHp") == 80, $"the exported disc has {name} mounted with 80 of 80 horse HP");
            Expect(exported.Value(EditSession.Characters, reese, "horseHp") == 90 && exported.Value(EditSession.Characters, reese, "horseMaxHp") == 150,
                "the exported disc has Reese's horse at 90 of 150");
            Expect(exported.EditedBytes(EditSession.ArmyUnits, unit).SequenceEqual(before), "the rest of the unit's record is unchanged");
            Console.WriteLine($"  export {result.Sha256}, {result.PatchCount} patches");
        }
        finally { File.Delete(output); }
        Console.WriteLine(ok ? "horse HP check passed" : "horse HP check FAILED");
        return ok ? 0 : 1;
    }
}
