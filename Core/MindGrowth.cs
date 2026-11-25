namespace Berwick.Core;

/// <summary>
/// A character's personal Mind growth is stored at twice its value. On level-up the game reads the
/// whole byte at offset 5 of the 32-byte growth record and halves it, rounding down, so a stored 30
/// gives a 15% growth. The other growth rates are used as stored, and so is the class Mind growth.
/// Only the low seven bits of the byte are known to belong to Mind growth, so editable values run
/// from 0% to 63%. A record with the top bit set would be read differently by the game and is left
/// as it is; its other fields can still be edited.
/// </summary>
public static class MindGrowth
{
    public const string Field = "mindGrowth";
    public const int MaxPercent = 63;
    private const int StoredByte = 5;
    private const byte UnknownBit = 0x80;

    /// <summary>The growth rate the game uses for a stored value.</summary>
    public static int Percent(int stored) => stored / 2;

    /// <summary>The stored value for a percentage. When the percentage is the one already in effect,
    /// the current stored value is kept, including an odd value the game also reads.</summary>
    public static int Stored(int percent, int currentStored)
    {
        if (percent is < 0 or > MaxPercent)
            throw new EditException($"Mind growth must be between 0% and {MaxPercent}%.");
        return Percent(currentStored) == percent ? currentStored : percent * 2;
    }

    /// <summary>Whether a growth record uses the top bit of the Mind growth byte.</summary>
    public static bool HasUnsupportedEncoding(ReadOnlySpan<byte> growthRecord) =>
        growthRecord.Length > StoredByte && (growthRecord[StoredByte] & UnknownBit) != 0;

    public static string UnsupportedEncodingMessage(string character) =>
        $"{character}'s Mind growth uses a stored form the editor does not support, so it cannot be changed. Other growth rates can still be edited.";
}
