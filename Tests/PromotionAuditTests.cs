using Berwick.Core;

public static class PromotionAuditTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Promotion audit CLI refuses missing and extra arguments before ISO tests", () =>
        {
            bool Valid(params string[] args) =>
                PromotionAuditCommand.TryParse(args, out _, out _, out _);
            if (Valid("--promotion-audit")
                || Valid("--promotion-audit", "jp.iso")
                || Valid("--promotion-audit", "jp.iso", "out", "en.iso")
                || Valid("--promotion-audit", "<Japanese original ISO>", "out")
                || Valid("--promotion-audit", "jp.iso", "<private output folder>")
                || Valid("--promotion-audit", "--maps", "out")
                || Valid("--promotion-audit", "jp.iso", "--help"))
                throw new Exception("Malformed audit invocations must be rejected.");
            if (!Valid("--promotion-audit", @"C:\\game\\jp.iso", @"C:\\private\\reports"))
                throw new Exception("An exact audit invocation was rejected.");
            if (PromotionAuditCommand.Run(["--promotion-audit"]) != 2)
                throw new Exception("Malformed arguments must exit before the ordinary game-image integration tests.");
        });

        test("Promotion audit records nested candidate commands and conditional contexts", () =>
        {
            var names = new string[100];
            names[40] = "SetUnitClass";
            names[41] = "GetUnitLevel";
            names[42] = "GetEventFlag";

            static ScriptNode Number(int value) => new() { Command = ScriptParser.Number, Value = value };
            var gate = new ScriptNode
            {
                Command = ScriptParser.If,
                Args =
                [
                    [new ScriptNode { Command = 41, Offset = 22, Args = [[Number(10)]] }],
                    [
                        new ScriptNode { Command = 42, Offset = 44, Args = [[Number(7)]] },
                        new ScriptNode { Command = 40, Offset = 70, Args = [[Number(99)]] },
                        new ScriptNode { Command = ScriptParser.RunEvent, Offset = 88, Args = [[Number(600)]] }
                    ]
                ]
            };
            var cases = new[] { (Event: 123, Nodes: (IReadOnlyList<ScriptNode>)new[] { gate }) };
            var audit = PromotionAudit.Inspect(names, cases);
            if (audit.Commands.Count != 3 || audit.EventCalls.Count != 1)
                throw new Exception("The candidate command or event-call count differs.");
            if (audit.Commands[0].Kind != "level" || !audit.Commands[0].InCondition)
                throw new Exception("Level requirement context was not retained.");
            if (audit.Commands[1].Kind != "flag" || audit.Commands[1].InCondition)
                throw new Exception("Body commands must not be marked as condition expressions.");
            if (audit.Commands[2].Kind != "class" || audit.Commands[2].LiteralArguments != "99")
                throw new Exception("Class target or literal argument was not reported.");
            if (audit.EventCalls[0].Target != 600 || audit.EventCalls[0].Event != 123)
                throw new Exception("The direct event reference was not found.");
        });

        test("Promotion audit preserves unresolved computed calls and does not invent targets", () =>
        {
            var names = new string[50];
            names[40] = "SetUnitClass";
            var computed = new ScriptNode { Command = ScriptParser.RunEvent, Offset = 4, Args = [[new ScriptNode { Command = ScriptParser.Variable, Variable = 1 }]] };
            var classChange = new ScriptNode { Command = 40, Offset = 20, Args = [[new ScriptNode { Command = ScriptParser.Variable, Variable = 3 }]] };
            var cases = new[] { (Event: 1, Nodes: (IReadOnlyList<ScriptNode>)new[] { computed, classChange }) };
            var audit = PromotionAudit.Inspect(names, cases);
            if (audit.EventCalls.Count != 1 || audit.EventCalls[0].Target is not null)
                throw new Exception("A computed run-event must remain unresolved.");
            if (audit.Commands.Count != 1 || audit.Commands[0].LiteralArguments != "?")
                throw new Exception("Computed class arguments must not be guessed.");
        });

        test("Promotion audit ignores unknown and generic script instructions", () =>
        {
            var names = new string[90];
            names[45] = "GetCurrentTurn";
            var cases = new[] { (Event: 10, Nodes: (IReadOnlyList<ScriptNode>)new[] { new ScriptNode { Command = 45, Offset = 12 } }) };
            var audit = PromotionAudit.Inspect(names, cases);
            if (audit.Commands.Count != 0 || audit.EventCalls.Count != 0)
                throw new Exception("An unrelated instruction became promotion evidence.");
        });
    }
}
