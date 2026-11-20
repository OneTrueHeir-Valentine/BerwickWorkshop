using Berwick.Core;
using System.Buffers.Binary;

internal static class ChangeReviewTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Manifest compares copied fields with the original disc, and includes project labels", () =>
        {
            var session = new EditSession(Fixture());
            session.CreateItem(2, 1, "Training sword");
            var rows = ChangeManifest.Build(session);
            var might = rows.Single(r => r.Table == EditSession.Items && r.Id == 2 && r.Key == "might");
            Check(might.Original == "0" && might.Current == "10", "Copied field uses the wrong baseline");
            Check(rows.Any(r => r.Kind == "Copy" && r.Id == 2), "Copy missing");
            Check(rows.Any(r => r.Kind == "Name" && r.File == "Project only" && r.Current == "Training sword"), "Name missing");
            session.Undo();
            Check(ChangeManifest.Build(session).Count == 0, "Undo left stale changes");
            session.Redo();
            var reopened = EditSession.FromJson(session.Disc, session.ToJson());
            Check(ChangeManifest.Build(reopened).SequenceEqual(ChangeManifest.Build(session)), "Reload differs");
        });
        test("Manifest preserves display scaling and rule setting names", () =>
        {
            var session = new EditSession(Fixture());
            session.Apply(EditSession.Characters, 1, new Dictionary<string, int> { ["swordLevel"] = 125 });
            session.Apply(EditSession.Rules, 1, new Dictionary<string, int> { [GameRules.ById(1).Field.Key] = 1 });
            var rows = ChangeManifest.Build(session);
            Check(rows.Single(r => r.Key == "swordLevel").Current == "12.5", "Weapon level scaling lost");
            Check(rows.Count(r => r.Table == EditSession.Rules) == 1, "Rule repeated for unrelated fields");
        });
        test("Dependency review reports shared classes and combined skill effects", () =>
        {
            var session = new EditSession(Fixture());
            session.Apply(EditSession.Classes, 1, new Dictionary<string, int> { ["strength"] = 8 });
            int robust = Array.IndexOf(CharacterDecoder.SkillBits, "Robust");
            int vulnerable = Array.IndexOf(CharacterDecoder.SkillBits, "Vulnerable");
            session.Apply(EditSession.Classes, 1, new Dictionary<string, int> { [$"skill{robust}"] = 1 });
            session.Apply(EditSession.Characters, 1, new Dictionary<string, int> { [$"skill{vulnerable}"] = 1 });
            var issues = DependencyChecks.Analyze(session);
            Check(issues.Any(i => i.Code == "shared-class" && i.Message.Contains("army", StringComparison.OrdinalIgnoreCase)), "Army dependency omitted");
            Check(issues.Any(i => i.Code == "skill-interaction"), "Combined personal and class skills missed");
        });
        test("Patch checks reject nested overlaps, incomplete expectations and overflow", () =>
        {
            Check(PatchChecks.Analyze([new(10, new byte[20]), new(12, new byte[1]), new(25, new byte[1])], 100).Count(i => i.Code == "patch-overlap") == 2, "Nested overlaps missed");
            Check(PatchChecks.Analyze([new(long.MaxValue - 1, new byte[5])], long.MaxValue).Any(i => i.Code == "patch-bounds"), "Offset overflow accepted");
            Check(PatchChecks.Analyze([new(0, [1, 2], [0])], 100).Any(i => i.Code == "patch-original"), "Incomplete original accepted");
            Check(PatchChecks.Analyze([new(0, [1], [0]), new(1, [2], [0])], 2).Count == 0, "Adjacent patches rejected");
        });
        test("Review keeps project-only changes separate from disc patches", () =>
        {
            var session = new EditSession(Fixture());
            session.Rename(EditSession.Items, 1, "Workshop label");
            var report = ProjectReview.Build(session);
            Check(report.Changes.Count == 1 && report.Patches.Count == 0 && report.CanExport, "Name-only project review failed");
            Check(report.Issues.Count == 0, "A project label triggered game dependencies");
        });
        test("Equipment review follows edited inventory and class permissions", () =>
        {
            var session = new EditSession(Fixture());
            session.Apply(EditSession.Characters, 1, new Dictionary<string, int> { ["item1"] = 1 });
            Check(!DependencyChecks.Analyze(session).Any(i => i.Code == "equipment-permission"), "Usable sword flagged");
            session.Apply(EditSession.Classes, 1, new Dictionary<string, int> { ["swordCap"] = 0 });
            Check(DependencyChecks.Analyze(session).Any(i => i.Code == "equipment-permission"), "Unusable sword missed");
            session.Undo();
            Check(!DependencyChecks.Analyze(session).Any(i => i.Code == "equipment-permission"), "Undo left a warning");
        });
        test("Export rejects overflowing patch offsets before creating a file", () =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "berwick-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string source = Path.Combine(folder, "source.bin"), destination = Path.Combine(folder, "result.bin");
            try
            {
                File.WriteAllBytes(source, [1, 2, 3]);
                bool rejected = false;
                try { Exporter.Export(source, "unused", destination, [new(long.MaxValue - 1, new byte[5])]); }
                catch (EditException) { rejected = true; }
                Check(rejected && !File.Exists(destination), "Invalid export created a file");
            }
            finally { Directory.Delete(folder, true); }
        });
    }

    public static void RunDiscs(Action<string, Action> test, InspectionResult jp, InspectionResult en)
    {
        foreach (var disc in new[] { jp, en })
        {
            test($"{disc.Edition}: item review includes active bags and recipes", () =>
            {
                var session = new EditSession(disc);
                int bag = session.Table(EditSession.Bags).Records.Keys.First(id => session.Value(EditSession.Bags, id, "bagCount") > 0);
                int bagItem = session.Value(EditSession.Bags, bag, BagData.ItemKey(0));
                int recipe = session.Table(EditSession.Recipes).Records.Keys.First();
                foreach (int item in new[] { bagItem, recipe }.Distinct())
                    session.Apply(EditSession.Items, item, new Dictionary<string, int> { ["might"] = (session.Value(EditSession.Items, item, "might") + 1) % 64 });
                var issues = DependencyChecks.Analyze(session);
                Check(issues.Any(i => i.Code == "shared-item" && i.Id == bagItem && i.Message.Contains("Starting bag:")), "Active bag omitted");
                Check(issues.Any(i => i.Code == "shared-item" && i.Id == recipe && i.Message.Contains("Atelier recipe:")), "Recipe output omitted");
            });
            test($"{disc.Edition}: loaded shop script replacement blocks export review", () =>
            {
                var session = new EditSession(disc);
                byte[] bytes = session.OriginalEventScript(602)!;
                var json = System.Text.Json.Nodes.JsonNode.Parse(session.ToJson())!;
                json["version"] = 3;
                json["scripts"] = System.Text.Json.JsonSerializer.SerializeToNode(new[] { new {
                    @event = 602, originalSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), script = Convert.ToBase64String(bytes) } });
                var loaded = EditSession.FromJson(disc, json.ToJsonString());
                var report = ProjectReview.Build(loaded);
                Check(!report.CanExport && report.Issues.Any(i => i.Code == "shop-script-replacement"), "Loaded replacement accepted");
            });
            test($"{disc.Edition}: manifest covers records, scripts, texts and their export files", () =>
            {
                var session = new EditSession(disc);
                session.Apply(EditSession.Items, 34, new Dictionary<string, int> { ["might"] = 8 });
                session.Apply(EditSession.Classes, 1, new Dictionary<string, int> { ["strength"] = (session.Value(EditSession.Classes, 1, "strength") + 1) % 32 });
                var originalScript = session.OriginalEventScript(3276)!;
                var number = ScriptValues.Numbers(3276, ScriptParser.Parse(originalScript)).First();
                session.SetScript(3276, ScriptValues.Apply(originalScript, new Dictionary<int, int> { [number.Index] = number.Value + 1 }));
                int text = Enumerable.Range(0, session.TextList.Count).First(i => session.FontOfText(i) is not null && session.OriginalText(i).Length > 1 && !session.OriginalText(i).StartsWith('<'));
                string oldText = session.OriginalText(text);
                session.SetText(text, oldText + " ");
                var report = ProjectReview.Build(session);
                Check(report.CanExport, string.Join("; ", report.Issues.Where(i => i.Severity == "Error").Select(i => i.Message)));
                var scriptRow = report.Changes.Single(c => c.Kind == "Script");
                Check(scriptRow.Original == ScriptText.Format(ScriptParser.Parse(originalScript)) && scriptRow.Current != scriptRow.Original, "Script baseline lost");
                Check(report.Changes.Single(c => c.Kind == "Text").Original == oldText, "Text baseline lost");
                Check(report.Files.Count > 0 && report.Files.All(f => f.Original.Length == f.Current.Length && f.Original != f.Current), "Incomplete file manifest");
                Check(report.Files.Any(f => f.File == "DATA3.DAT"), "Data archive omitted");
                Check(ChangeManifest.Build(EditSession.FromJson(disc, session.ToJson())).SequenceEqual(report.Changes), "Reload changed manifest");
                while (session.CanUndo) session.Undo();
                var empty = ProjectReview.Build(session);
                Check(empty.Changes.Count == 0 && empty.Patches.Count == 0 && empty.Issues.Count == 0, "Undo did not clear review");
            });
            test($"{disc.Edition}: conflicting shop and event prices block review", () =>
            {
                var session = new EditSession(disc);
                var slot = StablePrices.Slots[0];
                var nodes = ScriptParser.Parse(session.OriginalEventScript(602)!);
                IEnumerable<ScriptNode> Numbers(IEnumerable<ScriptNode> ns) => ns.SelectMany(n => n.Command == ScriptParser.Number ? new[] { n } : Numbers(n.Args.SelectMany(a => a)));
                int index = Numbers(nodes).Select((n, i) => (n, i)).Single(p => p.n.Offset + 8 == slot.Offset).i;
                int before = session.Value(EditSession.StablePrices, slot.Item, StablePrices.Field.Key);
                session.Apply(EditSession.StablePrices, slot.Item, new Dictionary<string, int> { [StablePrices.Field.Key] = before + 100 });
                session.Apply(EditSession.ScriptValues, ScriptValues.Key(602, index), new Dictionary<string, int> { ["value"] = before + 200 });
                var conflict = ProjectReview.Build(session);
                Check(!conflict.CanExport && conflict.Issues.Any(i => i.Code == "script-price-conflict"), "Conflicting price accepted");
                session.Apply(EditSession.ScriptValues, ScriptValues.Key(602, index), new Dictionary<string, int> { ["value"] = before + 100 });
                Check(ProjectReview.Build(session).CanExport, "Matching price rejected");
            });
        }
    }

    internal static InspectionResult Fixture()
    {
        var sword = new byte[56];
        FieldCodec.Write(sword, ItemEncoder.Fields.Single(f => f.Key == "might"), 10);
        sword[0] |= 1;
        var empty = new byte[56];
        var unit = new byte[256]; unit[19] = 1; unit[20] = 5;
        var growth = new byte[32]; growth[2] = 1 << 5;
        Array.Fill(growth, (byte)255, 26, 6);
        var cls = new byte[100];
        FieldCodec.Write(cls, ClassFields.Fields.Single(f => f.Key == "swordCap"), 40);
        var profile = new GameProfile("Test edition", "hash", 0, 1024)
        { UnitTable = 2048, SecondUnitTable = 4096, ClassTable = 8192, SecondClassTable = 16384 };
        return new("source.iso", 8 * 1024 * 1024, "", "", "", "hash", "", profile,
            [new("DATA3.DAT", 0, 4 * 1024 * 1024), new("SLPS_254.97", 4 * 1024 * 1024, 4 * 1024 * 1024)],
            [ItemDecoder.Decode(1, "Sword", sword), ItemDecoder.Decode(2, "DUMMY", empty)], 0)
        {
            ItemBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = sword, [2] = empty },
            UnitBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = unit },
            GrowthBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = growth },
            ClassBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = cls },
            Characters = [CharacterDecoder.Decode(new(1, "Unit", 0), unit, growth, new Dictionary<int, string>())],
            Classes = [ClassDecoder.Decode(1, "Soldier", cls)],
            CharacterOffsets = new Dictionary<int, int> { [1] = 0 },
            ArmyUnitBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1000] = unit.ToArray() },
            Armies = [new(1, "Test army", [100000], 256, [new(1000, 1, 0, 1, "Enemy", 0, 180)])],
            RuleWords = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = GameRules.ById(1).EncodeBytes(GameRules.ById(1).Default) },
            ExecutableFile = new("SLPS_254.97", 4 * 1024 * 1024, 4 * 1024 * 1024)
        };
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
