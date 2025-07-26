using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RandomPlus;
using RimWorld;
using Verse;

namespace FasterRandomPlus.Source
{
    public static class OptimizedRandomSettings
    {
        private static Action<Pawn, PawnGenerationRequest> genAge;
        private static Action<Pawn, PawnGenerationRequest> genTraits;
        private static Action<Pawn, PawnGenerationRequest> genSkills;
        private static Action<Pawn, PawnGenerationRequest> genHealth;
        private static Action<Pawn, PawnGenerationRequest> genBodyType;
        private static Action<Pawn, XenotypeDef, PawnGenerationRequest> genGenes;
        private static Func<List<Pawn>> getStartingPawns;

        public static int randomRerollCounter = 0;
        public static List<PawnFilter> pawnFilterList = new List<PawnFilter>();
        private static PawnFilter pawnFilter;

        private static readonly MethodInfo MiGeneratePawnRelations =
            typeof(PawnGenerator).GetMethod("GeneratePawnRelations", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo MinAgeForAdulthood =
            AccessTools.Field(typeof(PawnBioAndNameGenerator), "MinAgeForAdulthood");
        private static readonly MethodInfo MiGetMinAgeForAdulthood =
            Type.GetType("AlienRace.HarmonyPatches, AlienRace")
                ?.GetMethod(
                    "GetMinAgeForAdulthood",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Pawn), typeof(float) },
                    null
                );

        public static bool SelectAny = false;

        public static PawnFilter PawnFilter
        {
            get => pawnFilter;
            set => pawnFilter = value;
        }
        
        #region Stopwatch

        private static readonly Stopwatch swAge = new Stopwatch();
        private static readonly Stopwatch swRandomize =  new Stopwatch();
        private static readonly Stopwatch swTraits = new Stopwatch();
        private static readonly Stopwatch swHealth = new Stopwatch();
        private static readonly Stopwatch swGene = new Stopwatch();
        private static readonly Stopwatch swSkills = new Stopwatch();
        private static readonly Stopwatch swRedress = new Stopwatch();
        private static readonly Stopwatch swRelations = new Stopwatch();
        private static readonly Stopwatch swTotal = new Stopwatch();

        private static double totalRandomize;
        private static double totalAge, totalTraits, totalHealth, totalGene;
        private static double totalSkills, totalRedress, totalRelations, totalOverall;
        private static int countRuns;

        #endregion

        public static void Init()
        {
            pawnFilter = new PawnFilter();
            randomRerollCounter = 0;

            try
            {
                var miAge = typeof(PawnGenerator).GetMethod("GenerateRandomAge",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var miTrait =
                    typeof(PawnGenerator).GetMethod("GenerateTraits", BindingFlags.NonPublic | BindingFlags.Static);
                var miSkill =
                    typeof(PawnGenerator).GetMethod("GenerateSkills", BindingFlags.NonPublic | BindingFlags.Static);
                var miHealth = typeof(PawnGenerator).GetMethod("GenerateInitialHediffs",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var miBody =
                    typeof(PawnGenerator).GetMethod("GenerateBodyType", BindingFlags.NonPublic | BindingFlags.Static);
                var miGene =
                    typeof(PawnGenerator).GetMethod("GenerateGenes", BindingFlags.NonPublic | BindingFlags.Static);
                var piStart = typeof(StartingPawnUtility).GetProperty("StartingAndOptionalPawns",
                    BindingFlags.NonPublic | BindingFlags.Static);

                genAge = (Action<Pawn, PawnGenerationRequest>)Delegate.CreateDelegate(
                    typeof(Action<Pawn, PawnGenerationRequest>), miAge);
                genTraits = (Action<Pawn, PawnGenerationRequest>)Delegate.CreateDelegate(
                    typeof(Action<Pawn, PawnGenerationRequest>), miTrait);
                genSkills = (Action<Pawn, PawnGenerationRequest>)Delegate.CreateDelegate(
                    typeof(Action<Pawn, PawnGenerationRequest>), miSkill);
                genHealth = (Action<Pawn, PawnGenerationRequest>)Delegate.CreateDelegate(
                    typeof(Action<Pawn, PawnGenerationRequest>), miHealth);
                genBodyType =
                    (Action<Pawn, PawnGenerationRequest>)Delegate.CreateDelegate(
                        typeof(Action<Pawn, PawnGenerationRequest>), miBody);
                genGenes = (Action<Pawn, XenotypeDef, PawnGenerationRequest>)Delegate.CreateDelegate(
                    typeof(Action<Pawn, XenotypeDef, PawnGenerationRequest>), miGene);

                getStartingPawns = () => (List<Pawn>)piStart.GetValue(null, null);
            }
            catch (Exception ex)
            {
                Log.Error("RandomPlus: Failed to init delegates: " + ex.Message);
            }
        }
        
        public static void Reroll(int pawnIndex)
        {
            swTotal.Restart();
            
            var pawns = getStartingPawns();
            Pawn pawn = pawns[pawnIndex];

            SpouseRelationUtility.Notify_PawnRegenerated(pawn);
            pawn = StartingPawnUtility.RandomizeInPlace(pawn);

            randomRerollCounter++;
            var req = StartingPawnUtility.GetGenerationRequest(StartingPawnUtility.PawnIndex(pawn));
            req.ValidateAndFix();
            Faction faction = req.Faction
                              ?? (!Find.FactionManager.TryGetRandomNonColonyHumanlikeFaction(out var tmp, false, true)
                                  ? Faction.OfAncients
                                  : tmp);
            XenotypeDef xen = ModsConfig.BiotechActive
                ? PawnGenerator.GetXenotypeForGeneratedPawn(req)
                : null;
            
            float minAgeForAdulthood = (float)MinAgeForAdulthood.GetValue(null);

            Find.Scenario.Notify_NewPawnGenerating(pawn, req.Context);
            PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, req, xen);
            var cachedChildBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
            var cachedAdultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);

            int bioFailCount = 0;
            while (randomRerollCounter < PawnFilter.RerollLimit)
            {
                try
                {
                    randomRerollCounter++;
                    
                    swAge.Restart();
                    pawn.ageTracker = new Pawn_AgeTracker(pawn);
                    genAge?.Invoke(pawn, req);
                    int ageCount = 0;
                    foreach (var agePart in Find.Scenario.AllParts.OfType<ScenPart_PawnFilter_Age>())
                    {
                        while (!agePart.AllowPlayerStartingPawn(pawn, false, req) && ageCount < 1000)
                        {
                            genAge?.Invoke(pawn, req);
                            ageCount++;
                        }
                    }
                    
                    float effectiveMinAdult = minAgeForAdulthood;
                    if (FasterRandomPlus.hasHAR)
                        effectiveMinAdult = (float)MiGetMinAgeForAdulthood.Invoke(null, new object[] { pawn, minAgeForAdulthood });
                    
                    if (pawn.ageTracker.AgeBiologicalYearsFloat < effectiveMinAdult)
                    {
                        pawn.story.Adulthood = null;
                    }
                    else
                    {
                        if (pawn.story.GetBackstory(BackstorySlot.Adulthood) == null)
                        {
                            if (cachedAdultBs != null && cachedChildBs != null)
                            {
                                pawn.story.Childhood = cachedChildBs;
                                pawn.story.Adulthood = cachedAdultBs;
                            }
                            else
                            {
                                PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, req, xen);
                                if (pawn.story.GetBackstory(BackstorySlot.Adulthood) != null)
                                {
                                    cachedChildBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                                    cachedAdultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                                }
                            }
                        }
                    }
                    
                    
                    swAge.Stop();
                    totalAge += swAge.Elapsed.TotalMilliseconds;
                    if (!CheckAgeIsSatisfied(pawn)) continue;
                    
                    totalAge += swGene.Elapsed.TotalMilliseconds;
                    
                    swTraits.Restart();
                    pawn.story.traits = new TraitSet(pawn);
                    genTraits?.Invoke(pawn, req);
                    foreach (var part in Find.Scenario.AllParts.OfType<ScenPart_ForcedTrait>())
                        part.Notify_PawnGenerated(pawn, req.Context, false);
                    swTraits.Stop();
                    totalTraits += swTraits.Elapsed.TotalMilliseconds;
                    
                    if (!CheckTraitsIsSatisfied(pawn)) continue;
                    
                    swSkills.Restart();
                    pawn.skills = new Pawn_SkillTracker(pawn);
                    genSkills?.Invoke(pawn, req);
                    swSkills.Stop();
                    totalSkills += swSkills.Elapsed.TotalMilliseconds;
                    
                    if (!CheckSkillsIsSatisfied(pawn))
                    {
                        var combinedDisabledTags = pawn.story.DisabledWorkTagsBackstoryAndTraits;

                        var blockedWorkTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading
                            .Where(wt => (combinedDisabledTags & wt.workTags) != WorkTags.None)
                            .ToList();
                        
                        var activeSkillFilters = pawnFilter.Skills
                            .Where(f => 
                                f.MinValue > PawnFilter.SkillMinDefault
                                || f.Passion != Passion.None);
                        bool bioFail = activeSkillFilters.Any(f =>
                        {
                            var rec = pawn.skills.skills.FirstOrDefault(r => r.def == f.SkillDef);
                            if (rec == null) return false;

                            bool disabled = rec.def.IsDisabled(combinedDisabledTags, blockedWorkTypes);

                            var childBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                            var adultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                            bool reducedByBackstory = adultBs == null ||
                                                      (childBs != null && childBs.skillGains.Any(sg =>
                                                          sg.skill == f.SkillDef && sg.amount < 0)) ||
                                                      adultBs.skillGains.Any(sg =>
                                                          sg.skill == f.SkillDef && sg.amount < 0);

                            return disabled || reducedByBackstory;
                        });

                        // if (randomRerollCounter > 990)
                        // {
                        //     var childBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                        //     var adultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                        //     string childId = childBs != null ? childBs.title : "Nothing";
                        //     string adultId = adultBs  != null ? adultBs.title  : "Nothing";
                        //     Log.Message($"[{randomRerollCounter}] Backstory: Childhood={childId}, Adulthood={adultId}");
                        //     
                        //     var disabledTagsLog = pawn.story.DisabledWorkTagsBackstoryAndTraits;
                        //     Log.Message($"[{randomRerollCounter}] WorkTags: {disabledTagsLog}");
                        //     
                        //     var traits = pawn.story.traits.allTraits
                        //         .Select(t => t.def.defName)
                        //         .ToList();
                        //     var traitList = traits.Select(t => $"\"{t}\"").ToList();
                        //     Log.Message($"[{randomRerollCounter}] Traits: {string.Join(", ", traitList)}");
                        //     
                        //     var skillLevels = pawn.skills.skills
                        //         .Select(r => $"{r.def.defName}:{r.Level}")
                        //         .ToList();
                        //     var skillList = skillLevels.Select(t => $"\"{t}\"").ToList();
                        //     Log.Message($"[{randomRerollCounter}] Skill Levels: {string.Join(", ", skillList)}");
                        // }


                        if (bioFail)
                        {
                            bioFailCount++;
                            swRandomize.Restart();

                            bool hadDisabledOrNeg = activeSkillFilters.Any(f =>
                            {
                                var rec = pawn.skills.skills.FirstOrDefault(r => r.def == f.SkillDef);
                                if (rec == null) return false;
                                bool disabled = rec.def.IsDisabled(combinedDisabledTags, blockedWorkTypes);
                                var childBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                                var adultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                                bool negByBackstory =
                                    (childBs != null &&
                                     childBs.skillGains.Any(sg => sg.skill == f.SkillDef && sg.amount < 0))
                                    || (adultBs != null &&
                                        adultBs.skillGains.Any(sg => sg.skill == f.SkillDef && sg.amount < 0));
                                return disabled || negByBackstory;
                            });

                            if (hadDisabledOrNeg)
                            {
                                pawn = StartingPawnUtility.RandomizeInPlace(pawn);
                                PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, req, xen);
                            }
                            else if (pawn.story.GetBackstory(BackstorySlot.Adulthood) == null)
                            {
                                if (cachedAdultBs != null)
                                {
                                    pawn.story.Childhood = cachedChildBs;
                                    pawn.story.Adulthood = cachedAdultBs;
                                }
                                else
                                {
                                    pawn = StartingPawnUtility.RandomizeInPlace(pawn);
                                    PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, req, xen);
                                }
                            }

                            if (pawn.story.GetBackstory(BackstorySlot.Adulthood) != null)
                            {
                                cachedChildBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                                cachedAdultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                            }

                            swRandomize.Stop();
                            totalRandomize += swRandomize.Elapsed.TotalMilliseconds;
                        }

                        continue;
                    }

                    pawn.workSettings?.EnableAndInitialize();
                    if (!CheckWorkIsSatisfied(pawn)) continue;

                    swHealth.Restart();
                    bool okHealth = false;
                    for (int h = 0; h < 100 && !okHealth; h++)
                    {
                        pawn.health.Reset();
                        try
                        {
                            genHealth?.Invoke(pawn, req);
                            okHealth = !pawn.Dead && !pawn.Destroyed && !pawn.Downed;
                        }
                        catch
                        {
                        }
                    }

                    foreach (var part in Find.Scenario.AllParts.OfType<ScenPart_ForcedHediff>())
                        part.Notify_NewPawnGenerating(pawn, req.Context);
                    
                    swHealth.Stop();
                    totalHealth += swHealth.Elapsed.TotalMilliseconds;

                    if (!okHealth || !CheckHealthIsSatisfied(pawn)) continue;

                    var disabledTags = pawn.story.DisabledWorkTagsBackstoryAndTraits;
                    var disabledWorkTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading
                        .Where(wt => (disabledTags & wt.workTags) != 0).ToList();
                    var forcedPassionSkills =
                        new HashSet<SkillDef>(pawn.story.traits.allTraits.SelectMany(tr => tr.def.forcedPassions));

                    foreach (var rec in pawn.skills.skills)
                    {
                        if (pawn.story.traits.allTraits.Any(tr => tr.def.RequiresPassion(rec.def))
                            && rec.passion == Passion.None)
                        {
                            rec.passion = forcedPassionSkills.Contains(rec.def)
                                ? Passion.Major
                                : Passion.Minor;
                        }

                        if (rec.def.IsDisabled(disabledTags, disabledWorkTypes))
                        {
                            rec.passion = Passion.None;
                            rec.Level = 0;
                        }
                    }
                    
                    swGene.Restart();
                    if (ModsConfig.BiotechActive)
                    {
                        pawn.genes = new Pawn_GeneTracker(pawn);
                        pawn.genes.ClearXenogenes();
                        pawn.genes.SetXenotype(xen);
                        genGenes?.Invoke(pawn, xen, req);
                    }
                    swGene.Stop();

                    if (!CheckPawnIsSatisfied(pawn)) continue;

                    swRedress.Restart();
                    PawnGenerator.RedressPawn(pawn, req);
                    swRedress.Stop();
                    totalRedress += swRedress.Elapsed.TotalMilliseconds;

                    swRelations.Restart();
                    bool oldFlag = FasterRandomPlus.isRerolling;
                    FasterRandomPlus.isRerolling = false;
                    MiGeneratePawnRelations.Invoke(null, new object[] { pawn, req });
                    FasterRandomPlus.isRerolling = oldFlag;
                    swRelations.Stop();
                    totalRelations += swRelations.Elapsed.TotalMilliseconds;

                    genBodyType?.Invoke(pawn, req);
                    GeneratePawnStyle(pawn);

                    Find.Scenario.Notify_PawnGenerated(pawn, req.Context, true);
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        $"[Faster RandomPlus] Error during reroll {randomRerollCounter}: {ex.Message}\n{ex.StackTrace}");
                    try
                    {
                        Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
                        SpouseRelationUtility.Notify_PawnRegenerated(pawn);
                        pawn = StartingPawnUtility.RandomizeInPlace(pawn);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            
            swTotal.Stop();
            totalOverall += swTotal.Elapsed.TotalMilliseconds;
            countRuns++;

            // Log.Message(
            //     $"[FasterRandomPlus] Reroll Complete #{randomRerollCounter}\n" +
            //     $"Age: {totalAge:F1} ms, " +
            //     $"Traits: {totalTraits:F1} ms, " +
            //     $"Skills: {totalSkills:F1} ms,\n" +
            //     $"Health: {totalHealth:F1} ms, " +
            //     $"Gene: {totalGene:F1} ms,\n" +
            //     $"Redress: {totalRedress:F1} ms, " +
            //     $"Relations: {totalRelations:F1} ms, " + 
            //     $"Randomize: {totalRandomize:F1} ms" +
            //     $"Overall: {totalOverall:F1} ms\n" +
            //     $"BioFailCount: {bioFailCount}" 
            // );

            totalAge = totalTraits = totalSkills = totalHealth =
                totalGene = totalRedress = totalRelations = totalOverall = 0;
        }

        public static void ResetRerollCounter() => randomRerollCounter = 0;

        private static bool CheckPawnIsSatisfied(Pawn pawn)
        {
            return randomRerollCounter >= pawnFilter.RerollLimit
                   || (CheckGenderIsSatisfied(pawn)
                       && CheckSkillsIsSatisfied(pawn)
                       && CheckTraitsIsSatisfied(pawn)
                       && CheckHealthIsSatisfied(pawn)
                       && CheckWorkIsSatisfied(pawn)
                       && CheckAgeIsSatisfied(pawn));
        }

        private static bool CheckAgeIsSatisfied(Pawn pawn)
        {
            var r = pawnFilter.ageRange;
            long age = pawn.ageTracker.AgeBiologicalYears;
            return (r.min == PawnFilter.MinAgeDefault && r.max == PawnFilter.MaxAgeDefault)
                   || (r.min <= age && (r.max == PawnFilter.MaxAgeDefault || r.max >= age));
        }

        private static bool CheckGenderIsSatisfied(Pawn pawn)
        {
            return pawnFilter.Gender == Gender.None
                   || pawn.gender == pawnFilter.Gender;
        }

        private static bool CheckSkillsIsSatisfied(Pawn pawn)
        {
            var recs = pawn.skills.skills;
            foreach (var f in pawnFilter.Skills)
            {
                if (f.Passion != Passion.None || f.MinValue > 0)
                {
                    var rec = recs.FirstOrDefault(r => r.def == f.SkillDef);
                    if (rec == null) return false;
                    var effPassion = GetEffectivePassion(pawn, rec);
                    if (effPassion < f.Passion || rec.Level < f.MinValue)
                        return false;
                }
            }

            int passionCount = recs.Count(r => GetEffectivePassion(pawn, r) > Passion.None);
            int sum = recs.Sum(r => r.Level);

            var pr = pawnFilter.passionRange;
            if (pr.min > PawnFilter.PassionMinDefault || pr.max < PawnFilter.PassionMaxDefault)
            {
                if (passionCount < pr.min || passionCount > pr.max) return false;
            }

            var sr = pawnFilter.skillRange;
            if (sr.min != PawnFilter.SkillMinDefault || sr.max != PawnFilter.SkillMaxDefault)
            {
                int total;
                if (pawnFilter.countOnlyHighestAttack)
                {
                    var top = recs.OrderByDescending(r => r.Level).Take(2).ToList();
                    total = Math.Max(top[0].Level, top[1].Level);
                }
                else if (pawnFilter.countOnlyPassion)
                {
                    total = recs.Where(r => GetEffectivePassion(pawn, r) > Passion.None)
                        .Sum(r => r.Level);
                }
                else
                {
                    total = sum;
                }

                if (total < sr.min || total > sr.max) return false;
            }

            return true;
        }

        private static Passion GetEffectivePassion(Pawn pawn, SkillRecord rec)
        {
            if ((pawn.story.DisabledWorkTagsBackstoryAndTraits & rec.def.disablingWorkTags) != 0)
                return Passion.None;
            return rec.passion;
        }

        private static bool CheckTraitsIsSatisfied(Pawn pawn)
        {
            if (Page_RandomEditor.MOD_WELL_MET) return true;
            foreach (var tc in pawnFilter.Traits)
            {
                bool has = HasTrait(pawn, tc.trait);
                if (tc.traitFilter == TraitContainer.TraitFilterType.Required && !has) return false;
                if (tc.traitFilter == TraitContainer.TraitFilterType.Excluded && has) return false;
            }

            if (pawnFilter.RequiredTraitsInPool > 0)
            {
                int count = 0;
                foreach (var tc in pawnFilter.Traits)
                    if (tc.traitFilter == TraitContainer.TraitFilterType.Optional && HasTrait(pawn, tc.trait))
                    {
                        if (++count >= pawnFilter.RequiredTraitsInPool) break;
                    }

                if (count < pawnFilter.RequiredTraitsInPool) return false;
            }

            return true;
        }

        private static bool IsGeneAffectedHealth(Hediff hd)
        {
            return ModsConfig.BiotechActive
                   && hd is Hediff_ChemicalDependency cd
                   && cd.LinkedGene != null;
        }

        private static bool CheckHealthIsSatisfied(Pawn pawn)
        {
            switch (pawnFilter.FilterHealthCondition)
            {
                case PawnFilter.HealthOptions.OnlyStartCondition:
                    foreach (var hd in pawn.health.hediffSet.hediffs)
                        if (hd.def.defName != "CryptosleepSickness"
                            && hd.def.defName != "Malnutrition"
                            && !IsGeneAffectedHealth(hd))
                            return false;
                    break;
                case PawnFilter.HealthOptions.NoPain:
                    foreach (var hd in pawn.health.hediffSet.hediffs)
                        if (hd.PainOffset > 0 && !IsGeneAffectedHealth(hd))
                            return false;
                    break;
                case PawnFilter.HealthOptions.NoAddiction:
                    foreach (var hd in pawn.health.hediffSet.hediffs)
                        if (hd is Hediff_Addiction && !IsGeneAffectedHealth(hd))
                            return false;
                    break;
                case PawnFilter.HealthOptions.AllowNone:
                    if (ModsConfig.BiotechActive)
                    {
                        foreach (var hd in pawn.health.hediffSet.hediffs)
                            if (!IsGeneAffectedHealth(hd))
                                return false;
                    }
                    else if (pawn.health.hediffSet.hediffs.Count > 0)
                        return false;

                    break;
            }

            return true;
        }

        private static bool CheckWorkIsSatisfied(Pawn pawn)
        {
            switch (pawnFilter.FilterIncapable)
            {
                case PawnFilter.IncapableOptions.NoDumbLabor:
                    if ((pawn.story.DisabledWorkTagsBackstoryAndTraits & WorkTags.Violent) == WorkTags.Violent)
                        return false;
                    break;
                case PawnFilter.IncapableOptions.AllowNone:
                    if (pawn.story.DisabledWorkTagsBackstoryAndTraits != WorkTags.None)
                        return false;
                    break;
            }

            return true;
        }

        private static bool HasTrait(Pawn pawn, Trait trait)
        {
            foreach (var t in pawn.story.traits.allTraits)
                if ((t == null && trait == null) || (t != null && trait != null && t.Label == trait.Label))
                    return true;
            return false;
        }

        private static void GeneratePawnStyle(Pawn pawn)
        {
            if (!pawn.RaceProps.Humanlike) return;
            try
            {
                pawn.story.hairDef = PawnStyleItemChooser.RandomHairFor(pawn);
                if (pawn.style == null) return;
                pawn.style.beardDef = (pawn.gender == Gender.Male)
                    ? PawnStyleItemChooser.RandomBeardFor(pawn)
                    : BeardDefOf.NoBeard;
                if (ModsConfig.IdeologyActive)
                {
                    pawn.style.FaceTattoo = PawnStyleItemChooser.RandomTattooFor(pawn, TattooType.Face);
                    pawn.style.BodyTattoo = PawnStyleItemChooser.RandomTattooFor(pawn, TattooType.Body);
                }
                else pawn.style.SetupTattoos_NoIdeology();
            }
            catch
            {
            }
        }
    }
}