namespace Berwick.Core;

/// <summary>
/// The stable's horses for each chapter, chosen by event 138 (town_add_hourse) from stock slots 0 to 10 (event 603 maps
/// slots to horses). The Plain Horse is always sold; the Spry and Tough Horses in odd chapters; the Strong Horse at a chance;
/// at another chance one of six rare horses; Thin Horses fill the rest up to the total. Each kind counts once toward the
/// total, whatever its stock. Numbers are places in the event script, in reading order (see ScriptValues.Numbers).
/// </summary>
public static class StableStock
{
    public const int Event = 138;
    public enum Kind { Stock, Chance, Total }
    public sealed record Number(int Index, int Original, string Label, Kind Kind, int Item = 0)
    {
        public int Min => Kind == Kind.Total ? 1 : 0;
        public int Max => Kind switch { Kind.Chance => 100, Kind.Total => 30, _ => 9 };
    }

    public static IReadOnlyList<Number> Numbers { get; } =
    [
        new(28, 1, "Plain Horse, sold every chapter", Kind.Stock, 0x206),
        new(33, 1, "Spry Horse, on chosen chapters", Kind.Stock, 0x207),
        new(35, 1, "Tough Horse, on chosen chapters", Kind.Stock, 0x208),
        new(37, 25, "Chance of a Strong Horse (%)", Kind.Chance),
        new(39, 1, "Strong Horse", Kind.Stock, 0x209),
        new(41, 20, "Chance of one rare horse (%)", Kind.Chance),
        new(46, 1, "Ishs Stallion (rare)", Kind.Stock, 0x20A),
        new(49, 1, "Sinon Stallion (rare)", Kind.Stock, 0x20B),
        new(52, 1, "Liga Destrier (rare)", Kind.Stock, 0x20C),
        new(55, 1, "Liga Courser (rare)", Kind.Stock, 0x20D),
        new(58, 1, "Morach Horse (rare)", Kind.Stock, 0x20E),
        new(61, 1, "Ishs Courser (rare)", Kind.Stock, 0x20F),
        new(63, 6, "Horses in all; Thin Horses fill the rest", Kind.Total),
    ];

    /// <summary>The chapter test for the Spry and Tough Horses: chapter mod divisor equals remainder.</summary>
    public const int DivisorIndex = 30, RemainderIndex = 31;
    public static readonly string[] ChapterChoices = ["Odd chapters", "Even chapters", "Every chapter", "Never"];
    public static (int Divisor, int Remainder) ChapterWords(int choice) => choice switch
    {
        0 => (2, 1), 1 => (2, 0), 2 => (1, 0), 3 => (1, 1),
        _ => throw new EditException("Choose when the Spry and Tough Horses are sold."),
    };
    public static int? ChapterChoice(int divisor, int remainder) => (divisor, remainder) switch
    {
        (2, 1) => 0, (2, 0) => 1, (1, 0) => 2, (1, 1) => 3, _ => null,
    };

    /// <summary>True when the event's numbers are the ones this description expects.</summary>
    public static bool Matches(IReadOnlyList<ScriptNumber> numbers) =>
        numbers.Count > 63 && Numbers.All(n => numbers[n.Index].Value == n.Original)
        && numbers[DivisorIndex].Value == 2 && numbers[RemainderIndex].Value == 1;
}
