using Berwick.Core;
using System.Text;

/// <summary>Bulk edit recipes on a small generated project: selection, groups, arithmetic, limits, previews, undo and saving.</summary>
public static class BulkEditTests
{
    private const int Bow = 8, Crossbow = 9, Spear = 4, Sword = 1;

    public static void Run(Action<string, Action> test)
    {
        test("Bulk edit sets the range of every bow, except a named one, as one undo step", () =>
        {
            var session = new EditSession(Fixture());
            var recipe = Recipe(EditSession.Items, [], [ItemDecoder.Types[Bow]], [3],
                new BulkStep("minRange", BulkOperation.Set, 2), new BulkStep("maxRange", BulkOperation.Set, 3));
            var preview = BulkEdit.Preview(session, recipe);
            Equal(2, preview.ChangedRecords);
            Equal("Excluded by the recipe.", preview.Skipped.Single(s => s.Id == 3).Reason);
            Equal("hexes", preview.Rows[0].Unit);
            Equal(true, BulkEdit.Apply(session, preview));
            Equal(3, session.Value(EditSession.Items, 1, "maxRange"));
            Equal(1, session.Value(EditSession.Items, 3, "minRange"));
            session.Undo();
            Equal(2, session.Value(EditSession.Items, 1, "maxRange"));
            session.Redo();
            Equal(3, session.Value(EditSession.Items, 2, "maxRange"));
        });

        test("One blocked record stops the whole recipe and changes nothing", () =>
        {
            var session = new EditSession(Fixture());
            var preview = BulkEdit.Preview(session, Recipe(EditSession.Items, [1, 2, 4], [], [], new BulkStep("minRange", BulkOperation.Set, 2)));
            Equal(1, preview.BlockedRecords);
            Equal(true, preview.Rows.Single(r => r.Id == 4).Reason.Contains("Minimum range cannot be greater"));
            Throws<EditException>(() => BulkEdit.Apply(session, preview));
            Equal(false, session.CanUndo);
            Equal(1, session.Value(EditSession.Items, 1, "minRange"));
        });

        test("Steps are worked out in order and only the final value must fit the field", () =>
        {
            var session = new EditSession(Fixture());
            var over = BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("might", BulkOperation.Add, 60)));
            Equal(true, over.Rows.Single().Reason.Contains("would be 68, outside 0 to 63"));
            var clamped = BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [],
                new BulkStep("might", BulkOperation.Add, 60), new BulkStep("might", BulkOperation.Clamp, 0, 63)));
            Equal("63", clamped.Rows.Single().Proposed);
            var back = BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [],
                new BulkStep("might", BulkOperation.Multiply, 10), new BulkStep("might", BulkOperation.Add, -70)));
            Equal("10", back.Rows.Single().Proposed);
            var negative = BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("might", BulkOperation.Add, -9)));
            Equal(BulkRowStatus.Blocked, negative.Rows.Single().Status);
        });

        test("Bulk edit rounds signed bonuses once, as the recipe says", () =>
        {
            var session = new EditSession(Fixture());
            Equal("-15", BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("hitBonus", BulkOperation.Multiply, 1.5m))).Rows.Single().Proposed);
            var recipe = Recipe(EditSession.Items, [1], [], [], new BulkStep("hitBonus", BulkOperation.Multiply, 1.25m));
            Equal("-13", BulkEdit.Preview(session, recipe).Rows.Single().Proposed);
            Equal("-13", BulkEdit.Preview(session, recipe with { Rounding = BulkRounding.Down }).Rows.Single().Proposed);
            Equal("-12", BulkEdit.Preview(session, recipe with { Rounding = BulkRounding.Up }).Rows.Single().Proposed);
            var twice = Recipe(EditSession.Items, [1], [], [], new BulkStep("hitBonus", BulkOperation.Multiply, 0.55m), new BulkStep("hitBonus", BulkOperation.Multiply, 1.1m));
            Equal("-6", BulkEdit.Preview(session, twice).Rows.Single().Proposed);
        });

        test("Bulk edit works in weapon levels and growth rates as shown and keeps an odd Mind growth that does not change", () =>
        {
            var session = new EditSession(Fixture());
            var levels = BulkEdit.Preview(session, Recipe(EditSession.Characters, [1, 2], [], [], new BulkStep("bowLevel", BulkOperation.Add, 2.5m)));
            Equal("4.5", levels.Rows.First(r => r.Id == 1).Proposed);
            Equal("weapon level", levels.Rows[0].Unit);
            Equal(true, BulkEdit.Apply(session, levels));
            Equal(45, session.Value(EditSession.Characters, 1, "bowLevel"));
            var fine = BulkEdit.Preview(session, Recipe(EditSession.Characters, [1], [], [], new BulkStep("bowLevel", BulkOperation.Add, 0.04m)));
            Equal(BulkRowStatus.Unchanged, fine.Rows.Single().Status);

            var mind = BulkEdit.Preview(session, Recipe(EditSession.Growths, [1, 2], [], [], new BulkStep(MindGrowth.Field, BulkOperation.Clamp, 0, 20)));
            Equal(BulkRowStatus.Unchanged, mind.Rows.First(r => r.Id == 1).Status);
            Equal(BulkRowStatus.Changed, mind.Rows.First(r => r.Id == 2).Status);
            Equal("%", mind.Rows[0].Unit);
            Equal(true, BulkEdit.Apply(session, mind));
            Equal(31, session.Value(EditSession.Growths, 1, MindGrowth.Field));
            Equal(40, session.Value(EditSession.Growths, 2, MindGrowth.Field));
            var add = BulkEdit.Preview(session, Recipe(EditSession.Growths, [1], [], [], new BulkStep(MindGrowth.Field, BulkOperation.Add, 1)));
            Equal(32, add.Edits.Single().Value);
            var bonus = BulkEdit.Preview(session, Recipe(EditSession.Characters, [1], [], [], new BulkStep("strengthBonus", BulkOperation.Add, -20)));
            Equal(true, bonus.Rows.Single().Reason.Contains("outside -16 to 15"));
            Equal("points above the class base", bonus.Rows.Single().Unit);
        });

        test("A preview is refused when the project resolves the recipe differently", () =>
        {
            var session = new EditSession(Fixture());
            var preview = BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("weight", BulkOperation.Add, 1)));
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["weight"] = 9 });
            Throws<EditException>(() => BulkEdit.Apply(session, preview));
            Equal(9, session.Value(EditSession.Items, 1, "weight"));

            // Changing the type of a record outside the preview adds it to the type selection.
            var bows = BulkEdit.Preview(session, Recipe(EditSession.Items, [], ["Bow"], [], new BulkStep("accuracy", BulkOperation.Add, 1)));
            session.Apply(EditSession.Items, 5, new Dictionary<string, int> { ["type"] = Bow });
            Throws<EditException>(() => BulkEdit.Apply(session, bows));
            Equal(70, session.Value(EditSession.Items, 1, "accuracy"));
        });

        test("A changed or rebuilt preview is refused", () =>
        {
            var session = new EditSession(Fixture());
            var recipe = Recipe(EditSession.Items, [1, 2], [], [], new BulkStep("might", BulkOperation.Add, 1), new BulkStep("might", BulkOperation.Multiply, 2));
            var preview = BulkEdit.Preview(session, recipe);
            Throws<EditException>(() => BulkEdit.Apply(session, preview with { Edits = [new FieldEdit(EditSession.Items, 1, "might", 40)] }));
            Throws<EditException>(() => BulkEdit.Apply(session, preview with { Edits = [.. preview.Edits, new FieldEdit(EditSession.Items, 4, "might", 30)] }));
            Throws<EditException>(() => BulkEdit.Apply(session, preview with { Recipe = recipe with { Steps = [.. recipe.Steps.Reverse()] } }));
            Throws<EditException>(() => BulkEdit.Apply(session, preview with { Recipe = recipe with { Selection = recipe.Selection with { Ids = [1] } } }));
            Throws<EditException>(() => BulkEdit.Apply(session, preview with { Rows = preview.Rows.Select(r => r with { Status = BulkRowStatus.Unchanged }).ToList() }));
            Equal(false, session.CanUndo);
            Equal(true, BulkEdit.Apply(session, preview));
            Equal(18, session.Value(EditSession.Items, 1, "might"));
        });

        test("Numbers too large to calculate block the record instead of failing", () =>
        {
            var session = new EditSession(Fixture());
            var huge = BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [],
                new BulkStep("price", BulkOperation.Multiply, 79_000_000_000_000m), new BulkStep("price", BulkOperation.Multiply, 79_000_000_000_000m)));
            Equal(BulkRowStatus.Blocked, huge.Rows.Single().Status);
            Equal(true, huge.Rows.Single().Reason.Contains("too large to calculate"));
            Throws<EditException>(() => BulkEdit.Apply(session, huge));
        });

        test("Saved recipes carry the field definitions they were made for", () =>
        {
            var recipe = Recipe(EditSession.Items, [1], [], [], new BulkStep("might", BulkOperation.Add, 1));
            var loaded = BulkEdit.Load(BulkEdit.Save(recipe));
            Equal(BulkEdit.Schema(EditSession.Items), loaded.Schema);
            Equal(16, loaded.Schema.Length);
            Equal(false, BulkEdit.Schema(EditSession.Items) == BulkEdit.Schema(EditSession.Growths));
            var session = new EditSession(Fixture());
            Equal(1, BulkEdit.Preview(session, loaded).Edits.Count);
            Throws<EditException>(() => BulkEdit.Preview(session, loaded with { Schema = "0123456789abcdef" }));
            var v1 = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(BulkEdit.Save(recipe)).Replace("\"Version\": 2", "\"Version\": 1"));
            Equal(true, Catch(() => BulkEdit.Load(v1)).Message.Contains("Make the recipe again"));
        });

        test("Imported recipes and selections reject unknown operations, rounding, fields and records", () =>
        {
            var session = new EditSession(Fixture());
            var recipe = Recipe(EditSession.Items, [1], [], [], new BulkStep("might", BulkOperation.Add, 1));
            Throws<EditException>(() => BulkEdit.ValidateRecipe(session, recipe with { Rounding = (BulkRounding)99 }));
            Throws<EditException>(() => BulkEdit.ValidateRecipe(session, recipe with { Steps = [new BulkStep("might", (BulkOperation)99, 1)] }));
            Throws<EditException>(() => BulkEdit.ValidateRecipe(session, recipe with { Table = null! }));
            Throws<EditException>(() => BulkEdit.ValidateSelection(session, null!, recipe.Selection));
            Throws<EditException>(() => BulkEdit.ValidateSelection(session, EditSession.Items, recipe.Selection with { Ids = [99] }));
            Throws<EditException>(() => BulkEdit.ValidateSelection(session, EditSession.Characters, recipe.Selection with { ItemTypes = ["Bow"] }));
            var saved = Encoding.UTF8.GetString(BulkEdit.Save(recipe));
            Throws<EditException>(() => BulkEdit.Load(Encoding.UTF8.GetBytes(saved.Replace(BulkEdit.Schema(recipe.Table), ""))));
            Equal(false, session.CanUndo);
        });

        test("Oversized durability choices and fractional weapon levels are blocked without arithmetic exceptions", () =>
        {
            var session = new EditSession(Fixture());
            var grade = BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("durability", BulkOperation.Set, decimal.MaxValue)));
            Equal(BulkRowStatus.Blocked, grade.Rows.Single().Status);
            var level = BulkEdit.Preview(session, Recipe(EditSession.Characters, [1], [], [], new BulkStep("bowLevel", BulkOperation.Set, decimal.MaxValue)));
            Equal(BulkRowStatus.Blocked, level.Rows.Single().Status);
            Equal(false, session.CanUndo);
        });

        test("A selection can be saved and reused", () =>
        {
            var selection = new BulkSelection([3], ["Bow"], [BulkGroups.Shields], [7], true);
            var saved = BulkEdit.LoadSelection(BulkEdit.SaveSelection("Bow selection", EditSession.Items, selection));
            Equal("Bow selection", saved.Name);
            Equal(true, saved.Selection.Ids.SequenceEqual(selection.Ids) && saved.Selection.Groups.SequenceEqual(selection.Groups) && saved.Selection.IncludeUnused);
            Throws<EditException>(() => BulkEdit.LoadSelection("{\"Version\": 1, \"Name\": \"x\", \"Table\": \"classes\"}"u8));
            Throws<EditException>(() => BulkEdit.LoadSelection("[]"u8));
        });

        test("A recipe that changes nothing makes no undo step", () =>
        {
            var session = new EditSession(Fixture());
            var preview = BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("weight", BulkOperation.Set, 5)));
            Equal(0, preview.Edits.Count);
            Equal(false, BulkEdit.Apply(session, preview));
            Equal(false, session.CanUndo);
        });

        test("Recipes are refused for unsupported fields, choices with arithmetic, and unknown records, types or groups", () =>
        {
            var session = new EditSession(Fixture());
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("effect12", BulkOperation.Set, 1))));
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("durability", BulkOperation.Add, 1))));
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Items, [99], [], [], new BulkStep("might", BulkOperation.Set, 1))));
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Items, [], ["Laser"], [], new BulkStep("might", BulkOperation.Set, 1))));
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Items, [], [], [], ["Lances"], new BulkStep("might", BulkOperation.Set, 1))));
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Characters, [], ["Bow"], [], new BulkStep("level", BulkOperation.Set, 1))));
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Classes, [1], [], [], new BulkStep("move", BulkOperation.Set, 5))));
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [])));
            Throws<EditException>(() => BulkEdit.Preview(session, Recipe(EditSession.Items, [1], [], [], new BulkStep("might", BulkOperation.Clamp, 10, 5))));
        });

        test("A durability grade is set by choice", () =>
        {
            var session = new EditSession(Fixture());
            var preview = BulkEdit.Preview(session, Recipe(EditSession.Items, [1, 2], [], [], new BulkStep("durability", BulkOperation.Set, 1)));
            Equal("A", preview.Rows[0].Proposed);
            Equal("grade", preview.Rows[0].Unit);
            Equal(true, BulkEdit.Apply(session, preview));
            Equal(1, session.Value(EditSession.Items, 2, "durability"));
        });

        test("Spare records and broken-weapon placeholders stay out of type and group selections unless named or included", () =>
        {
            var session = new EditSession(Fixture());
            var recipe = Recipe(EditSession.Items, [], ["Bow"], [], new BulkStep("weight", BulkOperation.Add, 1));
            var preview = BulkEdit.Preview(session, recipe);
            Equal(3, preview.ChangedRecords);
            Equal(true, preview.Skipped.Single(s => s.Id == 6).Reason.Contains("spare record"));
            Equal(true, preview.Skipped.Single(s => s.Id == 7).Reason.Contains("broken form"));
            Equal(0, preview.Warnings.Count);
            var included = BulkEdit.Preview(session, recipe with { Selection = recipe.Selection with { IncludeUnused = true } });
            Equal(5, included.ChangedRecords);
            Equal(2, included.Warnings.Count);
            var named = BulkEdit.Preview(session, recipe with { Selection = recipe.Selection with { Ids = [7] } });
            Equal(4, named.ChangedRecords);
            Equal(true, named.Warnings.Single().Contains("BrokenBow"));
            Equal(false, BulkGroups.IsBrokenPlaceholder(session, 10));
        });

        test("A group recipe leaves out personal weapons and a bow with its own range unless they are named", () =>
        {
            var session = new EditSession(Fixture());
            var range = Recipe(EditSession.Items, [], [], [], new BulkStep("maxRange", BulkOperation.Set, 3)) with
            { Selection = new BulkSelection([], [], [BulkGroups.Bows], []) };
            var preview = BulkEdit.Preview(session, range);
            Equal(true, preview.Skipped.Single(s => s.Id == 3).Reason.StartsWith("Left out: its range (", StringComparison.Ordinal));
            Equal(true, preview.Skipped.Single(s => s.Id == 11).Reason.StartsWith("Left out: a personal weapon", StringComparison.Ordinal));
            Equal(false, preview.Edits.Any(e => e.Id is 3 or 11));
            Equal(2, preview.ChangedRecords);

            var named = range with { Selection = new BulkSelection([3, 11], [], [BulkGroups.Bows], []) };
            var included = BulkEdit.Preview(session, named);
            Equal(true, included.Warnings.Any(w => w.StartsWith("Serenia Bow (item 3) has its own range", StringComparison.Ordinal)));
            Equal(true, included.Warnings.Any(w => w.StartsWith("Pascanion (item 11) is a personal weapon", StringComparison.Ordinal)));
            Equal(true, included.Edits.Any(e => e.Id == 11));
            Equal(true, included.Rows.Any(r => r.Id == 3 && r.Status == BulkRowStatus.Unchanged));

            var might = Recipe(EditSession.Items, [], [], [], new BulkStep("might", BulkOperation.Add, 1)) with
            { Selection = new BulkSelection([], [], [BulkGroups.Bows], []) };
            var mightPreview = BulkEdit.Preview(session, might);
            Equal(true, mightPreview.Edits.Any(e => e.Id == 3));
            Equal(false, mightPreview.Edits.Any(e => e.Id == 11));
        });

        test("Personal weapons and own ranges can be included on purpose, and range edits never change which records are picked", () =>
        {
            var session = new EditSession(Fixture());
            var range = Recipe(EditSession.Items, [], [], [], new BulkStep("maxRange", BulkOperation.Set, 3)) with
            { Selection = new BulkSelection([], [], [BulkGroups.Bows], [], IncludeSpecial: true) };
            var all = BulkEdit.Preview(session, range);
            Equal(false, all.Skipped.Any(s => s.Id is 3 or 11));
            Equal(true, all.Warnings.Any(w => w.StartsWith("Serenia Bow (item 3) has its own range", StringComparison.Ordinal)));
            Equal(true, all.Warnings.Any(w => w.StartsWith("Pascanion (item 11) is a personal weapon", StringComparison.Ordinal)));
            Equal(true, all.Edits.Any(e => e.Id == 11));

            // Changing the bows' ranges first must not change what a range recipe leaves out.
            var plain = range with { Selection = range.Selection with { IncludeSpecial = false } };
            var before = BulkEdit.Preview(session, plain).Skipped.Select(s => s.Id).ToList();
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["maxRange"] = 3 });
            session.Apply(EditSession.Items, 2, new Dictionary<string, int> { ["maxRange"] = 3 });
            Equal(true, BulkEdit.Preview(session, plain).Skipped.Select(s => s.Id).SequenceEqual(before));
            Equal(true, BulkEdit.Preview(session, plain).Skipped.Any(s => s.Id == 3));

            // Two equally common ranges: none is the usual one, nothing is left out for range, and the preview says why.
            var tieSession = new EditSession(Fixture());
            var tied = BulkEdit.Preview(tieSession, plain with { Selection = new BulkSelection([], [], [BulkGroups.Bows], [2]) });
            Equal(true, tied.Warnings.Any(w => w.StartsWith("These records share no single usual range", StringComparison.Ordinal)));
            Equal(false, tied.Skipped.Any(s => s.Reason.Contains("differs from the usual", StringComparison.Ordinal)));

            var saved = BulkEdit.LoadSelection(BulkEdit.SaveSelection("special", EditSession.Items, range.Selection));
            Equal(true, saved.Selection.IncludeSpecial);
        });

        test("A recipe saved against one edition replays by record number on the other", () =>
        {
            var english = new EditSession(Fixture());
            var japanese = new EditSession(Fixture() with { Profile = Fixture().Profile! with { Name = "Other edition" }, Sha256 = "other" });
            var recipe = Recipe(EditSession.Items, [1, 2], [], [], new BulkStep("might", BulkOperation.Add, 2));
            var saved = BulkEdit.Save(recipe);
            var a = BulkEdit.Preview(english, BulkEdit.Load(saved));
            var b = BulkEdit.Preview(japanese, BulkEdit.Load(saved));
            Equal(true, a.Edits.SequenceEqual(b.Edits));
            Equal(true, a.Rows.Select(r => (r.Id, r.Field, r.Proposed)).SequenceEqual(b.Rows.Select(r => (r.Id, r.Field, r.Proposed))));
            string text = Encoding.UTF8.GetString(saved);
            Throws<EditException>(() => BulkEdit.Preview(japanese, BulkEdit.Load(Encoding.UTF8.GetBytes(text.Replace(BulkEdit.Schema(EditSession.Items), "0000000000000000", StringComparison.Ordinal)))));
            Throws<EditException>(() => BulkEdit.Load(Encoding.UTF8.GetBytes(text.Replace("\"Version\": 2", "\"Version\": 3", StringComparison.Ordinal))));
        });

        test("Item groups follow each record's data", () =>
        {
            var session = new EditSession(Fixture());
            int[] Members(string group) => BulkEdit.Preview(session, Recipe(EditSession.Items, [], [], [], [group], new BulkStep("price", BulkOperation.Add, 1)))
                .Rows.Select(r => r.Id).ToArray();
            Equal("1,2,3", string.Join(",", Members(BulkGroups.Bows)));
            Equal("4", string.Join(",", Members(BulkGroups.Crossbows)));
            Equal("8", string.Join(",", Members(BulkGroups.ConsumableThrowing)));
            Equal("9", string.Join(",", Members(BulkGroups.ReusableThrowing)));
            Equal(0, Members(BulkGroups.Shields).Length);
        });

        test("Search finds records by name or number", () =>
        {
            var session = new EditSession(Fixture());
            Equal("1,2", string.Join(",", BulkEdit.Search(session, EditSession.Items, "bow").Where(r => r.Id < 3).Select(r => r.Id)));
            Equal(5, BulkEdit.Search(session, EditSession.Items, "0x5").Single().Id);
            Equal(4, BulkEdit.Search(session, EditSession.Items, "4").Single().Id);
            Equal("Knight", BulkEdit.Search(session, EditSession.Characters, "kni").Single().Name);
        });

        test("Recipes save without paths, replay to the same edits, and survive saving the project", () =>
        {
            var recipe = Recipe(EditSession.Items, [4], ["Bow"], [3], new BulkStep("accuracy", BulkOperation.Add, -5)) with { Name = "Bow accuracy" };
            byte[] saved = BulkEdit.Save(recipe);
            string json = Encoding.UTF8.GetString(saved);
            Equal(false, json.Contains(":\\") || json.Contains("source.iso"));
            var loaded = BulkEdit.Load(saved);
            var session = new EditSession(Fixture());
            var a = BulkEdit.Preview(session, recipe).Edits;
            Equal(true, a.SequenceEqual(BulkEdit.Preview(new EditSession(Fixture()), loaded).Edits));
            Equal(3, a.Count);
            Throws<EditException>(() => BulkEdit.Load(Encoding.UTF8.GetBytes(json.Replace("\"Version\": 2", "\"Version\": 1"))));
            Throws<EditException>(() => BulkEdit.Load("not json"u8));
            Throws<EditException>(() => BulkEdit.Load("{\"Version\": 2, \"Name\": \"x\", \"Table\": \"items\"}"u8));

            Equal(true, BulkEdit.Apply(session, BulkEdit.Preview(session, loaded)));
            string folder = Path.Combine(Path.GetTempPath(), $"bw-bulk-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                string project = Path.Combine(folder, "bulk.bwproj");
                session.Save(project);
                var reopened = EditSession.Load(Fixture(), project);
                foreach (var e in a) Equal(e.Value, reopened.Value(e.Table, e.Id, e.Field));
                // An Add step is relative, so running the recipe again on the reopened project lowers accuracy once more.
                Equal(true, BulkEdit.Preview(reopened, loaded).Edits.Select(e => e.Value + 5).SequenceEqual(a.Select(e => e.Value)));
            }
            finally { Directory.Delete(folder, true); }
        });
    }

    private static BulkRecipe Recipe(string table, int[] ids, string[] types, int[] excluded, params BulkStep[] steps) =>
        Recipe(table, ids, types, excluded, [], steps);

    private static BulkRecipe Recipe(string table, int[] ids, string[] types, int[] excluded, string[] groups, params BulkStep[] steps) =>
        new(BulkRecipe.CurrentVersion, "Test", table, new BulkSelection(ids, types, groups, excluded), steps);

    /// <summary>
    /// Bows, a crossbow, a sword, a spare record, a broken bow, a spear that is used up when thrown and one that wears down,
    /// an unbreakable unpriced magic weapon, and two characters, one with an odd stored Mind growth.
    /// </summary>
    internal static InspectionResult Fixture()
    {
        byte[] Item(int type, int might, int min, int max, int hit, int price = 600, int uses = 101, params string[] flags)
        {
            var bytes = new byte[56];
            void W(string key, int v) => FieldCodec.Write(bytes, ItemEncoder.Field(key), v);
            W("type", type); W("might", might); W("minRange", min); W("maxRange", max); W("weight", 5); W("accuracy", 70); W("hitBonus", hit);
            W("durability", 3); W("price", price); W("uses", uses);
            foreach (string flag in flags) W(flag, 1);
            return bytes;
        }
        var items = new Dictionary<int, byte[]>
        {
            [1] = Item(Bow, 8, 1, 2, -10), [2] = Item(Bow, 10, 1, 2, 0), [3] = Item(Bow, 12, 1, 3, 0), [4] = Item(Crossbow, 9, 0, 1, 0),
            [5] = Item(Sword, 7, 1, 1, 0), [6] = Item(Bow, 1, 1, 2, 0), [7] = Item(Bow, 0, 1, 2, 0, 0, 0, "effect34"),
            [8] = Item(Spear, 12, 1, 1, 0, 300, 6, "effect60", "effect19"), [9] = Item(Spear, 13, 0, 1, 0, 5500, 101, "effect60"),
            [10] = Item(11, 21, 1, 2, 0, 0, 0, "effect34"), [11] = Item(Bow, 10, 1, 2, 0, 5200, 10, "effect32"),
        };
        byte[] Unit(int bow)
        {
            var bytes = new byte[256]; bytes[19] = 1; bytes[20] = 5;
            FieldCodec.Write(bytes, CharacterFields.Record.Single(f => f.Key == "bowLevel"), bow);
            return bytes;
        }
        byte[] Growth(int mindStored)
        {
            var bytes = new byte[32]; bytes[2] = 1 << 5; Array.Fill(bytes, (byte)255, 26, 6);
            FieldCodec.Write(bytes, CharacterFields.Growth.Single(f => f.Key == MindGrowth.Field), mindStored);
            return bytes;
        }
        var units = new Dictionary<int, byte[]> { [1] = Unit(20), [2] = Unit(0) };
        var growths = new Dictionary<int, byte[]> { [1] = Growth(31), [2] = Growth(50) };
        var cls = new byte[100];
        var profile = new GameProfile("Test edition", "hash", 0, 1024)
        { UnitTable = 2048, SecondUnitTable = 4096, ClassTable = 8192, SecondClassTable = 16384 };
        string[] names = ["", "Hunter Bow", "Long Bow", "Serenia Bow", "Crossbow", "Sword", "DUMMYBow", "BrokenBow", "Pilum", "Angon", "Flametongue", "Pascanion"];
        return new("source.iso", 8 * 1024 * 1024, "", "", "", "hash", "", profile,
            [new("DATA3.DAT", 0, 4 * 1024 * 1024), new("SLPS_254.97", 4 * 1024 * 1024, 4 * 1024 * 1024)],
            items.Select(i => ItemDecoder.Decode(i.Key, names[i.Key], i.Value)).ToList(), 0)
        {
            ItemBytes = items.ToDictionary(i => i.Key, i => (ReadOnlyMemory<byte>)i.Value),
            UnitBytes = units.ToDictionary(u => u.Key, u => (ReadOnlyMemory<byte>)u.Value),
            GrowthBytes = growths.ToDictionary(g => g.Key, g => (ReadOnlyMemory<byte>)g.Value),
            ClassBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = cls },
            Characters = [CharacterDecoder.Decode(new(1, "Archer", 0), units[1], growths[1], new Dictionary<int, string>()),
                          CharacterDecoder.Decode(new(2, "Knight", 256), units[2], growths[2], new Dictionary<int, string>())],
            Classes = [ClassDecoder.Decode(1, "Soldier", cls)],
            CharacterOffsets = new Dictionary<int, int> { [1] = 0, [2] = 256 },
            ExecutableFile = new("SLPS_254.97", 4 * 1024 * 1024, 4 * 1024 * 1024),
        };
    }

    private static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual} (line {line})");
    }
    private static Exception Catch(Action action)
    {
        try { action(); } catch (Exception e) { return e; }
        throw new Exception("Expected an error");
    }
    private static void Throws<T>(Action action, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name} (line {line})");
    }
}
