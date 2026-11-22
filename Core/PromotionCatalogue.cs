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
public sealed record PromotionCatalogueResult(
    bool OriginalSourceVerified, string SourceDescription, IReadOnlyList<PromotionPath> Paths)
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
                new("Caller", $"Level at least {minimum} (input to event 236)"),
                new("Caller", "Current class must differ from destination; unit/team checks also apply")
            };
            var consequences = new List<string>();
            var unknown = new List<string>
            {
                "The current unit level, class, flags, team and quest availability are not available from an ISO.",
                "Other native or computed callers may exist; the destination user set is not complete."
            };
            int? quest = null, check = null, application = null, root = null;
            switch (unit)
            {
                case 134: // Ruby
                    (quest,check,application,root) = (169,375,175,953);
                    facts.Add(new("Shared destination", "Sword at least 20 AND Spear at least 20 AND Small Shield at least 20"));
                    facts.Add(new("Story", "Event flag 214 required by the original check"));
                    facts.Add(new("Quest", "Campaign counter T from 1 through 12; phase and quest-history filters also apply"));
                    facts.Add(new("Offer order", "Other candidates can take priority before quest 169 is applied"));
                    consequences.Add("Ruby's event clears flags 236 and 369 and records status key 207; Clifford-related quest 121 reads related state.");
                    consequences.Add("An alternative town route can introduce Clifford's recruitment without being the same as Ruby's class-change operation.");
                    unknown.Add("Complete live town scheduling, optional events and reload refresh timing have not been accepted.");
                    break;
                case 83: // Reese
                    (quest,check,application,root) = (162,368,163,1083);
                    facts.Add(new("Story", "Quest 84 is not offered AND (ScoreGet at least 18 OR campaign counter T at least 9)"));
                    facts.Add(new("Quest", "Normal executable quest window T from 1 through 9; phase and quest-history filters also apply"));
                    consequences.Add("The class-change event adjusts team mercenary favor and branches into Larentia's recruitment sequence.");
                    unknown.Add("Additional destination-class eligibility and the complete normal-entry sequence are not a live result.");
                    break;
                case 132: // Enid
                    (quest,check,application,root) = (158,364,876,1129);
                    facts.Add(new("Shared destination", "Effective (derived) Mind at least 5, not necessarily personal Mind"));
                    facts.Add(new("Story", "Enid in primary team AND Perceval in primary or secondary team AND flag 296 unset AND flag 297 set"));
                    facts.Add(new("Quest", "Campaign counter T from 7 through 12; internal phase filter 1 and quest history also apply"));
                    facts.Add(new("Offer order", "Quest 140 can take priority before Enid's promotion offer"));
                    consequences.Add("Original event includes a town battle, item 256 delivery and flag 297 changes, with downstream Derrick-related conditions.");
                    unknown.Add("Town battle, item recipient, alternate dispatch and reload effects are not fully accepted.");
                    break;
                default:
                    unknown.Add("Additional weapon, shield, story and quest conditions for this caller are not fully catalogued.");
                    unknown.Add("The complete original application event and town entry have not been confirmed for this caller.");
                    break;
            }
            var name = roster.TryGetValue(unit, out var displayed) ? displayed : $"Unit 0x{unit:X3}";
            paths.Add(new(unit, name, destination, caller, minimum, quest, check, application, root,
                facts.AsReadOnly(), consequences.AsReadOnly(), unknown.AsReadOnly(), users[destination]));
        }
        return new(true,
            $"Static conditions for verified {recognized.Name}. Live eligibility is unknown; project edits are not included.",
            paths.OrderBy(p => p.CharacterName, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly());
    }

    private static PromotionCatalogueResult Unavailable(string reason) => new(false, reason, Array.Empty<PromotionPath>());
}
