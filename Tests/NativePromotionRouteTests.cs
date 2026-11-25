using Berwick.Core;

public static class NativePromotionRouteTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Source-only native promotion route inventory keeps all 16 names and original quest IDs", () =>
        {
            var routes = NativePromotionRoutes.All;
            if (routes.Count != 16 || routes.Select(r => r.QuestId).Distinct().Count() != 16
                || routes.Select(r => r.UnitId).Distinct().Count() != 16)
                throw new Exception("Native promotion route source inventory is duplicated or incomplete.");
            foreach (var route in routes)
                if (PromotionQuestWindows.QuestFor(route.UnitId) != route.QuestId
                    || !route.CheckHash.StartsWith("0x", StringComparison.Ordinal)
                    || !route.ApplicationHash.StartsWith("0x", StringComparison.Ordinal)
                    || route.SceneEvents.Count == 0)
                    throw new Exception("Native quest row or ceremony mapping is missing.");
        });
        test("Enid source introduction is not falsely labelled the native clear event", () =>
        {
            var enid = NativePromotionRoutes.ForUnit(132)!;
            if (enid.CheckEvent != 364 || enid.ApplicationEvent is not null || enid.IntroEvent != 876
                || enid.ResolvedApplication || enid.ConversionEvent != 876)
                throw new Exception("Quest 158 unresolved application was invented.");
        });
        test("Adel/Leon keep separate class-change scenes and original scripted gifts", () =>
        {
            var adel = NativePromotionRoutes.ForUnit(87)!;
            var leon = NativePromotionRoutes.ForUnit(88)!;
            if (adel.QuestId != 164 || adel.ApplicationEvent != 167 || adel.SceneSetup != "1353"
                || !adel.SceneEvents.SequenceEqual(new[] { 1354 }) || !adel.RelatedItemEvents.SequenceEqual(new[] { 168 })
                || leon.QuestId != 165 || leon.ApplicationEvent != 169 || leon.SceneSetup != "1367"
                || !leon.SceneEvents.SequenceEqual(new[] { 1368 }) || !leon.RelatedItemEvents.SequenceEqual(new[] { 170 }))
                throw new Exception("Source-observed promotion ceremonies or reward calls changed.");
        });
        test("Scene-mediated class conversion remains distinct for Kramer Dean and Esteban", () =>
        {
            if (NativePromotionRoutes.ForUnit(149)!.ConversionEvent != 182
                || NativePromotionRoutes.ForUnit(109)!.ConversionEvent != 184
                || NativePromotionRoutes.ForUnit(90)!.ConversionEvent != 186
                || !NativePromotionRoutes.ForUnit(109)!.SceneEvents.SequenceEqual(new[] { 1380, 1382 })
                || !NativePromotionRoutes.ForUnit(90)!.SceneEvents.SequenceEqual(new[] { 1372, 1374 }))
                throw new Exception("Branch-specific late conversions are mislabeled.");
        });
    }
}
