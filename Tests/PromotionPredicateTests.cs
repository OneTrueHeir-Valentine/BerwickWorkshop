using Berwick.Core;

internal static class PromotionPredicateTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Shared promotion thresholds preserve all-of requirement grouping", () =>
        {
            var source = Predicate();
            var fields = PromotionPredicates.Read(28, source);
            Check(fields.Count == 2 && fields.All(f => f.RequireAll), "Spear and shield must both qualify.");
            Check(fields[0].Selector == 5 && fields[0].Minimum == 30, "Spear binding wrong.");
            var changed = PromotionPredicates.Change(28, source, fields[0].LiteralIndex, 25);
            var after = PromotionPredicates.Read(28, changed);
            Check(after[0].Minimum == 25 && after[1].Minimum == 20, "Wrong threshold changed.");
            Check(PromotionPredicates.Change(28, source, fields[0].LiteralIndex, 30).SequenceEqual(source), "No-op changed bytes.");
        });
        test("Shared promotion thresholds retain any-of magic requirements", () =>
        {
            var fields = PromotionPredicates.Read(118, Predicate(destination: 118, first: 9, second: 10, op: "and"));
            Check(fields.Count == 2 && fields.All(f => !f.RequireAll), "Failure-AND must remain success-OR.");
        });
        test("Shared promotion editing refuses computed selectors and ambiguous branches", () =>
        {
            Reject(() => PromotionPredicates.Read(28, Predicate(selector: "v4")));
            Reject(() => PromotionPredicates.Read(28, Predicate(destination: 37)));
            var duplicate = ScriptWriter.Write(ScriptParser.Parse(Predicate()).Concat(ScriptParser.Parse(Predicate())).ToArray());
            Reject(() => PromotionPredicates.Read(28, duplicate));
        });
        test("Shared promotion thresholds enforce capped whole-point bounds", () =>
        {
            var source = Predicate();
            int index = PromotionPredicates.Read(28, source)[0].LiteralIndex;
            foreach (int value in new[] { 0, 63 })
                Check(PromotionPredicates.Read(28, PromotionPredicates.Change(28, source, index, value))[0].Minimum == value,
                    "Threshold boundary rejected.");
            Reject(() => PromotionPredicates.Change(28, source, index, -1));
            Reject(() => PromotionPredicates.Change(28, source, index, 64));
            Reject(() => PromotionPredicates.Change(28, source, index + 100, 20));
        });
        test("Combined destination branches expose the same shared threshold bindings", () =>
        {
            var source = ScriptWriter.Write(ScriptCompiler.Compile(
                "event (8 local values)\n    if ((v1 == command31(118)) or (v1 == command31(119)))\n        v5 = command328(v3, v0, 9)\n        if (v5 < 30)\n            v2 = 0\n        else\n    else\n    v2", null));
            Check(PromotionPredicates.Read(118, source).Single().LiteralIndex ==
                PromotionPredicates.Read(119, source).Single().LiteralIndex,
                "Shared destination branch was split into independent controls.");
        });
        test("Promotion source-class branches keep independent shield thresholds", () =>
        {
            var source = ScriptWriter.Write(ScriptCompiler.Compile(
                "event (10 local values)\n    if (v1 == command31(160))\n        v4 = command328(v3, v0, 14)\n        v5 = command328(v3, v0, 15)\n        if (v8 == command31(158))\n            if (v4 < 20)\n                v2 = 0\n            else\n        else\n        if (v8 == command31(231))\n            if ((v4 < 20) or (v5 < 10))\n                v2 = 0\n            else\n        else\n    else\n    v2", null));
            var fields = PromotionPredicates.Read(160, source);
            var medium = fields.Single(f => f.Selector == 15);
            Check(medium.SourceClasses == "Source class 231", "Class branch label lost.");
            var changed = PromotionPredicates.Change(160, source, medium.LiteralIndex, 9);
            Check(PromotionPredicates.Read(160, changed).Where(f => f.Selector == 14).All(f => f.Minimum == 20),
                "A different branch's threshold changed.");
        });
        test("Derived Mind requirements stay read-only and mixed groups are refused", () =>
        {
            var mind = Predicate(destination: 121, first: 17);
            var field = PromotionPredicates.Read(121, mind).Single(f => f.Selector == 17);
            Check(!field.Editable, "Derived Mind exposed as a proficiency control.");
            Reject(() => PromotionPredicates.Change(121, mind, field.LiteralIndex, 6));
            var mixed = ScriptWriter.Write(ScriptCompiler.Compile(
                "event (8 local values)\n    if (v1 == command31(28))\n        v5 = command328(v3, v0, 5)\n        v6 = command328(v3, v0, 14)\n        if ((v5 < 30) or ((v6 < 20) and (v5 < 25)))\n            v2 = 0\n        else\n    else\n    v2", null));
            Reject(() => PromotionPredicates.Read(28, mixed));
        });
    }

    private static byte[] Predicate(int destination = 28, int first = 5, int second = 14, string op = "or", string? selector = null) =>
        ScriptWriter.Write(ScriptCompiler.Compile($"event (8 local values)\n    if (v1 == command31({destination}))\n        v5 = command328(v3, v0, {selector ?? first.ToString()})\n        v6 = command328(v3, v0, {second})\n        if ((v5 < 30) {op} (v6 < 20))\n            v2 = 0\n        else\n    else\n    v2", null));

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is EditException or InvalidDataException) { return; }
        throw new Exception("Unsupported shared predicate accepted.");
    }
}
