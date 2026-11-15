using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Berwick.Core;

/// <summary>A unit on the current map in the running game: where its record is in memory, and what it is.</summary>
public sealed record LiveUnit(uint Address, int NameId, string Name, int Army, int X, int Y, int Level, int ClassId, int Hp);

/// <summary>An item slot of a unit in the running game.</summary>
public sealed record LiveItem(int Slot, int ItemId, int Uses);

/// <summary>A battle a sortie from the base can load: a story mission or a quest, with the battle number the game gives it,
/// and its title where the disc has one.</summary>
public sealed record LiveBattle(string Name, int Battle, string Title = "")
{
    public string Text => Title.Length > 0 ? $"{Name}: {Title}" : Name;
}

/// <summary>
/// Reads and changes the running game in PCSX2 through PINE, a connection PCSX2 offers on this computer only
/// (127.0.0.1). Every word this class changes is remembered so all changes can be undone; nothing on the disc or in saves changes.
/// Undo puts a word back only while it still holds the value written here, and finds item slots again through the unit,
/// because the game moves item lists and reuses memory as it plays.
/// Unit records in memory have the disc's starting-record layout without its 4-byte tag, so the character fields apply
/// with their offsets shifted by 4.
/// </summary>
public sealed class LiveGame : IDisposable
{
    public const int DefaultPort = 28011;
    private const byte Read32 = 2, Read64 = 3, Write32 = 6, MsgId = 12, MsgStatus = 15;
    private const uint PoolStart = 0x01D00000, PoolEnd = 0x02000000, RecordLength = 0xB0, ItemList = 0x7C;
    private const int Tag = 4, ChunkLength = 0x1000;
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly Dictionary<uint, (uint Original, uint Written)> originals = [];
    private readonly Dictionary<(uint Unit, int Slot), (uint Original, uint Written)> itemEdits = [];

    private LiveGame(TcpClient client) { this.client = client; stream = client.GetStream(); }

    /// <summary>Connects to PCSX2 on this computer. Throws IOException when PCSX2 is not running or PINE is off.</summary>
    public static LiveGame Connect(int port = DefaultPort)
    {
        var client = new TcpClient { ReceiveTimeout = 3000, SendTimeout = 3000, NoDelay = true };
        try { client.Connect("127.0.0.1", port); }
        catch (SocketException) { client.Dispose(); throw new IOException("PCSX2 did not answer. Start the game in PCSX2 and turn on PINE in its settings."); }
        return new LiveGame(client);
    }

    public void Dispose() { stream.Dispose(); client.Dispose(); }

    public int ChangedWords => originals.Count + itemEdits.Count;

    /// <summary>The words changed through this connection, with the value each held first.</summary>
    public IReadOnlyDictionary<uint, uint> Originals => originals.ToDictionary(k => k.Key, k => k.Value.Original);

    private byte[] Call(byte[] payload)
    {
        var message = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(message, message.Length);
        payload.CopyTo(message, 4);
        try
        {
            stream.Write(message);
            var head = ReadExactly(4);
            var body = ReadExactly(BinaryPrimitives.ReadInt32LittleEndian(head) - 4);
            if (body.Length == 0 || body[0] != 0) throw new IOException("PCSX2 refused the request.");
            return body[1..];
        }
        catch (Exception e) when (e is SocketException || e is IOException { InnerException: SocketException })
        {
            throw new IOException("PCSX2 stopped answering. PCSX2 takes one connection at a time, so close any other tool connected to it, then connect again.");
        }
    }

    private byte[] ReadExactly(int length)
    {
        var buffer = new byte[length];
        int done = 0;
        while (done < length)
        {
            int n = stream.Read(buffer, done, length - done);
            if (n == 0) throw new IOException("The connection to PCSX2 was lost.");
            done += n;
        }
        return buffer;
    }

    private string ReadString(byte op)
    {
        var data = Call([op]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(data);
        return Encoding.UTF8.GetString(data, 4, Math.Max(0, Math.Min(length, data.Length - 4))).TrimEnd('\0');
    }

    /// <summary>The serial of the game PCSX2 is running, such as SLPS-25497.</summary>
    public string GameId() => ReadString(MsgId);

    /// <summary>True while the game is running (not paused or shut down).</summary>
    public bool Running() => BinaryPrimitives.ReadInt32LittleEndian(Call([MsgStatus])) == 0;

    /// <summary>Reads memory in batches of 64-bit reads.</summary>
    public byte[] ReadRange(uint address, int length)
    {
        var result = new byte[length];
        for (int at = 0; at < length; at += ChunkLength)
        {
            int n = Math.Min(ChunkLength, length - at) & ~7;
            var payload = new byte[n / 8 * 5];
            for (int i = 0; i < n / 8; i++)
            {
                payload[i * 5] = Read64;
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 5 + 1), (uint)(address + at + i * 8));
            }
            Call(payload).AsSpan(0, n).CopyTo(result.AsSpan(at));
        }
        return result;
    }

    public uint ReadWord(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(Call([Read32, .. BitConverter.GetBytes(address)]));

    private void Store(uint address, uint value)
    {
        var payload = new byte[9];
        payload[0] = Write32;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), address);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5), value);
        Call(payload);
    }

    /// <summary>Writes one word, remembering the word that was there first so it can be put back.</summary>
    public void WriteWord(uint address, uint value)
    {
        if (address % 4 != 0) throw new ArgumentException("Words are written at multiples of 4.");
        uint original = originals.TryGetValue(address, out var known) ? known.Original : ReadWord(address);
        Store(address, value);
        originals[address] = (original, value);
    }

    /// <summary>
    /// Puts back what was changed through this connection. A word the game has changed since is left as it is, and so is an
    /// item slot whose contents changed; returns how many changes were left that way.
    /// </summary>
    public int UndoAll()
    {
        int left = 0;
        foreach (var (address, (original, written)) in originals.ToList())
        {
            if (ReadWord(address) == written) Store(address, original); else left++;
        }
        foreach (var ((unit, slot), (original, written)) in itemEdits.ToList())
        {
            uint list = ReadWord(unit + ItemList);
            if (list < 0x00100000 || list >= PoolEnd) { left++; continue; }
            uint at = list + 4 + (uint)slot * 8;
            if (ReadWord(at) == written) Store(at, original); else left++;
        }
        originals.Clear();
        itemEdits.Clear();
        return left;
    }

    // Words where the Japanese and English programs differ; the game never changes them while it runs.
    private static readonly uint[] EditionChecks =
    [
        0x0041BE54, 0x0041C0C4, 0x004568B4, 0x004595FC, 0x00459870, 0x004AB07C, 0x004AB820, 0x004ABB6C, 0x004ABE4C, 0x004AC9A0, 0x004ACC10, 0x004ACE84,
        0x004AD0F8, 0x004AD6F0, 0x004AD960, 0x004ADBD4, 0x004ADE48, 0x004AE0BC, 0x004AE330, 0x004AF744, 0x004B04C8, 0x004B24F4, 0x004B28A8, 0x004B4250,
    ];

    /// <summary>Whether the running program is the one on the opened disc, judged by words where the two editions differ.</summary>
    public bool MatchesProgram(ReadOnlySpan<byte> executable)
    {
        foreach (uint address in EditionChecks)
        {
            long at = address - GameRules.ProgramToFile;
            if (at < 0 || at + 4 > executable.Length) return false;
            if (ReadWord(address) != BinaryPrimitives.ReadUInt32LittleEndian(executable[(int)at..])) return false;
        }
        return true;
    }

    /// <summary>Units on the current map: records in the unit pool whose battle data gives an army (0 is the player's) and a position.</summary>
    public IReadOnlyList<LiveUnit> Units(IReadOnlyDictionary<int, string> names)
    {
        var pool = ReadRange(PoolStart, (int)(PoolEnd - PoolStart));
        uint U32(uint a) => BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan((int)(a - PoolStart)));
        var units = new List<LiveUnit>();
        for (uint a = PoolStart; a < PoolEnd - 0x100; a += 4)
        {
            int o = (int)(a - PoolStart);
            int id = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(o + 8)), id2 = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(o + 10));
            if (id != id2 || id is <= 0 or >= 1600) continue;
            uint battle = U32(a + 0xA8);
            if (battle < PoolStart || battle >= PoolEnd - 12) continue;
            uint army = U32(battle), x = U32(battle + 4), y = U32(battle + 8);
            if (army > 3 || x > 64 || y > 64) continue;
            int level = pool[o + 0x10] & 0x3F;
            if (level is < 1 or > 63) continue;
            int hp = (BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(o + 0x10)) & 0x1FFF) >> 6;
            units.Add(new(a, id, names.TryGetValue(id, out var n) && n.Length > 0 ? n : $"Unit {id}", (int)army, (int)x, (int)y, level, pool[o + 0xF], hp));
        }
        // The game keeps short-lived copies of a unit (for a forecast, for example); the first record of each unit at a position is the live one.
        return units.GroupBy(u => (u.NameId, u.Army, u.X, u.Y)).Select(g => g.First()).OrderBy(u => u.Army).ThenBy(u => u.Address).ToList();
    }

    /// <summary>A unit's record in the starting-record layout (with 4 empty bytes where the tag would be), for the character fields.</summary>
    public byte[] ReadRecord(LiveUnit unit)
    {
        var record = new byte[CharacterDecoder.RecordLength];
        ReadRange(unit.Address, (int)RecordLength).CopyTo(record, Tag);
        return record;
    }

    /// <summary>Writes the parts of a record that differ from what the game holds now (only within the unit's own data).</summary>
    public int WriteRecord(LiveUnit unit, byte[] edited)
    {
        var current = ReadRecord(unit);
        int words = 0;
        for (int i = Tag; i < Tag + RecordLength; i += 4)
        {
            if (edited.AsSpan(i, 4).SequenceEqual(current.AsSpan(i, 4))) continue;
            WriteWord(unit.Address + (uint)(i - Tag), BinaryPrimitives.ReadUInt32LittleEndian(edited.AsSpan(i)));
            words++;
        }
        return words;
    }

    public IReadOnlyList<LiveItem> Items(LiveUnit unit)
    {
        uint list = ReadWord(unit.Address + ItemList);
        if (list < 0x00100000 || list >= PoolEnd) return [];
        var data = ReadRange(list, 72);
        var items = new List<LiveItem>();
        for (int slot = 0; slot < 8; slot++)
        {
            uint w = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + slot * 8));
            items.Add(new(slot, (int)(w & 0x3FF), (int)(w >> 20 & 0xFF)));
        }
        return items;
    }

    /// <summary>Puts an item (0 for none) with its uses in a slot.</summary>
    public void SetItem(LiveUnit unit, int slot, int itemId, int uses)
    {
        if (slot is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(slot));
        uint list = ReadWord(unit.Address + ItemList);
        if (list < 0x00100000 || list >= PoolEnd) throw new IOException("This unit's items could not be found.");
        uint at = list + 4 + (uint)slot * 8;
        uint current = ReadWord(at);
        uint w = (current & ~0x3FFu & ~0x0FF00000u) | ((uint)itemId & 0x3FF) | ((uint)Math.Clamp(uses, 0, 255) << 20);
        uint original = itemEdits.TryGetValue((unit.Address, slot), out var known) && known.Written == current ? known.Original : current;
        Store(at, w);
        itemEdits[(unit.Address, slot)] = (original, w);
    }

    // Story missions (16-byte records) and quests (52-byte records). Bits 12 to 23 of a record's first word are the battle its sortie
    // loads, and the battle's own data then names the map, units and events; quests that are not battles have 0. A mission record's
    // low byte is its number and a quest record's second word is 1. The addresses are the same on both editions.
    private const uint MissionTable = 0x0046D8F0, QuestTable = 0x0046D9E0;
    private const int MissionCount = 15, MissionLength = 16, QuestCount = 218, QuestLength = 52;

    private static List<(uint Address, string Name, uint Word, string Entry)>? SortieRecords(ReadOnlySpan<byte> missions, ReadOnlySpan<byte> quests)
    {
        var records = new List<(uint, string, uint, string)>();
        for (int k = 0; k < MissionCount; k++)
        {
            uint w = BinaryPrimitives.ReadUInt32LittleEndian(missions[(k * MissionLength)..]);
            if ((w & 0xFF) != k + 1) return null;
            records.Add((MissionTable + (uint)(k * MissionLength), $"Story mission {k + 1}", w, $"mission{k + 1:D3}"));
        }
        for (int k = 0; k < QuestCount; k++)
        {
            var record = quests[(k * QuestLength)..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 1) return null;
            records.Add((QuestTable + (uint)(k * QuestLength), $"Quest {k + 1}", BinaryPrimitives.ReadUInt32LittleEndian(record), $"quest{k + 1:D3}"));
        }
        return records;
    }

    private static int BattleOf(uint word) => (int)(word >> 12 & 0xFFF);

    /// <summary>
    /// The story missions and quest battles in a program, in the game's order, or none when the lists are not where expected.
    /// Titles come from <see cref="ArmyData.LoadBattleTitles"/>, by entry name.
    /// </summary>
    public static IReadOnlyList<LiveBattle> Battles(ReadOnlySpan<byte> executable, IReadOnlyDictionary<string, string>? titles = null)
    {
        long missions = MissionTable - GameRules.ProgramToFile, quests = QuestTable - GameRules.ProgramToFile;
        if (missions < 0 || quests + QuestCount * QuestLength > executable.Length) return [];
        var records = SortieRecords(executable.Slice((int)missions, MissionCount * MissionLength), executable.Slice((int)quests, QuestCount * QuestLength));
        return records is null ? [] : records.Where(r => BattleOf(r.Word) != 0)
            .Select(r => new LiveBattle(r.Name, BattleOf(r.Word), titles is not null && titles.TryGetValue(r.Entry, out var t) ? t : "")).ToList();
    }

    private List<(uint Address, string Name, uint Word)> LiveSortieRecords()
    {
        var missions = ReadRange(MissionTable, MissionCount * MissionLength);
        var quests = ReadRange(QuestTable, QuestCount * QuestLength);
        var records = SortieRecords(missions, quests) ?? throw new IOException("The game's list of missions is not where Berwick Workshop expects it, so it was left alone.");
        // A record already changed through this connection keeps its own battle in the remembered original.
        return records.Select(r => (r.Address, r.Name, originals.TryGetValue(r.Address, out var known) ? known.Original : r.Word)).ToList();
    }

    /// <summary>
    /// Makes every story mission and quest battle load the given battle when its sortie starts, until the sorties are put back.
    /// Takes effect at the next sortie; a battle already on the map is not changed. Returns how many sorties now load it.
    /// </summary>
    public int SendSorties(int battle)
    {
        if (battle is <= 0 or > 0xFFF) throw new ArgumentOutOfRangeException(nameof(battle));
        int count = 0;
        foreach (var (address, _, word) in LiveSortieRecords())
        {
            if (BattleOf(word) == 0) continue;
            uint target = (word & ~(0xFFFu << 12)) | ((uint)battle << 12);
            if (ReadWord(address) != target) WriteWord(address, target);
            count++;
        }
        return count;
    }

    /// <summary>Gives every sortie its own battle again. Returns how many records were left because the game changed them since.</summary>
    public int RestoreSorties()
    {
        int left = 0;
        foreach (var address in originals.Keys.Where(IsSortieRecord).ToList())
        {
            var (original, written) = originals[address];
            if (ReadWord(address) == written) Store(address, original); else left++;
            originals.Remove(address);
        }
        return left;
    }

    /// <summary>Whether any sortie loads a battle other than its own because of this connection.</summary>
    public bool SortiesChanged => originals.Keys.Any(IsSortieRecord);

    private static bool IsSortieRecord(uint address) =>
        address >= MissionTable && address < MissionTable + MissionCount * MissionLength && (address - MissionTable) % MissionLength == 0 ||
        address >= QuestTable && address < QuestTable + QuestCount * QuestLength && (address - QuestTable) % QuestLength == 0;

    /// <summary>Writes a rule's instructions for a setting, after checking that each site holds an instruction the rule knows.</summary>
    public bool WriteRule(GameRule rule, int setting)
    {
        var current = rule.Sites.Select(s => ReadWord(s.Address)).ToArray();
        if (rule.Decode(current) is null) return false;
        var words = rule.Encode(setting);
        for (int i = 0; i < words.Length; i++)
            if (current[i] != words[i]) WriteWord(rule.Sites[i].Address, words[i]);
        return true;
    }
}
