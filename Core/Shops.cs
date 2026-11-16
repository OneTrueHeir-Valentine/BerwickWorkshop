namespace Berwick.Core;

/// <summary>How a shop offers an item: always, from a stock that the game refills over time (Extra is the stock number), or as a
/// special purchase that runs an event when bought (Extra is the event).</summary>
public enum ShopSale { Always, Stock, Special }

/// <summary>An item a shop offers, with where its command sits in the shop's script: the place of the item number among the
/// script's numbers (for changing the item) and the path of its line (for removing it).</summary>
public sealed record ShopItem(int Item, ShopSale Sale, int Extra, int NumberIndex, IReadOnlyList<(int Arg, int Item)> Path);

/// <summary>A shop: the event whose script builds it, the battle and spot it stands on (for shops on battle maps), and its items.</summary>
public sealed record Shop(int Event, int Battle, string Spot, IReadOnlyList<ShopItem> Items);

/// <summary>
/// Shops are built by event scripts: MachiShopCreate(kind), then MachiShopAddItem(item), MachiShopAddItemStock(item, stock) or
/// MachiShopAddItemEvnet(item, event) for each item, then MachiShopOpen. The base's shops are placed by their shopkeepers
/// (SetPeopleEx with the shop's event); a shop on a battle map is reached from a spot whose event runs a town scene with
/// BMapTownExec(0, scene), where the shopkeeper is placed the same way.
/// </summary>
public static class Shops
{
    public const int Create = 0x49A, AddItem = 0x49B, AddItemSpecial = 0x49C, AddItemStock = 0x49D;
    private const int TownScene = 0x415, SpotEvent = 1;

    /// <summary>Every shop with at least one item. The script of an event comes from <paramref name="script"/> (the project's
    /// version), or the disc.</summary>
    public static IReadOnlyList<Shop> Read(EventLibrary library, Func<int, byte[]?>? script = null)
    {
        var placedBy = new Dictionary<int, List<int>>();
        var scenes = new Dictionary<int, List<int>>();
        int setPeople = (library.CommandNames ?? []).ToList().IndexOf("SetPeopleEx");
        var shops = new List<(int Event, IReadOnlyList<ShopItem> Items)>();
        foreach (int number in library.Numbers)
        {
            byte[] bytes;
            try { bytes = script?.Invoke(number) ?? library.Bytes(number); } catch (InvalidDataException) { continue; }
            IReadOnlyList<ScriptNode> nodes;
            try { nodes = ScriptParser.Parse(bytes); } catch (InvalidDataException) { continue; }
            foreach (var n in Walk(nodes))
            {
                if (n.Command == setPeople && n.Args.Count >= 2 && n.Args[1] is [{ Value: int shop }]) Add(placedBy, shop, number);
                if (n.Command == ScriptParser.RunEvent && n.Args.Count == 1 && n.Args[0] is [{ Value: int run }]) Add(placedBy, run, number);
                if (n.Command == TownScene && n.Args.Count >= 2 && n.Args[1] is [{ Value: int scene }]) Add(scenes, scene, number);
            }
            if (Walk(nodes).Any(n => n.Command == Create))
            {
                var items = Items(nodes);
                if (items.Count > 0) shops.Add((number, items));
            }
        }
        var spots = library.MapEvents().Where(r => r.Type == SpotEvent && r.Args.Length >= 4).GroupBy(r => r.Event).ToDictionary(g => g.Key, g => g.First());
        return shops.Select(s =>
        {
            // From the shop to the scene that places its shopkeeper, to the spot that runs the scene.
            foreach (int scene in placedBy.GetValueOrDefault(s.Event) ?? [])
                foreach (int spotEvent in scenes.GetValueOrDefault(scene) ?? [])
                    if (spots.TryGetValue(spotEvent, out var spot))
                        return new Shop(s.Event, spot.Battle, BattleMaps.SpotName(spot.Args[0]), s.Items);
            return new Shop(s.Event, 0, "", s.Items);
        }).ToList();
    }

    /// <summary>The items a shop's script adds, in order. Items given as anything but a plain number are left out.</summary>
    public static IReadOnlyList<ShopItem> Items(IReadOnlyList<ScriptNode> nodes)
    {
        var result = new List<ShopItem>();
        int numbers = 0;
        // The same order as ScriptValues.Numbers, so a number's place matches; lines found the way ScriptLines finds them.
        void Visit(ScriptNode n, List<(int, int)>? path)
        {
            if (n.Command == ScriptParser.Number) { numbers++; return; }
            if (path is not null && n.Command is AddItem or AddItemStock or AddItemSpecial && n.Args.Count >= 1 && n.Args[0] is [{ Command: ScriptParser.Number, Value: int item }])
            {
                int extra = n.Args.Count >= 2 && n.Args[1] is [{ Value: int value }] ? value : 0;
                result.Add(new(item, n.Command == AddItem ? ShopSale.Always : n.Command == AddItemStock ? ShopSale.Stock : ShopSale.Special, extra, numbers, path));
            }
            for (int a = 0; a < n.Args.Count; a++)
            {
                bool lines = n.Command == ScriptParser.Event && a == 0 || n.Command == ScriptParser.If && a is 1 or 2 || n.Command == ScriptParser.While && a == 1;
                for (int i = 0; i < n.Args[a].Count; i++)
                    Visit(n.Args[a][i], lines ? [.. (n.Command == ScriptParser.Event ? [] : path ?? []), (a, i)] : null);
            }
        }
        foreach (var n in nodes) Visit(n, null);
        return result;
    }

    private static void Add(Dictionary<int, List<int>> map, int key, int value) => (map.TryGetValue(key, out var list) ? list : map[key] = []).Add(value);

    private static IEnumerable<ScriptNode> Walk(IEnumerable<ScriptNode> nodes)
    {
        foreach (var n in nodes) { yield return n; foreach (var a in n.Args) foreach (var c in Walk(a)) yield return c; }
    }
}
