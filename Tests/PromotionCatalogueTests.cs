using Berwick.Core;

public static class PromotionCatalogueTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Original promotion catalogue is source-bound and has 16 literal paths per edition", () =>
        {
            foreach (var p in Profiles.All)
            {
                var result = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, modified: false);
                if (!result.OriginalSourceVerified || result.Paths.Count != 16 || result.WriteAllowed)
                    throw new Exception("A verified original must have 16 read-only candidate paths.");
                if (result.Paths.Any(x => x.WriteAllowed || x.CallerSetComplete
                    || x.LiveEligible is not null || x.LiveAvailable is not null))
                    throw new Exception("The static catalogue claimed live eligibility, complete caller coverage or write authority.");
                if (result.Paths.Select(x => x.UnitId).Distinct().Count() != 16)
                    throw new Exception("One literal promotion caller was duplicated.");
            }
        });
        test("Promotion catalogue keeps original edition identities separate", () =>
        {
            var jp = Profiles.All.Single(p => p.Name == "Japanese retail");
            var en = Profiles.All.Single(p => p.Name == "English v2.3");
            if (PromotionCatalogue.Read(jp, en.Sha256, jp.ExecutableSha256, false).OriginalSourceVerified)
                throw new Exception("English source hash was accepted for Japanese profile.");
            if (PromotionCatalogue.Read(en, en.Sha256, jp.ExecutableSha256, false).OriginalSourceVerified)
                throw new Exception("Japanese executable hash was accepted for English source.");
            if (PromotionCatalogue.Read(en, en.Sha256, en.ExecutableSha256, true).OriginalSourceVerified)
                throw new Exception("A modified image was treated as an original.");
            if (PromotionCatalogue.Read(null, en.Sha256, en.ExecutableSha256, false).OriginalSourceVerified
                || PromotionCatalogue.Read(en, "unrecognized", en.ExecutableSha256, false).OriginalSourceVerified)
                throw new Exception("An unknown source was accepted.");
        });
        test("Ruby Reese and Enid have distinct verified quests and application events", () =>
        {
            var p = Profiles.All[0];
            var rows = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, false).Paths.ToDictionary(x => x.UnitId);
            if (rows[134].QuestId != 169 || rows[134].CheckEvent != 375 || rows[134].ApplicationEvent != 175 || rows[134].TownRoot != 953)
                throw new Exception("Ruby promotion binding changed.");
            if (rows[83].QuestId != 162 || rows[83].CheckEvent != 368 || rows[83].ApplicationEvent != 163 || rows[83].TownRoot != 1083)
                throw new Exception("Reese promotion binding changed.");
            if (rows[132].QuestId != 158 || rows[132].CheckEvent != 364 || rows[132].ApplicationEvent != 876 || rows[132].TownRoot != 1129)
                throw new Exception("Enid promotion binding changed.");
            if (!rows[132].KnownConditions.Any(c => c.Expression.Contains("derived", StringComparison.OrdinalIgnoreCase))
                || !rows[83].KnownConditions.Any(c => c.Expression.Contains(" OR ", StringComparison.Ordinal))
                || !rows[134].KnownConditions.Any(c => c.Expression.Contains(" AND ", StringComparison.Ordinal)))
                throw new Exception("Known logical requirements or effective-stat semantics were lost.");
        });
        test("Shared destinations name known users without claiming complete coverage", () =>
        {
            var p = Profiles.All[0];
            var rows = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, false).Paths.ToDictionary(x => x.UnitId);
            if (!rows[87].ConfirmedDestinationUsers.SequenceEqual(new[] {87,88})
                || !rows[88].ConfirmedDestinationUsers.SequenceEqual(new[] {87,88})
                || !rows[85].ConfirmedDestinationUsers.SequenceEqual(new[] {85,96})
                || !rows[96].ConfirmedDestinationUsers.SequenceEqual(new[] {85,96}))
                throw new Exception("A shared destination lost its other known caller.");
            if (rows[87].CallerSetComplete || rows[96].CallerSetComplete)
                throw new Exception("Known destination users were incorrectly presented as exhaustive.");
        });
        test("Incomplete callers preserve unknown conditions and no fake live status", () =>
        {
            var p = Profiles.All[0];
            var rows = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, false).Paths;
            var ordinary = rows.Single(x => x.CallerEvent == 370);
            if (ordinary.UnresolvedConditions.Count == 0 || ordinary.QuestId.HasValue
                || ordinary.LiveEligible.HasValue || ordinary.LiveAvailable.HasValue)
                throw new Exception("An incomplete original promotion path was silently treated as complete.");
            var unsupported = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, modified: true);
            if (unsupported.Paths.Count != 0 || unsupported.WriteAllowed || unsupported.OriginalSourceVerified)
                throw new Exception("A modified disc inherited original requirements.");
        });
    }
}
