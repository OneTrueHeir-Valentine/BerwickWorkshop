internal static class TestRunnerArgumentTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Runner preserves offline, help, focused and positional modes", () =>
        {
            (string[] Args, TestRunMode Mode)[] cases =
            [
                ([], TestRunMode.Offline), (["--help"], TestRunMode.Help), (["-h"], TestRunMode.Help),
                (["--maps"], TestRunMode.Maps), (["--animation-export"], TestRunMode.AnimationExport),
                (["jp.iso", "en.iso"], TestRunMode.Discs), (["jp.iso", "en.iso", "mod.iso"], TestRunMode.Discs),
                (["--promotion-audit", "jp.iso", "private"], TestRunMode.PromotionAudit)
            ];
            foreach (var (args, expected) in cases)
                if (!TestRunnerArguments.TryParse(args, out var actual, out var error) || actual != expected)
                    throw new Exception($"Wrong runner mode: {string.Join(' ', args)} ({error})");
        });
        test("Runner refuses options in every positional ISO argument", () =>
        {
            foreach (string option in new[] { "--unknown", "--promotion-audit", "--help", "--maps", "--animation-export" })
                for (int count = 2; count <= 3; count++)
                    for (int index = 0; index < count; index++)
                    {
                        var args = Enumerable.Repeat("disc.iso", count).ToArray();
                        args[index] = option;
                        // A leading audit with exactly two paths has its own mode; it is never positional.
                        bool accepted = TestRunnerArguments.TryParse(args, out var mode, out _);
                        if (accepted && mode == TestRunMode.Discs)
                            throw new Exception($"Option reached ISO mode: {string.Join(' ', args)}");
                    }
        });
        test("Runner rejects malformed audit and positional argument counts", () =>
        {
            string[][] cases =
            [
                ["--promotion-audit"], ["--promotion-audit", "disc.iso"], ["--promotion-audit", "disc.iso", "private", "extra"],
                ["--promotion-audit", "<source>", "private"], ["--promotion-audit", "disc.iso", "<private>"],
                ["--promotion-audit", "--maps", "private"], ["--promotion-audit", "disc.iso", "--help"],
                ["--unknown"], ["--maps", "extra"], ["--animation-export", "extra"],
                ["disc.iso"], ["one", "two", "three", "four"], ["<jp>", "en.iso"], ["jp.iso", " "]
            ];
            foreach (var args in cases)
                if (TestRunnerArguments.TryParse(args, out _, out var error) || string.IsNullOrWhiteSpace(error))
                    throw new Exception($"Malformed invocation accepted: {string.Join(' ', args)}");
        });
    }
}
