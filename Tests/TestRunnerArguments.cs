internal enum TestRunMode { Offline, Help, Maps, AnimationExport, PromotionAudit, Discs }

internal static class TestRunnerArguments
{
    public static bool TryParse(string[] args, out TestRunMode mode, out string error)
    {
        mode = TestRunMode.Offline;
        error = "";
        if (args.Length == 0)
            return true;
        if (args[0] == "--promotion-audit")
        {
            mode = TestRunMode.PromotionAudit;
            return PromotionAuditCommand.TryParse(args, out _, out _, out error);
        }
        if (args is ["--help"] or ["-h"])
        {
            mode = TestRunMode.Help;
            return true;
        }
        if (args is ["--maps"])
        {
            mode = TestRunMode.Maps;
            return true;
        }
        if (args is ["--animation-export"])
        {
            mode = TestRunMode.AnimationExport;
            return true;
        }
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal)) is { } option)
        {
            error = $"Unknown or incomplete test runner option: {option}";
            return false;
        }
        if (args.Length is not (2 or 3))
        {
            error = "Integration tests need two original ISO paths and optionally a third comparison ISO.";
            return false;
        }
        if (args.Any(arg => string.IsNullOrWhiteSpace(arg) || arg.Contains('<') || arg.Contains('>')))
        {
            error = "Enter real ISO paths. Text in angle brackets is an example, not a filename.";
            return false;
        }
        mode = TestRunMode.Discs;
        return true;
    }
}
