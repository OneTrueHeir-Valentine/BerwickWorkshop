using Berwick.Core;

internal static class ScriptDependencyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Scripted dependency discovery preserves literals and marks computed arguments", () =>
        {
            static ScriptNode Number(int n) => new() { Command = ScriptParser.Number, Value = n };
            static ScriptNode Variable(int n) => new() { Command = ScriptParser.Variable, Variable = n };
            static ScriptNode Call(int opcode, params ScriptNode[] arguments) =>
                new() { Command = opcode, Args = arguments.Select(a => (IReadOnlyList<ScriptNode>)new[] { a }).ToList() };
            var nodes = new List<ScriptNode>
            {
                new() { Command = ScriptParser.Event, Locals = 0, Args =
                [
                    new ScriptNode[]
                    {
                        Call(0x20, Number(106)), // item2int: reference, not necessarily an item grant
                        Call(0x3AB, Number(87), Number(5)), // AddSkill
                        new ScriptNode { Command = ScriptParser.If, Args =
                        [
                            new ScriptNode[] { Call(0x79, Number(214)) },
                            new ScriptNode[] { Call(0x77, Number(214)), Call(0x49D, Number(2), Number(106)) }
                        ] },
                        Call(0x1A8, Variable(0), Variable(1), Number(0)), // computed recipient/item
                    }
                ] }
            };
            byte[] bytes = ScriptWriter.Write(nodes);
            var index = ScriptDependencyIndex.Build([12, 13], n => n == 12 ? bytes :
                ScriptWriter.Write([new ScriptNode { Command = ScriptParser.Event, Locals = 0, Args =
                [new ScriptNode[] { Call(0x4A6, Number(2)) }] }]));
            Check(index.IndexedEvents == 2 && index.UnreadableEvents.Count == 0, "Wrong source coverage.");
            Check(index.ItemReferences.TryGetValue(106, out var itemEvents)
                && itemEvents.SequenceEqual(new[] { 12 }), "Literal item reference index incorrect.");
            var skill = index.InEvent(12).Single(x => x.Opcode == 0x3AB);
            Check(skill.Kind == ScriptDependencyKind.Skills && skill.EditableNumberIndices.SequenceEqual(new[] { 1, 2 }),
                "Skill numeric arguments do not match the Numbers tab indexing.");
            var flags = index.InEvent(12).Where(x => x.Kind == ScriptDependencyKind.Flags).ToArray();
            Check(flags.Length == 2 && flags.All(x => x.Conditional), "Conditional operations not marked.");
            var delivery = index.InEvent(12).Single(x => x.Opcode == 0x1A8);
            Check(delivery.Arguments[0].Value is null && delivery.Arguments[1].Value is null
                && delivery.Arguments[2].Value == 0 && delivery.EditableNumberIndices.Count == 1,
                "Computed inventory arguments must not be treated as editable literals.");
            Check(index.Filter(ScriptDependencyKind.Shops).Count == 1, "Shop operation missing.");
            Check(index.Filter(ScriptDependencyKind.Horses).Count == 1, "Horse stock operation missing.");
            Check(index.Filter(null, verifiedOnly: true).Count == 0,
                "Source-observed operations were mistakenly upgraded to Verified.");
        });
        test("Randomizer scripted-item audit flags both old and replacement references without forbidding them", () =>
        {
            var references = new Dictionary<int, IReadOnlyList<int>>
            {
                [106] = new[] { 168, 170 }, [125] = new[] { 50 }
            };
            var found = RandomizerDependencyAudit.Inspect(references,
                new (int OldItem, int NewItem)[] { (30, 106), (106, 45), (125, 125), (30, 45) });
            Check(found.Select(x => x.ItemId).SequenceEqual(new[] { 106, 125 }),
                "Scripted item audit missed or duplicated an item.");
            Check(found[0].Events.SequenceEqual(new[] { 168, 170 }), "Lost scripted reward provenance.");
        });
        test("Script audit digest covers raw source bytes, not just recognized operations", () =>
        {
            static byte[] Event(int number) => ScriptWriter.Write([new ScriptNode
            {
                Command = ScriptParser.Event, Locals = 0,
                Args = [new ScriptNode[] { new() { Command = 0x20,
                    Args = [new ScriptNode[] { new() { Command = ScriptParser.Number, Value = number } }] } }]
            }]);
            var a = ScriptDependencyIndex.Build([8, 9], n => Event(n == 8 ? 106 : 107));
            var b = ScriptDependencyIndex.Build([8, 9], n => Event(n == 8 ? 106 : 108));
            if (a.IndexedEvents != 2 || b.IndexedEvents != 2 || a.UnreadableEvents.Count > 0
                || a.SourceSha256 == b.SourceSha256)
                throw new Exception("Source-script digest did not track all raw events.");
            if (a.SourceSha256 != ScriptDependencyIndex.Build([8, 9],
                n => Event(n == 8 ? 106 : 107)).SourceSha256)
                throw new Exception("Source-script audit digest is not repeatable.");
        });
        test("Malformed event in system dependency index is reported instead of silently omitted", () =>
        {
            byte[] valid = ScriptWriter.Write([new ScriptNode { Command = ScriptParser.Event, Locals = 0,
                Args = [Array.Empty<ScriptNode>()] }]);
            var index = ScriptDependencyIndex.Build([8, 9], n => n == 8 ? new byte[7] : valid);
            Check(index.IndexedEvents == 1 && index.UnreadableEvents.SequenceEqual(new[] { 8 }),
                "Unreadable source coverage lost.");
        });
    }

    public static void RunDiscs(Action<string, Action> test, InspectionResult disc)
    {
        test($"Source dependency index parses original {disc.Edition} events and traces scripted item 106", () =>
        {
            using var stream = new FileStream(disc.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var library = new EventLibrary(new GameArchives(new IsoImage(stream)));
            var index = ScriptDependencyIndex.Build(library.Numbers, library.Bytes, library.CommandNames);
            Check(index.IndexedEvents >= 3500, $"Only {index.IndexedEvents} events were indexable.");
            Check(index.UnreadableEvents.Count == 0, "Unparseable source events require review.");
            Check(index.ItemReferences.TryGetValue(106, out var callers)
                && callers.Contains(168) && callers.Contains(170),
                "Original source is missing the independently observed Adel/Leon item references.");
            Check(index.InEvent(168).Any(x => x.Opcode == 0x20 && x.Arguments.Any(a => a.Value == 106)),
                "Adel's literal Iron Lance reference was not decoded.");
            Check(index.InEvent(170).Any(x => x.Opcode == 0x20 && x.Arguments.Any(a => a.Value == 106)),
                "Leon's literal Iron Lance reference was not decoded.");
        });
    }

    private static void Check(bool ok, string error)
    {
        if (!ok) throw new Exception(error);
    }
}
