using Berwick.Core;

public static class RandomizerR0Tests
{
    public static void Run(Action<string, Action> test)
    {
        test("Starting-item randomizer excludes both mounted and map-pickup horses", () =>
        {
            foreach (int item in new[] { 0x205, 0x214, 0x264, 0x274, 0x20B, 0x26B })
                if (!Randomizer.IsHorseRecord(item))
                    throw new Exception($"A horse record was not excluded: 0x{item:X}");
            foreach (int item in new[] { 0x204, 0x215, 0x263, 0x275 })
                if (Randomizer.IsHorseRecord(item))
                    throw new Exception($"An adjacent non-horse ID was excluded: 0x{item:X}");
        });
        test("Eight-slot randomization is opt-in and preserves legacy choices and empty slots", () =>
        {
            var disc = ChangeReviewTests.Fixture();
            var bytes = new Dictionary<int, ReadOnlyMemory<byte>>();
            var items = new List<ItemRecord>();
            foreach (int id in new[] { 1, 2, 0x205 })
            {
                var item = disc.ItemBytes[1].ToArray();
                FieldCodec.Write(item, ItemEncoder.Fields.Single(f => f.Key == "price"), 100);
                FieldCodec.Write(item, ItemEncoder.Fields.Single(f => f.Key == "uses"), 20);
                bytes[id] = item;
                items.Add(ItemDecoder.Decode(id, $"Test item {id}", item));
            }
            var bag = new byte[BagData.RecordLength];
            "SACK"u8.CopyTo(bag);
            FieldCodec.Write(bag, BagData.Fields.Single(f => f.Key == "bagCount"), 1);
            FieldCodec.Write(bag, BagData.Fields.Single(f => f.Key == BagData.ItemKey(0)), 1);
            FieldCodec.Write(bag, BagData.Fields.Single(f => f.Key == BagData.UsesKey(0)), 20);
            var session = new EditSession(disc with { Items = items, ItemBytes = bytes,
                BagBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = bag } });
            session.Apply(EditSession.Characters, 1, new Dictionary<string, int> {
                ["item1"] = 1, ["item1Uses"] = 20, ["item6"] = 1, ["item6Uses"] = 20,
                ["item8"] = 0x205, ["item8Uses"] = 20,
            });
            bool isExtra(FieldEdit e) => e.Table == EditSession.Characters &&
                new[] { "item6", "item6Uses", "item7", "item7Uses", "item8", "item8Uses" }.Contains(e.Field);
            var old = new RandomizeOptions(7);
            var extended = old with { ExtendedInventory = true };
            if (System.Text.Json.JsonSerializer.Serialize(old).Contains("ExtendedInventory") ||
                System.Text.Json.JsonSerializer.Deserialize<RandomizeOptions>("{\"Seed\":7}")!.ExtendedInventory ||
                System.Text.Json.JsonSerializer.Deserialize<RandomizeOptions>(System.Text.Json.JsonSerializer.Serialize(extended)) != extended)
                throw new Exception("Inventory scope did not survive legacy/default serialization.");
            var legacy = Randomizer.StartingItems(session, old);
            var expandedLegacy = Randomizer.StartingItems(session, extended);
            if (!legacy.SequenceEqual(expandedLegacy.Where(e => !isExtra(e))) || legacy.Any(isExtra) ||
                !expandedLegacy.Any(e => e.Field == "item6") || expandedLegacy.Any(e => e.Field is "item7" or "item8"))
                throw new Exception("Sequential randomizer changed old draws or selected an excluded slot.");
            // Try several fixed seeds so the test does not depend on a particular draw selecting a different item.
            bool changed = false;
            for (int seed = 0; seed < 32; seed++)
            {
                var before = RandomizerR0.PreviewStartingItems(session, old with { Seed = seed });
                var after = RandomizerR0.PreviewStartingItems(session, extended with { Seed = seed });
                var repeat = RandomizerR0.PreviewStartingItems(session, extended with { Seed = seed });
                if (before.FieldEdits.Any(isExtra) || !before.FieldEdits.SequenceEqual(after.FieldEdits.Where(e => !isExtra(e))) ||
                    !after.FieldEdits.SequenceEqual(repeat.FieldEdits) || after.Replacements.Any(e => e.Table == EditSession.Characters && (e.Slot is 6 or 7)))
                    throw new Exception("Keyed randomizer changed the old scope or selected an empty/horse slot.");
                changed |= after.Replacements.Any(e => e.Table == EditSession.Characters && e.Slot == 5);
            }
            if (!changed) throw new Exception("No occupied extended slot was randomized.");
        });
        test("R0 randomizer seeded substreams are deterministic across call ordering", () =>
        {
            ulong a = R0SeedStreams.Word(12345, "starting-items", "characters", 87, 0);
            ulong unrelated = R0SeedStreams.Word(12345, "starting-items", "characters", 88, 0);
            ulong b = R0SeedStreams.Word(12345, "starting-items", "characters", 87, 0);
            if (a != 0x5E0D88F0E5F269E8UL) throw new Exception("R0 stable algorithm test vector changed.");
            if (a != b || unrelated == a) throw new Exception("Keyed substreams failed repeatability or record independence.");
            if (R0SeedStreams.Choice(12345, "starting-items", "characters", 87, 0, 7) is < 0 or >= 7)
                throw new Exception("Out-of-range deterministic pool choice.");
        });
        test("R0 substreams isolate modules, records, slots and seeds", () =>
        {
            var set = new HashSet<ulong>();
            for (int i = 0; i < 10; i++) set.Add(R0SeedStreams.Word(7, "starting-items", "characters", 87, i));
            if (set.Count != 10) throw new Exception("Slot-specific draws unexpectedly collided.");
            ulong original = R0SeedStreams.Word(7, "starting-items", "characters", 87, 0);
            if (original == R0SeedStreams.Word(7, "growths", "characters", 87, 0)
                || original == R0SeedStreams.Word(7, "starting-items", "characters", 88, 0)
                || original == R0SeedStreams.Word(8, "starting-items", "characters", 87, 0))
                throw new Exception("Module/entity/seed boundaries were not preserved.");
        });
        test("R0 indexed randomizer never accepts an empty candidate pool", () =>
        {
            try { _ = R0SeedStreams.Choice(1, "starting-items", "characters", 87, 0, 0); }
            catch (ArgumentOutOfRangeException) { return; }
            throw new Exception("A zero-length pool did not fail closed.");
        });
        test("R0 rejects reconstituted or altered previews without accepting their supplied edits", () =>
        {
            var session = new EditSession(ChangeReviewTests.Fixture());
            var index = ExampleAudit(106);
            var options = new RandomizeOptions(7);
            var preview = RandomizerR0.PreviewStartingItems(session, options, index);
            CheckRejected(() => RandomizerR0.Apply(session, preview, index),
                "The preview's original script-index object was reused for application.");
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<R0StartingItemPreview>(preview.ToJson())!;
            CheckRejected(() => RandomizerR0.Apply(session, deserialized, index),
                "Deserialized preview bypassed source replay validation.");
            CheckRejected(() => RandomizerR0.Apply(session, preview with { Options = options with { Seed = 8 } }, index),
                "Changed preview seed was accepted.");
            CheckRejected(() => RandomizerR0.Apply(session, preview with
                { Replacements = [new(EditSession.Characters, 1, 0, 1, 2, 10, 20)] }, index),
                "Forged replacement or durability was accepted.");
            CheckRejected(() => RandomizerR0.Apply(session, preview with
                { ExpectedValues = [new(EditSession.Items, 1, "might", 10)] }, index),
                "Forged original value was accepted.");
            CheckRejected(() => RandomizerR0.Apply(session, preview with { ScriptAuditComplete = false }, index),
                "Forged audit completeness was accepted.");
            CheckRejected(() => RandomizerR0.Apply(session, preview with { ScriptReferences = [new(106, [12])] }, index),
                "Forged warning list was accepted.");
            CheckRejected(() => RandomizerR0.Apply(session, preview with { UnreadableEvents = [13] }, index),
                "Forged unreadable-event list was accepted.");
            CheckRejected(() => RandomizerR0.Apply(session, preview with { FieldEdits = [new(EditSession.Items, 1, "might", 60)] }, index),
                "Caller-supplied field edits were accepted.");
            var next = RandomizerR0.PreviewStartingItems(session, options, index);
            CheckRejected(() => RandomizerR0.Apply(session, preview, index),
                "Superseded preview was accepted.");
            if (RandomizerR0.Apply(session, next, ExampleAudit(106)))
                throw new Exception("Empty test-fixture preview should not have changed any items.");
            CheckRejected(() => RandomizerR0.Apply(session, next, index),
                "An already consumed preview was accepted twice.");
        });
        test("R0 detects a project edit with an unchanged change count", () =>
        {
            var session = new EditSession(ChangeReviewTests.Fixture());
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["might"] = 11 });
            var index = ExampleAudit(106);
            var preview = RandomizerR0.PreviewStartingItems(session, new RandomizeOptions(9), index);
            int count = session.ChangeCount;
            session.Apply(EditSession.Items, 1, new Dictionary<string, int> { ["might"] = 12 });
            if (count != session.ChangeCount) throw new Exception("Test requires a stable change count.");
            CheckRejected(() => RandomizerR0.Apply(session, preview, ExampleAudit(106)),
                "A stale project preview with the same edit count was accepted.");
        });
        test("R0 warns on incomplete source coverage without silently treating it as verified", () =>
        {
            var session = new EditSession(ChangeReviewTests.Fixture());
            var good = ExampleAudit(106);
            ScriptDependencyIndex IncompleteIndex() => ScriptDependencyIndex.Build([12, 13], n => n == 13 ? new byte[7] :
                ScriptWriter.Write([new ScriptNode { Command = ScriptParser.Event, Locals = 0,
                    Args = [Array.Empty<ScriptNode>()] }]));
            var incomplete = IncompleteIndex();
            if (incomplete.UnreadableEvents.Count != 1) throw new Exception("Unreadable event was not recorded.");
            var preview = RandomizerR0.PreviewStartingItems(session, new RandomizeOptions(9), incomplete);
            if (preview.ScriptAuditComplete || !preview.UnreadableEvents.SequenceEqual(new[] { 13 }))
                throw new Exception("Incomplete source coverage was misclassified.");
            CheckRejected(() => RandomizerR0.Apply(session, preview, incomplete),
                "Missing incomplete-audit acknowledgment was ignored.");
            if (RandomizerR0.Apply(session, preview, IncompleteIndex(), permitIncompleteScriptAudit: true))
                throw new Exception("Empty test-fixture preview changed items.");
        });
        test("R0 requires a new source-script audit and detects changes outside item references", () =>
        {
            var session = new EditSession(ChangeReviewTests.Fixture());
            var index = ExampleAudit(106);
            var preview = RandomizerR0.PreviewStartingItems(session, new RandomizeOptions(9), index);
            CheckRejected(() => RandomizerR0.Apply(session, preview, null!),
                "A missing fresh audit was accepted.");
            var changed = ExampleAudit(107);
            if (string.Equals(index.SourceSha256, changed.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Source-script digest did not change.");
            CheckRejected(() => RandomizerR0.Apply(session, preview, changed),
                "A changed source-script audit was accepted.");
        });
    }

    private static ScriptDependencyIndex ExampleAudit(int value)
    {
        var script = ScriptWriter.Write([new ScriptNode { Command = ScriptParser.Event, Locals = 0,
            Args = [new ScriptNode[] { new() { Command = 0x20, Args =
                [new ScriptNode[] { new() { Command = ScriptParser.Number, Value = value } }] } }] }]);
        return ScriptDependencyIndex.Build([12], _ => script);
    }

    private static void CheckRejected(Action run, string failure)
    {
        try { run(); }
        catch (EditException) { return; }
        throw new Exception(failure);
    }

    public static void RunDiscs(Action<string, Action> test, InspectionResult disc)
    {
        test($"R0 starting-item replay and undo preserve source safety ({disc.Edition})", () =>
        {
            ScriptDependencyIndex Audit()
            {
                using var stream = new FileStream(disc.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var library = new EventLibrary(new GameArchives(new IsoImage(stream)));
                return ScriptDependencyIndex.Build(library.Numbers, library.Bytes, library.CommandNames);
            }
            var session = new EditSession(disc);
            var pool = Randomizer.Pool(session);
            if (pool.Any(item => Randomizer.IsHorseRecord(item.Id)))
                throw new Exception("Horse records were included in the starting-item pool.");
            var preview = RandomizerR0.PreviewStartingItems(session,
                new RandomizeOptions(12345) { Weapons = true, Ammunition = true, Other = true, Bags = true }, Audit());
            if (preview.Replacements.Any(change => Randomizer.IsHorseRecord(change.ToItem)
                || Randomizer.IsHorseRecord(change.FromItem)))
                throw new Exception("A horse record was selected as an ordinary inventory replacement.");
            if (preview.Replacements.Count == 0) throw new Exception("No original-disc starting items were selected.");
            int before = session.ChangeCount;
            if (!RandomizerR0.Apply(session, preview, Audit(), permitScriptReferences: true,
                    permitIncompleteScriptAudit: false))
                throw new Exception("A verified, permitted starting-item replay made no edits.");
            if (session.ChangeCount <= before) throw new Exception("Starting-item edits were not applied.");
            var saved = session.ToJson();
            session.Undo();
            if (session.ChangeCount != before) throw new Exception("R0 edits were not undone in one step.");
            session.Redo();
            if (session.ToJson() != saved) throw new Exception("R0 redo changed replayed records.");
            var reopened = EditSession.FromJson(disc, saved);
            if (reopened.ToJson() != saved) throw new Exception("R0 project round-trip changed saved edits.");
        });
    }
}
