using Berwick.Core;
using System.IO;

namespace Berwick.Workshop;

internal sealed record MapBattle(int Battle, string Text, string File);
internal sealed record MapCatalogue(List<MapBattle> Battles, Dictionary<int, List<int>> BattleArmies);
internal sealed record MapSelection(BattleMap? Map, MapPicture? Picture);

/// <summary>Reads map data without depending on WPF controls or the main window.</summary>
internal sealed class MapEditorController
{
    private static readonly IReadOnlyDictionary<int, List<int>> NoArmies =
        new Dictionary<int, List<int>>();
    private string? sourcePath;
    private MapCatalogue? catalogue;

    internal IReadOnlyDictionary<int, List<int>> BattleArmies => catalogue?.BattleArmies ?? NoArmies;

    internal bool IsCurrent(string path) => sourcePath == path && catalogue is { Battles.Count: > 0 };

    internal bool TryGetBattle(int battle, out MapBattle entry)
    {
        entry = catalogue?.Battles.FirstOrDefault(b => b.Battle == battle)!;
        return entry is not null;
    }

    internal void SetCatalogue(string path, MapCatalogue index)
    {
        sourcePath = path;
        catalogue = index;
    }

    internal void Reset()
    {
        sourcePath = null;
        catalogue = null;
    }

    internal Task<MapCatalogue> ReadCatalogueAsync(InspectionResult disc, EventLibrary library) => Task.Run(() =>
    {
        var executable = disc.ExecutableFile ?? throw new InvalidDataException("Game program not found.");
        byte[] data;
        using (var stream = new FileStream(disc.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var iso = new IsoImage(stream))
            data = iso.ReadRange(executable.Offset, (int)executable.Length, (int)executable.Length);

        lock (library)
        {
            var named = LiveGame.Battles(data, ArmyData.LoadBattleTitles(library.Archives));
            var order = named.Select(b => b.Battle).Distinct().ToList();
            var names = named.GroupBy(b => b.Battle).ToDictionary(g => g.Key, g => g.First().Text);
            var files = library.BattleMapFiles();
            var onDisc = BattleMaps.Files(library.Archives);
            var known = onDisc.Select(f => f.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var battles = files.Keys.Where(b => known.Contains(files[b]))
                .OrderBy(b => order.IndexOf(b) is int position && position >= 0 ? position : order.Count + b)
                .Select(b => new MapBattle(b,
                    names.TryGetValue(b, out var title) ? $"{title} (battle {b})" :
                    files[b].StartsWith("zmap", StringComparison.OrdinalIgnoreCase) ?
                    $"Title screen demo (battle {b})" : $"Battle {b}", files[b])).ToList();
            var used = files.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            battles.AddRange(onDisc.Where(f => !used.Contains(f.File))
                .Select(f => new MapBattle(-f.Top, $"Unused map {Path.GetFileNameWithoutExtension(f.File)}", f.File)));

            var armies = new Dictionary<int, List<int>>();
            foreach (var (army, armyBattles) in library.ArmyBattles())
                if (armyBattles.Count <= 2)
                    foreach (int battle in armyBattles)
                        (armies.TryGetValue(battle, out var list) ? list : armies[battle] = []).Add(army);
            return new MapCatalogue(battles, armies);
        }
    });

    internal Task<MapSelection> ReadMapAsync(EventLibrary library, MapBattle entry) => Task.Run(() =>
    {
        lock (library)
        {
            var map = entry.Battle < 0
                ? BattleMaps.Load(library.Archives, null, -entry.Battle, 0, entry.File)
                : BattleMaps.Load(library.Archives, library, entry.Battle, entry.File);
            // Unused maps do not have battle scripts, so the first water layer is used.
            var art = map is null ? null : BattleMaps.LoadPicture(library.Archives, map.Top,
                entry.Battle < 0 ? [4] : library.StartingLayers(entry.Battle));
            return new MapSelection(map, art);
        }
    });
}
