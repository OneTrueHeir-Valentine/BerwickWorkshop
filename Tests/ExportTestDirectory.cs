internal sealed class ExportTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "berwick-export-test-" + Guid.NewGuid().ToString("N"));
    public ExportTestDirectory() => Directory.CreateDirectory(Root);
    public string Target(string name = "export") => Path.Combine(Root, name);
    public void Dispose() => Directory.Delete(Root, true);
}
