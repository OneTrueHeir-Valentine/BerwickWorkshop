namespace Berwick.Core;

/// <summary>A requirement observed in an exact original image, not a current-game eligibility result.</summary>
public sealed record PromotionCondition(string Scope, string Expression);

/// <summary>
/// Static promotion facts tied to one verified original edition.
/// Entries do not describe edited project scripts or the live state of a running game.
/// </summary>
public sealed record PromotionPath(
    int UnitId, string CharacterName, int DestinationClassId, int CallerEvent, int MinimumLevel,
    int? QuestId, int? CheckEvent, int? ApplicationEvent, int? TownRoot,
    IReadOnlyList<PromotionCondition> KnownConditions, IReadOnlyList<string> KnownConsequences,
    IReadOnlyList<string> UnresolvedConditions, IReadOnlyList<int> ConfirmedDestinationUsers)
{
    public string UnitText => $"0x{UnitId:X3}";
    public string DestinationText => $"Class 0x{DestinationClassId:X2}";
    public string MinimumLevelText => $"Level {MinimumLevel}";
    public string QuestText => QuestId is int id ? $"Quest {id}" : "Not traced";
    public string RootText => TownRoot is int id ? $"Event {id}" : "Not traced";
    public bool? LiveEligible => null;
    public bool? LiveAvailable => null;
    public bool WriteAllowed => false;
    public bool CallerSetComplete => false;
}

/// <summary>Fail-closed, immutable results of a source-identified static catalogue lookup.</summary>
public sealed record PromotionCoverageGap(string Category, string Description);

public sealed record PromotionCatalogueResult(
    bool OriginalSourceVerified, string SourceDescription, IReadOnlyList<PromotionPath> Paths,
    IReadOnlyList<PromotionCoverageGap> CoverageGaps)
{
    public bool HasLiveSnapshot => false;
    public bool WriteAllowed => false;
}

public static class PromotionCatalogue
{
    // Literal calls to the original eligibility helper; additional computed and inherited callers
    // remain outside this finite index. Numeric IDs come from the verified original script catalogue,
    // not from translated character names or from a live game snapshot.
    private static readonly (int Unit, int Target, int Minimum, int Caller)[] Callers =
    [
        (132,121,1,364), (83,4,1,368), (85,160,10,369), (87,28,10,370),
        (88,28,10,371), (89,226,15,372), (124,24,15,373), (97,164,15,374),
        (134,37,10,375), (96,160,10,376), (131,114,10,377), (149,81,15,378),
        (109,154,15,379), (90,227,15,380), (127,119,15,401), (113,118,15,402)
    ];

    /// <summary>Destination-wide test in original event 237; no validation of arbitrary target classes is implied.</summary>
    private static string SharedRequirement(int destination) => destination switch
    {
        37 => "(Sword >= 20 AND Spear >= 20 AND Small Shield >= 20)",
        28 => "(Spear >= 30 AND Small Shield >= 20)",
        24 => "(Bow >= 20 AND Crossbow >= 20)",
        164 => "(Axe >= 30 AND Small Shield >= 20 AND Medium Shield >= 10)",
        160 => "IF current class 231 THEN (Sword >= 20 AND Small Shield >= 20 AND Medium Shield >= 10); ELSE (Sword >= 20 AND Spear >= 20 AND Small Shield >= 20), including current class 158",
        121 => "Effective derived Mind >= 5",
        226 or 227 => "Bow >= 30",
        114 => "Holy >= 30",
        81 => "Sword >= 30",
        154 => "Axe >= 30",
        118 or 119 => "(Fire >= 30 OR Thunder >= 30 OR Wind >= 30)",
        4 or 85 => "No additional proficiency test in the examined event-237 branch",
        _ => "Destination branch unverified; helper default success is not proof this class is a valid promotion"
    };

    // Confirmed gaps in the finite literal-caller index. They are not invented promotion
    // paths or counted as verified eligible characters.
    private static readonly IReadOnlyList<PromotionCoverageGap> KnownGaps =
        Array.AsReadOnly(new[]
        {
            new PromotionCoverageGap("Inherited script routes",
                "Wrapper event 187 and its inherited callers have not been resolved into a complete set of distinct promotion paths."),
            new PromotionCoverageGap("Computed or native calls",
                "Computed event references and native dispatch can reach promotion logic without a literal caller in this catalogue."),
            new PromotionCoverageGap("Scripted and historical cases",
                "Story-only and historical class changes may bypass the ordinary eligibility helper; their complete pathways are not verified here.")
        });

    public static PromotionCatalogueResult Read(InspectionResult? disc)
    {
        if (disc is null) return Unavailable("Open a supported original disc to inspect promotions.");
        return Read(disc.Profile, disc.Sha256, disc.ExecutableSha256, disc.IsModified);
    }

    /// <summary>
    /// Only the two exact original image and executable identities are recognized.
    /// Modified or unrecognized copies cannot silently inherit unverified promotion conditions.
    /// </summary>
    public static PromotionCatalogueResult Read(GameProfile? profile, string imageHash, string executableHash, bool modified)
    {
        var recognized = Profiles.Match(imageHash);
        if (modified || profile is null || recognized is null
            || !string.Equals(recognized.Sha256, profile.Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(executableHash, recognized.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            return Unavailable("Original promotion conditions are not verified for this disc. Open an unmodified Japanese retail or English v2.3 image.");

        var roster = CharacterDecoder.LoadRoster().ToDictionary(c => c.Id, c => c.Name);
        var users = Callers.GroupBy(c => c.Target).ToDictionary(
            g => g.Key, g => (IReadOnlyList<int>)Array.AsReadOnly(g.Select(c => c.Unit).Distinct().Order().ToArray()));
        var paths = new List<PromotionPath>(Callers.Length);
        foreach (var (unit, destination, minimum, caller) in Callers)
        {
            var facts = new List<PromotionCondition>
            {
                new("Caller, event 236", $"Level >= {minimum} AND member of primary team AND current class != destination class"),
                new("Mercenary gate, event 236",
                    "(Not from mercenaries) OR (original mercenary unit AND not currently mercenary AND mercenary-level-up) OR (unit is one of 149, 109, 90, 129, 113)"),
                new("Source-class gate, event 236", "Required source class 0 is a wildcard; otherwise current class must match the required class"),
                new("Shared destination, event 237", SharedRequirement(destination)),
                new("Proficiency semantics", "Weapon and shield comparisons use effective capped whole points, not raw tenths")
            };
            var consequences = new List<string>();
            var unknown = new List<string>
            {
                "The current unit level, class, flags, team and quest availability are not available from an ISO.",
                "The required source-class literal has not been independently reproduced for this caller in this catalogue.",
                "Other native or computed callers may exist; the destination user set is not complete.",
                "Whether the restored QUEST list was refreshed at the current moment is unknown."
            };
            int? quest = null, check = null, application = null, root = null;
            switch (unit)
            {
                case 134: // Ruby
                    (quest,check,application,root) = (169,375,175,953);
                    facts.Add(new("Story", "Event flag 214 required by the original check"));
                    facts.Add(new("Quest", "Campaign counter T from 1 through 12; phase and quest-history filters also apply"));
                    facts.Add(new("Offer order", "Other candidates can take priority before quest 169 is applied"));
                    consequences.Add("Ruby's event clears flags 236 and 369 and records status key 207; Clifford-related quest 121 reads related state.");
                    consequences.Add("An alternative town route can introduce Clifford's recruitment without being the same as Ruby's class-change operation.");
                    unknown.Add("Complete live town scheduling, optional events and reload refresh timing have not been accepted.");
                    unknown.Remove("The required source-class literal has not been independently reproduced for this caller in this catalogue.");
                    facts.Add(new("Source-class literal", "Original eligibility helper uses wildcard required-source-class 0"));
                    break;
                case 83: // Reese
                    (quest,check,application,root) = (162,368,163,1083);
                    facts.Add(new("Story", "Quest 84 is not offered AND (ScoreGet at least 18 OR campaign counter T at least 9)"));
                    facts.Add(new("Quest", "Normal executable quest window T from 1 through 9; phase and quest-history filters also apply"));
                    consequences.Add("The class-change event adjusts team mercenary favor and branches into Larentia's recruitment sequence.");
                    unknown.Add("Additional destination-class eligibility and the complete normal-entry sequence are not a live result.");
                    unknown.Remove("The required source-class literal has not been independently reproduced for this caller in this catalogue.");
                    facts.Add(new("Source-class literal", "Original eligibility helper uses wildcard required-source-class 0"));
                    break;
                case 132: // Enid
                    (quest,check,application,root) = (158,364,876,1129);
                    facts.Add(new("Story", "Enid in primary team AND Perceval in primary or secondary team AND flag 296 unset AND flag 297 set"));
                    facts.Add(new("Quest", "Campaign counter T from 7 through 12; internal phase filter 1 and quest history also apply"));
                    facts.Add(new("Offer order", "Quest 140 can take priority before Enid's promotion offer"));
                    consequences.Add("Original event includes a town battle, item 256 delivery and flag 297 changes, with downstream Derrick-related conditions.");
                    unknown.Add("Town battle, item recipient, alternate dispatch and reload effects are not fully accepted.");
                    unknown.Remove("The required source-class literal has not been independently reproduced for this caller in this catalogue.");
                    facts.Add(new("Source-class literal", "Original eligibility helper uses wildcard required-source-class 0"));
                    break;
                default:
                    unknown.Add("Caller-specific story/quest gates and the complete application route are not yet catalogued.");
                    unknown.Add("The complete original application event and town entry have not been confirmed for this caller.");
                    break;
            }
            var name = roster.TryGetValue(unit, out var displayed) ? displayed : $"Unit 0x{unit:X3}";
            paths.Add(new(unit, name, destination, caller, minimum, quest, check, application, root,
                facts.AsReadOnly(), consequences.AsReadOnly(), unknown.AsReadOnly(), users[destination]));
        }
        return new(true,
            $"Static conditions for verified {recognized.Name}. Live eligibility is unknown; project edits are not included.",
            paths.OrderBy(p => p.CharacterName, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(), KnownGaps);
    }

    private static PromotionCatalogueResult Unavailable(string reason) =>
        new(false, reason, Array.Empty<PromotionPath>(), Array.Empty<PromotionCoverageGap>());
}
