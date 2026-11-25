using Berwick.Core;

internal static class EventReferenceTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Event references distinguish exact literal targets from substring matches", () =>
        {
            byte[] source = ScriptWriter.Write(ScriptCompiler.Compile(
                "event (0 local values)\n    run event 168\n    run event 1681\n    run event v0\n    if 1\n        run event 168\n", null));
            var direct = EventReferenceIndex.DirectCalls(ScriptParser.Parse(source));
            Check(direct.LiteralTargets.SequenceEqual(new[] { 168, 1681, 168 }) && direct.ComputedCalls == 1,
                "Event 168 was confused with 1681 or a computed call.");
            var references = EventReferenceIndex.Build([1354, 1800], n => n == 1354
                ? source : ScriptWriter.Write(ScriptCompiler.Compile("event (0 local values)\n    run event 169\n", null)));
            Check(references.Callers(168).SequenceEqual(new[] { 1354 }), "Incorrect incoming event 168 reference.");
            Check(references.Callers(1681).SequenceEqual(new[] { 1354 }), "Incorrect incoming event 1681 reference.");
            Check(references.Callers(169).SequenceEqual(new[] { 1800 }), "Unrelated event wrongly matched.");
            Check(references.Calls(1354).SequenceEqual(new[] { 168, 1681 }), "Duplicate literal calls were not deduplicated.");
            Check(references.DynamicCallers.SequenceEqual(new[] { 1354 }), "Dynamic call was incorrectly resolved.");
            Check(references.IndexedEvents == 2 && references.UnreadableEvents.Count == 0, "Unexpected index coverage.");
            Check(references.EventsUsingCommand(ScriptParser.RunEvent).SequenceEqual(new[] { 1354, 1800 }),
                "Command source index lost an event with a direct reference.");
        });
        test("Unreadable events are reported rather than silently counted", () =>
        {
            var index = EventReferenceIndex.Build([4, 5], n => n == 4 ? new byte[7]
                : ScriptWriter.Write(ScriptCompiler.Compile("event (0 local values)\n", null)));
            Check(index.IndexedEvents == 1 && index.UnreadableEvents.SequenceEqual(new[] { 4 }),
                "Malformed event lost its coverage warning.");
        });
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
