namespace Berwick.Core;

/// <summary>
/// Static SOURCE OBSERVATIONS of the original executable quest table and archived event calls.
/// The quest hashes and their index resolution were checked in English v2.3 and Japanese retail;
/// scene bodies and all gameplay conditions are NOT independently accepted on both editions.
/// Separate from PromotionCatalogue's historical town-entry research and ALL writers.
/// </summary>
public sealed record NativePromotionRoute(
    int UnitId, int QuestId, int CheckEvent, int? ApplicationEvent, int? IntroEvent,
    string CheckHash, string ApplicationHash, int ConversionEvent,
    string SceneSetup, IReadOnlyList<int> SceneEvents, IReadOnlyList<int> RelatedItemEvents,
    string ResearchNote)
{
    public (int Event, int ItemId)? KnownItemRequest { get; init; }
    public bool ResolvedApplication => ApplicationEvent is not null;
    public string ApplicationText => ApplicationEvent is int e ? $"Event {e}" : "Unresolved native application hash";
}

public static class NativePromotionRoutes
{
    private static readonly IReadOnlyList<NativePromotionRoute> Original = Array.AsReadOnly(new NativePromotionRoute[]
    {
        new(132, 158, 364, null, 876,
            "0x604FDD58", "0x7057955C", 876,
            "(branching within introduction event 876)", new[] { 1430, 1434, 1436, 1438 }, new[] { 876 },
            "Quest 158 intro hash resolves to 876 and includes unit 132/class 121, helper 188, item 256, town battle and flag operations. The distinct native application hash 0x7057955C does not resolve via the indexed event hashes; DO NOT call intro876 the verified quest-clear pointer."),
        new(83, 162, 368, 163, null,
            "0x21B6AB88", "0x31AEE38C", 163,
            "branching event 165", new[] { 1362, 1364, 1366, 1534 }, Array.Empty<int>(),
            "Application 163 calls conversion helper 188, favor/event 164, then branching event 165 with alternate scenes; do not imply a single town root."),
        new(85, 163, 369, 166, null,
            "0xA70CDAE9", "0xB71492ED", 166,
            "1345", new[] { 1346 }, Array.Empty<int>(),
            "Application 166 calls helper 188 BEFORE town setup."),
        new(87, 164, 370, 167, null,
            "0x381751CE", "0x280F19CA", 167,
            "1353", new[] { 1354 }, new[] { 168 },
            "Application 167 calls helper 188 BEFORE 1353; ceremony 1354 explicitly calls item grant 168 for item 106.") { KnownItemRequest = (168, 106) },
        new(88, 165, 371, 169, null,
            "0xFF3EA17F", "0xEF26E97B", 169,
            "1367", new[] { 1368 }, new[] { 170 },
            "Application 169 calls helper 188 BEFORE 1367; ceremony 1368 explicitly calls item grant 170 for item 106.") { KnownItemRequest = (170, 106) },
        new(89, 166, 372, 171, null,
            "0x036F6640", "0x13772E44", 171,
            "1387", new[] { 1388 }, new[] { 172 },
            "Application 171 calls TeamUnitDelSkill before scene; event 172 checks horse items 517/518/519 and assigns item 527 under a condition. It is not a generic grant."),
        new(124, 167, 373, 173, null,
            "0xA5D41129", "0xB5CC592D", 173,
            "1357", new[] { 1358 }, Array.Empty<int>(),
            "Application 173 also calls helper 240; its effect not fully classified."),
        new(97, 168, 374, 174, null,
            "0x63D39558", "0x73CBDD5C", 174,
            "1341", new[] { 1342 }, Array.Empty<int>(),
            "Application 174 calls helper188 before town scene."),
        new(134, 169, 375, 175, null,
            "0xA9F913EA", "0xB9E15BEE", 175,
            "1383", new[] { 1384 }, Array.Empty<int>(),
            "Application 175 also calls helper 240, Ruby story flags may be involved."),
        new(96, 170, 376, 176, null,
            "0xEFCE0C7B", "0xFFD6447F", 176,
            "1337", new[] { 1338 }, Array.Empty<int>(),
            "Application 176 calls helper 188 before town scene."),
        new(131, 171, 377, 178, null,
            "0xB7FEE3AD", "0xA7E6ABA9", 178,
            "1349", new[] { 1350 }, new[] { 177 },
            "Ceremony event1350 calls item request177, whose item2int(303) travels through delivery helper129.") { KnownItemRequest = (177, 303) },
        new(149, 172, 378, 181, null,
            "0x5EBE8CD7", "0x4EA6C4D3", 182,
            "1339", new[] { 1340 }, Array.Empty<int>(),
            "Quest application181 calls town setup1339; ceremony1340 calls event182, where unit149/class81 calls helper188. Conversion is reached DURING/AFTER presentation, unlike Adel."),
        new(109, 173, 379, 183, null,
            "0x9A0CA926", "0x8A14E122", 184,
            "1379 or 1381", new[] { 1380, 1382 }, Array.Empty<int>(),
            "Quest application183 branches through distinct scene setups1379/1381; both ceremonies call event184, where unit109/class154 calls helper188. Other direct call229 is not itself a confirmed conversion."),
        new(90, 174, 380, 185, null,
            "0xDEB36137", "0xCEAB2933", 186,
            "1371 or 1373", new[] { 1372, 1374 }, Array.Empty<int>(),
            "Quest application185 conditionally branches to setups1371/1373; both scenes call event186, where unit90/class227 calls helper188."),
        new(127, 198, 401, 179, null,
            "0x3CFFA90F", "0x2CE7E10B", 179,
            "1333", new[] { 1334 }, Array.Empty<int>(),
            "Application179 calls helper188 before town scene."),
        new(113, 199, 402, 180, null,
            "0xDF1A15F7", "0xCF025DF3", 180,
            "1375", new[] { 1376 }, Array.Empty<int>(),
            "Application180 calls helper188 before town scene."),
    });

    /// <summary>Only invoke from a validated ORIGINAL-DISC view. The table is not a live or edited-source diagnosis.</summary>
    public static IReadOnlyList<NativePromotionRoute> All => Original;
    public static NativePromotionRoute? ForUnit(int unitId) => Original.FirstOrDefault(r => r.UnitId == unitId);
}
