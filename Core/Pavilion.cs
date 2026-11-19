namespace Berwick.Core;

/// <summary>
/// The Kingfisher Pavilion (facility 16, town map 18). It serves while event 648 says so (in the game, while a mission is
/// chosen): event 667 seats units from the army at eight seats, and talking to a seated unit runs event 655, which orders
/// from a menu (event 649) and eats (GUnitEatFood). Each unit eats once a chapter. The seats are kept in status values
/// 80 to 95 until the game clears them. The server (person 344) runs event 658, which runs 659 when no quest is waiting.
/// "Any unit can order" replaces event 659: while the Pavilion serves, the server opens the unit picker the game's own
/// formation screen uses (SelectTeamUnit), and the chosen unit orders and eats as a seated unit would. Both editions
/// share these events.
/// </summary>
public static class Pavilion
{
    public const int ServerEvent = 659;

    public const string AnyUnitScript = """
        event (5 local values)
            TownMessagePopupPos(0, 344)
            v0 = run event 648
            if v0
                v1 = MakeFacilityMessageId(16, 3)
                TownMessage(1, v1)
                v2 = GetTeamAt(1, 0)
                v0 = SelectTeamUnit(v2, 0, 1)
                if v0
                    v1 = GUnitIsEatFood(v0)
                    if (v1 != 0)
                        v-1 = v0
                        run event 653
                    else
                        TownMessageFace(2, 344)
                        TownMessagePopupPos(2, 344)
                        SetMessageUnitValueBank(0, v0)
                        v2 = MakeFacilityMessageId(16, 4)
                        TownMessage(1, v2)
                        CreateGoldWindow(1, 300, 360)
                        v1 = run event 649
                        if (v1 != 0)
                            v2 = MakeFacilityMessageId(16, 8)
                            TownMessageNoFast(1, v2)
                            CloseGoldWindow()
                            v3 = GUnitEatFood(v0, v1)
                            TownMessageFace(0, v0)
                            v-1 = v0
                            if (v3 == 0)
                                run event 650
                            else
                            if (v3 == 1)
                                run event 651
                            else
                            if (v3 == 2)
                                run event 652
                            else
                            TownDispFoodEffect(v0, v1, v3)
                            wait 60 frames
                            SkipEnabled(0)
                            ShowPeopleStatus(v0)
                            wait 30 frames
                            SkipEnabled(1)
                        else
                            CloseGoldWindow()
                else
            else
                v1 = MakeFacilityMessageId(16, 10)
                TownMessage(1, v1)
        """;

    /// <summary>The server event that lets any unit order, compiled with the disc's command names.</summary>
    public static byte[] AnyUnitServer(IReadOnlyList<string> names) => ScriptWriter.Write(ScriptCompiler.Compile(AnyUnitScript, names));

    /// <summary>True when the server event is the "any unit can order" version.</summary>
    public static bool IsAnyUnit(ReadOnlySpan<byte> script, IReadOnlyList<string> names) => script.SequenceEqual(AnyUnitServer(names));
}
