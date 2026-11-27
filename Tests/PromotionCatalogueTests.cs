using Berwick.Core;

public static class PromotionCatalogueTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Promotion class comparison shows movement loss, cap changes and class skill differences", () =>
        {
            var source = new ClassRecord(1, "Source", 20, 5, 3, 6, 2, 5, 10, ["Vantage"], [new("Spear", 30)]);
            var target = new ClassRecord(2, "Target", 22, 7, 4, 6, 2, 4, 10, ["Prepared"], [new("Sword", 40)]) { Mount = 1, ShieldSizes = 1 };
            var rows = PromotionClassComparison.Compare(source, target).ToDictionary(r => r.Field);
            if (rows["Movement"].Difference != "-1" || rows["Base HP"].Difference != "+2"
                || rows["Spear cap"].Destination != "0" || rows["Sword cap"].Source != "0"
                || rows["Class skill: Vantage"].Difference != "Removed" || rows["Class skill: Prepared"].Difference != "Added"
                || rows["Mount"].Destination != "Mounted" || rows["Shield sizes"].Destination != "Small")
                throw new Exception("Incorrect class-record comparison.");
        });
        test("Promotion permissions decode the native class mask separately from caps and use configured skill grants", () =>
        {
            var bytes = new byte[ClassDecoder.RecordLength];
            bytes[8] = 0x2F; // movement 15; sword permission bit
            bytes[10] = 0x20; // type 17 (dark healing)
            var cls = ClassDecoder.Decode(1, "Source", bytes);
            if (cls.BaseWeaponTypes != ((1 << 1) | (1 << 17)) || cls.Move != 15)
                throw new Exception("Incorrect native class permission mask.");
            var grants = new Dictionary<string, int?> { ["Lance"] = 8 };
            var target = cls with { BaseWeaponTypes = 0, Skills = ["Lance"], ShieldSizes = 1 };
            if (PromotionClassComparison.ClassAllows(cls, 1, grants) != true
                || PromotionClassComparison.ClassAllows(target, 1, grants) != false
                || PromotionClassComparison.ClassAllows(target, 8, grants) != true
                || PromotionClassComparison.ClassAllows(target, 5, grants) != false
                || PromotionClassComparison.ClassAllows(target, 18, grants) != true
                || PromotionClassComparison.ClassAllows(target, 21, grants) is not null
                || PromotionClassComparison.ClassAllows(target, 5, new Dictionary<string, int?>()) is not null)
                throw new Exception("Permission comparison inferred an unsupported grant or confused it with caps.");
        });
        test("Promotion comparison reuses traced item requests without treating conditional horse changes as grants", () =>
        {
            if (NativePromotionRoutes.ForUnit(87)?.KnownItemRequest != (168, 106)
                || NativePromotionRoutes.ForUnit(88)?.KnownItemRequest != (170, 106)
                || NativePromotionRoutes.ForUnit(131)?.KnownItemRequest != (177, 303)
                || NativePromotionRoutes.ForUnit(89)?.KnownItemRequest is not null)
                throw new Exception("Original item request evidence was changed or overstated.");
        });
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
        test("Ruby Reese and Enid preserve native quest and introduction distinctions", () =>
        {
            var p = Profiles.All[0];
            var rows = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, false).Paths.ToDictionary(x => x.UnitId);
            if (rows[134].QuestId != 169 || rows[134].CheckEvent != 375 || rows[134].ApplicationEvent != 175 || rows[134].TownRoot != 953)
                throw new Exception("Ruby promotion binding changed.");
            if (rows[83].QuestId != 162 || rows[83].CheckEvent != 368 || rows[83].ApplicationEvent != 163 || rows[83].TownRoot != 1083)
                throw new Exception("Reese promotion binding changed.");
            if (rows[132].QuestId != 158 || rows[132].CheckEvent != 364 || rows[132].ApplicationEvent is not null || rows[132].TownRoot != 1129)
                throw new Exception("Enid's unresolved application hash was misrepresented as event 876.");
            if (rows[132].Evidence != EvidenceLevel.SourceObserved
                || !rows[132].UnresolvedConditions.Any(x => x.Contains("0x7057955C")))
                throw new Exception("Enid's source-observed introduction was wrongly called a verified application.");
            if (!rows[132].KnownConditions.Any(c => c.Expression.Contains("derived", StringComparison.OrdinalIgnoreCase))
                || !rows[83].KnownConditions.Any(c => c.Expression.Contains(" OR ", StringComparison.Ordinal))
                || !rows[134].KnownConditions.Any(c => c.Expression.Contains(" AND ", StringComparison.Ordinal)))
                throw new Exception("Known logical requirements or effective-stat semantics were lost.");
        });
        test("Destination 237 preserves class-dependent and OR-grouped proficiency checks", () =>
        {
            var p = Profiles.All[0];
            var rows = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, false).Paths.ToDictionary(x => x.UnitId);
            string Shared(int id) => rows[id].KnownConditions.Single(c => c.Scope == "Shared destination, event 237").Expression;
            if (!Shared(85).Contains("current class 231") || !Shared(85).Contains("Medium Shield >= 10")
                || !Shared(96).Contains("ELSE") || !Shared(96).Contains("Spear >= 20"))
                throw new Exception("Destination 160 lost its current-class branch.");
            if (!Shared(113).Contains("Fire >= 30 OR Thunder >= 30 OR Wind >= 30")
                || !Shared(127).Contains("Fire >= 30 OR Thunder >= 30 OR Wind >= 30"))
                throw new Exception("Magic promotion OR predicate became an AND or was hidden.");
            if (!Shared(87).Contains("Spear >= 30 AND Small Shield >= 20")
                || !Shared(97).Contains("Axe >= 30 AND Small Shield >= 20 AND Medium Shield >= 10")
                || !Shared(124).Contains("Bow >= 20 AND Crossbow >= 20"))
                throw new Exception("Original destination thresholds were lost.");
            if (rows.Values.Any(r => !r.KnownConditions.Any(c => c.Scope == "Mercenary gate, event 236")
                || !r.KnownConditions.Any(c => c.Scope == "Proficiency semantics")))
                throw new Exception("The shared mercenary gate or capped point semantics were omitted.");
            if (rows.Values.Any(r => !r.KnownConditions.Any(c =>
                    c.Scope == "Source-class gate, event 236"
                    && c.Expression.Contains("literal is 0 (wildcard)"))
                    || r.UnresolvedConditions.Any(c => c.Contains("required source-class literal"))))
                throw new Exception("The 16 verified required-source-class wildcard literals were not retained.");
        });
        test("All 16 original outer caller conditions preserve original logic", () =>
        {
            foreach (var edition in Profiles.All)
            {
                var rows = PromotionCatalogue.Read(edition, edition.Sha256, edition.ExecutableSha256, false)
                    .Paths.ToDictionary(x => x.CallerEvent);
                if (rows.Count != 16) throw new Exception("Original caller inventory incomplete.");
                string Gate(int e) => rows[e].KnownConditions.Single(c => c.Scope == "Caller gate").Expression;
                foreach (var e in new[] {369,370,371,372,374,375,376})
                    if (Gate(e) != "Event flag 214 must be set")
                        throw new Exception($"Flag-214 outer check missing from caller {e}.");
                if (!Gate(373).Contains("mission 10 is completed")
                    || !Gate(373).Contains("AND"))
                    throw new Exception("Christine's caller mission condition was lost.");
                foreach (var e in new[] {377,401,402})
                    if (!Gate(e).Contains("quest 84 is not offered"))
                        throw new Exception($"Quest-history condition missing from caller {e}.");
                foreach (var e in new[] {378,379,380})
                    if (!Gate(e).Contains("No additional outer predicate"))
                        throw new Exception($"Additional condition was invented for caller {e}.");
                if (!Gate(368).Contains("ScoreGet >= 18 OR campaign counter >= 9")
                    || !Gate(368).Contains("NOT quest 84"))
                    throw new Exception("Reese's two-level outer predicate is incomplete.");
                if (!Gate(364).Contains("NOT flag 296 AND flag 297")
                    || !Gate(364).Contains("Perceval"))
                    throw new Exception("Enid's team/flag caller test is incomplete.");
            }
        });
        test("Known original quest windows are listed without inventing untraced application routes", () =>
        {
            foreach (var profile in Profiles.All)
            {
                var rows = PromotionCatalogue.Read(profile, profile.Sha256, profile.ExecutableSha256, false)
                    .Paths.ToDictionary(path => path.UnitId);
                if (rows.Count != 16 || rows.Values.Any(path => path.QuestId != PromotionQuestWindows.QuestFor(path.UnitId)))
                    throw new Exception("A source-bound quest window was omitted or assigned to another caller.");
                var leon = rows[88];
                if (leon.QuestId != 165 || leon.CallerEvent != 371 || leon.CheckEvent != 371
                    || leon.ApplicationEvent != 169 || leon.DestinationClassId != 28 || leon.TownRoot != 1367)
                    throw new Exception("Leon's verified quest route changed or an unknown town root was invented.");
                if (!leon.KnownConsequences.Any(c => c.Contains("1367"))
                    || !leon.KnownConsequences.Any(c => c.Contains("Iron Lance")))
                    throw new Exception("Leon's conversion and unresolved item reward provenance were conflated.");
                var adel = rows[87];
                if (adel.QuestId != 164 || adel.CheckEvent != 370 || adel.ApplicationEvent != 167
                    || adel.TownRoot != 1353 || adel.LiveEligible.HasValue || adel.LiveAvailable.HasValue)
                    throw new Exception("Adel's quest window was wrongly treated as a complete event trace.");
                if (rows[88].ConfirmedDestinationUsers.SequenceEqual(new[] {87,88}) is false)
                    throw new Exception("Leon must retain the known shared destination with Adel.");
            }
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
        test("Promotion coverage gaps are explicit and do not create fictional paths", () =>
        {
            foreach (var original in Profiles.All)
            {
                var result = PromotionCatalogue.Read(original, original.Sha256, original.ExecutableSha256, false);
                if (result.Paths.Count != 16 || result.CoverageGaps.Count != 3)
                    throw new Exception("The finite caller count or documented gaps changed.");
                if (!result.CoverageGaps.Any(g => g.Category.Contains("Inherited")
                    && g.Description.Contains("187"))
                    || !result.CoverageGaps.Any(g => g.Category.Contains("Computed"))
                    || !result.CoverageGaps.Any(g => g.Category.Contains("Scripted")))
                    throw new Exception("Incomplete promotion pathways are not disclosed.");
                var rejected = PromotionCatalogue.Read(original, original.Sha256, original.ExecutableSha256, true);
                if (rejected.CoverageGaps.Count != 0 || rejected.Paths.Count != 0)
                    throw new Exception("Unsupported source inherited original-disc coverage assertions.");
            }
        });

        test("Adel and Leon have verified separate application scripts and original rewards", () =>
        {
            foreach (var edition in Profiles.All)
            {
                var rows = PromotionCatalogue.Read(edition, edition.Sha256, edition.ExecutableSha256, false)
                    .Paths.ToDictionary(x => x.UnitId);
                if (rows[87].QuestId != 164 || rows[87].CheckEvent != 370
                    || rows[87].ApplicationEvent != 167 || rows[87].TownRoot != 1353
                    || rows[88].QuestId != 165 || rows[88].CheckEvent != 371
                    || rows[88].ApplicationEvent != 169 || rows[88].TownRoot != 1367)
                    throw new Exception("The source-verified Adel/Leon promotion route changed.");
                if (!rows[87].KnownConsequences.Any(x => x.Contains("event 168"))
                    || !rows[88].KnownConsequences.Any(x => x.Contains("event 170")))
                    throw new Exception("The independent promotion item requests were omitted.");
            }
        });

        test("Other class changes stay separate, reference-only and non-editable", () =>
        {
            foreach (var profile in Profiles.All)
            {
                var original = PromotionCatalogue.Read(profile, profile.Sha256, profile.ExecutableSha256, false);
                if (!original.OriginalSourceVerified || original.OtherClassChanges.Count != 3)
                    throw new Exception("The separate original-disc reference section was not populated.");
                var toggle = original.OtherClassChanges[0];
                if (toggle.Character != "Owen" || toggle.Mechanism != "Convert"
                    || toggle.WriteAllowed
                    || !toggle.Evidence.Contains("not yet traced"))
                    throw new Exception("Owen's reference-only class toggle was misclassified or exposed for editing.");
                if (original.OtherClassChanges.Count(x => x.EvidenceLabel == "Source observed") != 2
                    || original.OtherClassChanges.Skip(1).Any(x => x.WriteAllowed))
                    throw new Exception("Original unverified class-transition sites were lost or marked writable.");
                if (original.Paths.Any(x => x.CharacterName == "Owen"))
                    throw new Exception("The class toggle was incorrectly counted as a promotion path.");
                var unsupported = PromotionCatalogue.Read(profile, profile.Sha256, profile.ExecutableSha256, true);
                if (unsupported.OtherClassChanges.Count != 0)
                    throw new Exception("Unverified source inherited original-reference entries.");
            }
        });

        test("Incomplete callers preserve unknown conditions and no fake live status", () =>
        {
            var p = Profiles.All[0];
            var rows = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, false).Paths;
            var ordinary = rows.Single(x => x.CallerEvent == 372);
            if (ordinary.UnresolvedConditions.Count == 0 || ordinary.QuestId != 166
                || ordinary.ApplicationEvent.HasValue || ordinary.TownRoot.HasValue
                || ordinary.LiveEligible.HasValue || ordinary.LiveAvailable.HasValue)
                throw new Exception("An incomplete original promotion path was silently treated as complete.");
            var unsupported = PromotionCatalogue.Read(p, p.Sha256, p.ExecutableSha256, modified: true);
            if (unsupported.Paths.Count != 0 || unsupported.WriteAllowed || unsupported.OriginalSourceVerified)
                throw new Exception("A modified disc inherited original requirements.");
        });
    }
}
