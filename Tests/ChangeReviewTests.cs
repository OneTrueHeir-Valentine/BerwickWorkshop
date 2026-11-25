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
        test("Experimental evidence survives project save/reload and manifest without silent promotion", () =>
        {
            var session = new EditSession(Fixture());
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["might"] = 11 });
            var changed = ChangeManifest.Build(session).Single(r => r.Table == EditSession.Items && r.Key == "might");
            Check(changed.Evidence is { Interpretation: EvidenceLevel.SourceObserved,
                Binding: EvidenceBinding.Bound, Gameplay: EvidenceAcceptance.Untested },
                "A source edit was falsely described as gameplay-accepted.");
            Check(session.ToJson().Contains("\"formatVersion\": 1") && !session.ToJson().Contains("\"evidence\""),
                "Derived evidence unnecessarily upgraded the project format.");
            var restored = EditSession.FromJson(session.Disc, session.ToJson());
            var after = ChangeManifest.Build(restored).Single(r => r.Table == EditSession.Items && r.Key == "might");
            Check(changed.Evidence == after.Evidence && after.Evidence?.Gameplay == EvidenceAcceptance.Untested,
                "Saved project upgraded or discarded its experimental evidence.");
            Check(ProjectReview.Build(restored).Issues.Any(i => i.Code == "research-provenance"),
                "Export review omitted the experimental status warning.");
            session.Undo();
            Check(ChangeManifest.Build(session).Count == 0, "Undo retained evidence on a reverted field.");
            session.Redo();
            Check(ChangeManifest.Build(session).Single(x => x.Key == "might").Evidence?.Experimental == true,
                "Redo erased experimental status.");
        });
        test("Imported evidence rejects unsupported claims with property-specific errors", () =>
        {
            var session = new EditSession(Fixture());
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["might"] = 11 });
            // The current serializer omits reconstructible evidence. Build an actual
            // legacy-v5 fixture with canonical source-generated evidence before forging it.
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(session.ToJson())!;
            legacy["formatVersion"] = 5;
            legacy["evidence"] = System.Text.Json.JsonSerializer.SerializeToNode(
                ChangeManifest.Build(session).Select(row => new ProjectEvidence(row.Table, row.Id, row.Key, row.Evidence!)).ToArray(),
                new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            string json = legacy.ToJsonString();
            var original = EditSession.FromJson(session.Disc, json);
            Check(ChangeManifest.Build(original).Single(x => x.Key == "might").Evidence?.Gameplay
                == EvidenceAcceptance.Untested, "Unmodified project evidence failed to load.");

            string? LoadError(System.Text.Json.Nodes.JsonNode root)
            {
                try { _ = EditSession.FromJson(session.Disc, root.ToJsonString()); return null; }
                catch (EditException ex) { return ex.Message; }
            }

            void Reject(Action<System.Text.Json.Nodes.JsonObject> change, string property)
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                var entry = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)root["evidence"]!)[0]!;
                var status = (System.Text.Json.Nodes.JsonObject)entry["status"]!;
                change(status);
                var error = LoadError(root);
                Check(error is not null && error.Contains("items", StringComparison.OrdinalIgnoreCase)
                    && error.Contains("might", StringComparison.OrdinalIgnoreCase)
                    && error.Contains("unsupported evidence", StringComparison.OrdinalIgnoreCase)
                    && error.Contains($"'{property}'", StringComparison.Ordinal),
                    "Forged evidence was accepted or its exact property was not identified.");
            }

            Reject(status => { status["interpretation"] = (int)EvidenceLevel.Verified; }, "interpretation");
            Reject(status => { status["binding"] = (int)EvidenceBinding.Unbound; }, "binding");
            Reject(status => { status["gameplay"] = (int)EvidenceAcceptance.BothObserved; }, "gameplay");
            Reject(status => { status["basis"] = "Gameplay verified by this project file"; }, "basis");

            var unknown = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            ((System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)unknown["evidence"]!)[0]!)["key"] = "not-an-edited-field";
            Check(LoadError(unknown)?.Contains("unknown change", StringComparison.OrdinalIgnoreCase) == true,
                "Unknown evidence entry was accepted.");

            var duplicate = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var entries = (System.Text.Json.Nodes.JsonArray)duplicate["evidence"]!;
            entries.Add(entries[0]!.DeepClone());
            Check(LoadError(duplicate)?.Contains("twice", StringComparison.OrdinalIgnoreCase) == true,
                "Duplicate evidence entry was accepted.");

            var invalid = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            ((System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)invalid["evidence"]!)[0]!)["status"]!)["interpretation"] = 999;
            Check(LoadError(invalid)?.Contains("'interpretation'", StringComparison.OrdinalIgnoreCase) == true,
                "Invalid enum was not identified by property.");
        });
        test("Research notes use format 5 without granting verification authority", () =>
        {
            var session = new EditSession(Fixture());
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["might"] = 11 });
            Check(session.ToJson().Contains("\"formatVersion\": 1"), "An ordinary edit must use format 1.");
            const string note = "Compare this value in an actual gameplay test.\nNot verified.";
            session.SetResearchNote(EditSession.Items, 1, "might", note);
            string json = session.ToJson();
            Check(json.Contains("\"formatVersion\": 5") && json.Contains("\"notes\"")
                && !json.Contains("\"evidence\""), "Only irreconstructible notes should require format 5.");
            var loaded = EditSession.FromJson(session.Disc, json);
            Check(loaded.ResearchNotes.Single().Text == note, "Research note was lost or normalized.");
            Check(ChangeManifest.Build(loaded).Single(x => x.Key == "might").Evidence?.Gameplay == EvidenceAcceptance.Untested,
                "Research note granted game acceptance.");
            session.Undo();
            Check(session.ResearchNotes.Single().Text == note && session.ToJson().Contains("\"formatVersion\": 5"),
                "Undoing the field silently discarded its research note.");
            session.Redo();
            session.SetResearchNote(EditSession.Items, 1, "might", null);
            Check(session.ResearchNotes.Count == 0 && session.ToJson().Contains("\"formatVersion\": 1"),
                "Removing the note did not restore the oldest compatible format.");

            string Error(string candidate)
            {
                try { _ = EditSession.FromJson(session.Disc, candidate); return ""; }
                catch (EditException e) { return e.Message; }
            }
            var invalidVersion = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            invalidVersion["formatVersion"] = 4;
            Check(Error(invalidVersion.ToJsonString()).Contains("format older", StringComparison.OrdinalIgnoreCase),
                "Older format accepted nonreconstructible notes.");
            var duplicate = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var notes = (System.Text.Json.Nodes.JsonArray)duplicate["notes"]!;
            notes.Add(notes[0]!.DeepClone());
            Check(Error(duplicate.ToJsonString()).Contains("duplicate", StringComparison.OrdinalIgnoreCase),
                "Duplicate research note accepted.");
            var unknown = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            ((System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)unknown["notes"]!)[0]!)["key"] = "invalid-field";
            Check(Error(unknown.ToJsonString()).Contains("invalid-field", StringComparison.OrdinalIgnoreCase),
                "Unknown field note accepted.");
            var oversized = new EditSession(Fixture());
            oversized.SetResearchNote(EditSession.Items, 1, "might", new string('n', 4096));
            bool rejected = false;
            try { oversized.SetResearchNote(EditSession.Items, 1, "might", new string('n', 4097)); }
            catch (EditException) { rejected = true; }
            Check(rejected, "Unbounded research text accepted.");
        });
        test("Research notes mark a saved project as modified without changing gameplay undo", () =>
        {
            var session = new EditSession(Fixture());
            Check(!session.IsDirty && !session.CanUndo, "New project should be clean.");
            session.SetResearchNote(EditSession.Items, 1, "might", "First observation");
            Check(session.IsDirty && !session.CanUndo, "Adding research did not mark project modified, or created gameplay undo.");
            string path = Path.Combine(Path.GetTempPath(), $"berwick-research-{Guid.NewGuid():N}.bwproj");
            try
            {
                session.Save(path);
                Check(!session.IsDirty, "Saving research did not clear dirty status.");
                session.SetResearchNote(EditSession.Items, 1, "might", "First observation");
                Check(!session.IsDirty, "Unchanged research text incorrectly marked project modified.");
                session.SetResearchNote(EditSession.Items, 1, "might", "Updated observation");
                Check(session.IsDirty && !session.CanUndo, "Changing research did not mark project modified independently.");
                var reopened = EditSession.FromJson(session.Disc, session.ToJson());
                Check(reopened.ResearchNotes.Single().Text == "Updated observation", "Updated research did not round trip.");
                session.SetResearchNote(EditSession.Items, 1, "might", null);
                Check(session.IsDirty && session.ResearchNotes.Count == 0, "Removing research did not mark project modified.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        test("Canonical legacy version 5 evidence reopens and saves in the lowest compatible format", () =>
        {
            var session = new EditSession(Fixture());
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["might"] = 11 });
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(session.ToJson())!;
            legacy["formatVersion"] = 5;
            legacy["evidence"] = System.Text.Json.JsonSerializer.SerializeToNode(
                ChangeManifest.Build(session).Select(row => new ProjectEvidence(row.Table, row.Id, row.Key, row.Evidence!)).ToArray(),
                new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            var evidence = (System.Text.Json.Nodes.JsonArray)legacy["evidence"]!;
            var entry = (System.Text.Json.Nodes.JsonObject)evidence[0]!;
            Check(entry.ContainsKey("table") && entry.ContainsKey("status")
                && ((System.Text.Json.Nodes.JsonObject)entry["status"]!).ContainsKey("interpretation"),
                "Legacy fixture does not use the project JSON property names.");
            var reopened = EditSession.FromJson(session.Disc, legacy.ToJsonString());
            Check(reopened.ToJson().Contains("\"formatVersion\": 1") && !reopened.ToJson().Contains("\"evidence\""),
                "Canonical legacy evidence prevented a lossless format downgrade.");
            Check(ChangeManifest.Build(reopened).Single(x => x.Key == "might").Evidence?.Gameplay == EvidenceAcceptance.Untested,
                "Legacy evidence gained gameplay authority.");
        });
        test("Verified-only filter hides source observations without deleting them", () =>
        {
            var levels = new[] { EvidenceLevel.SourceObserved, EvidenceLevel.Verified, EvidenceLevel.Unresolved };
            Check(EvidenceVisibility.Filter(levels, x => x, false).SequenceEqual(levels),
                "Default filter hid unverified entries.");
            Check(EvidenceVisibility.Filter(levels, x => x, true).SequenceEqual(new[] { EvidenceLevel.Verified }),
                "Verified-only filter leaked a non-verified record.");
        });
        test("Legacy projects load as unverified, not automatically upgraded", () =>
        {
            var session = new EditSession(Fixture());
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["might"] = 11 });
            var data = System.Text.Json.Nodes.JsonNode.Parse(session.ToJson())!;
            data["formatVersion"] = 4;
            ((System.Text.Json.Nodes.JsonObject)data).Remove("evidence");
            var imported = EditSession.FromJson(session.Disc, data.ToJsonString());
            Check(ChangeManifest.Build(imported).Single(x => x.Key == "might").Evidence?.Experimental == true,
                "Old project was implicitly marked verified.");
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
