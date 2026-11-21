using Berwick.Core;

internal sealed record OnMapMember(string Name, int StoredSize, bool Compressed, string Prefix);

internal static class OnMapArchiveInspection
{
    private const int SampleArchives = 12;
    private const int SampleMembers = 12;
    private const int MaxMemberBytes = 16 * 1024 * 1024;

    public static IReadOnlyList<OnMapMember> Describe(ReadOnlySpan<byte> archive, int maximum = SampleMembers)
    {
        if (maximum is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        var result = new List<OnMapMember>();
        foreach (var entry in Tarc.Parse(archive).Take(maximum))
        {
            string prefix;
            if (entry.Size > MaxMemberBytes)
                prefix = "(large member, not extracted)";
            else
            {
                try
                {
                    var bytes = Tarc.Extract(archive, entry);
                    prefix = Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length)));
                }
                catch (InvalidDataException)
                {
                    prefix = "(could not decompress)";
                }
            }
            result.Add(new(entry.Name, entry.Size, entry.Compressed, prefix));
        }
        return result;
    }

    public static int Run(string imagePath)
    {
        try
        {
            using var stream = File.OpenRead(imagePath);
            using var iso = new IsoImage(stream);
            var archives = new GameArchives(iso);
            var files = archives.FolderFiles("onmap").OrderBy(item => item.Top).ToList();
            Console.WriteLine($"Found {files.Count} archives in onmap.");
            if (files.Count == 0) return 1;
            foreach (var (top, hash) in files.Take(SampleArchives))
            {
                var data = archives.Top(top);
                Console.WriteLine($"Archive {top}, hash 0x{hash:X7}, {data.Length} bytes");
                if (!Tarc.IsArchive(data))
                {
                    Console.WriteLine("  Not a TARC archive");
                    continue;
                }
                var total = Tarc.Parse(data).Count;
                Console.WriteLine($"  {total} members");
                foreach (var member in Describe(data))
                    Console.WriteLine($"  {member.Name} | {member.StoredSize} bytes | {(member.Compressed ? "compressed" : "raw")} | {member.Prefix}");
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
