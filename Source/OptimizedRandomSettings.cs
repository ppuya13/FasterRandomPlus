using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlienRace;
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
        private static PawnGenerationRequest _req;

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
        
        static readonly BackstoryCategoryFilter FRP_NewbornCategoryGroup = new BackstoryCategoryFilter {
            categories = new List<string> { "Newborn" }, commonality = 1f
        };

        static readonly BackstoryCategoryFilter FRP_FallbackCategoryGroup = new BackstoryCategoryFilter {
            categories = new List<string> { "Civil" }, commonality = 1f
        };
        
        #region Stopwatch

        private static readonly Stopwatch swFirst = new Stopwatch();
        private static readonly Stopwatch swCaching = new Stopwatch();
        private static readonly Stopwatch swGender = new Stopwatch();
        private static readonly Stopwatch swAge = new Stopwatch();
        private static readonly Stopwatch swRandomize = new Stopwatch();
        private static readonly Stopwatch swTraits = new Stopwatch();
        private static readonly Stopwatch swHealth = new Stopwatch();
        private static readonly Stopwatch swGene = new Stopwatch();
        private static readonly Stopwatch swSkills = new Stopwatch();
        private static readonly Stopwatch swRedress = new Stopwatch();
        private static readonly Stopwatch swRelations = new Stopwatch();
        private static readonly Stopwatch swWork = new Stopwatch();
        private static readonly Stopwatch swPassion = new Stopwatch();
        private static readonly Stopwatch swStyle = new Stopwatch();
        private static readonly Stopwatch swFinalNotify = new Stopwatch();
        private static readonly Stopwatch swTotal = new Stopwatch();

        private static double totalRandomize, totalCaching;
        private static double totalAge, totalTraits, totalHealth, totalGene;
        private static double totalSkills, totalRedress, totalRelations, totalOverall;
        private static double totalWork, totalPassion, totalStyle, totalFinalNotify;
        private static double totalGender;
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

        private static WorkTags requiredWorkTags;
        private static List<Pawn> pawns;
        private static Pawn pawn;
        private static Faction faction;
        private static BackstoryDef cachedChildBs;
        private static BackstoryDef cachedAdultBs;
        private static Dictionary<(XenotypeDef, Gender), Name> cachedName;
        private static XenotypeDef xen;

        private static ScenPart_PawnFilter_Age agePart;
        private static ScenPart_ForcedTrait[] traitPart;
        private static ScenPart_ForcedHediff[] hediffPart;
        
        static int bioFailCount = 0; //bioFail시 상승, 백스토리 변경 시 초기화
        static int noPositiveGainCount = 0; //스킬 레벨 필터 불만족 시 스킬게인이 없는 백스토리일 경우 상승, 백스토리 변경 시 초기화
        static int skillRerollCount = 0; //스킬 레벨 필터 불만족 시 상승, 스킬 레벨 필터에서 백스토리 변경 또는 다른 필터 불만족 시 초기화
        static int cacheCount1 = 0;
        static int cacheCount2 = 0;
        static int genderCount = 0;
        static int ageCount = 0;
        static int traitCount = 0;
        static int skillCount = 0;
        static int workCount = 0;
        static int healthCount = 0;
        static int passionCount = 0;
        static int finalCount = 0;
        
        public static void BeginReroll(int pawnIndex, int limit)
        {
            swTotal.Restart();
            
            requiredWorkTags = WorkTags.None;
            foreach (var tc in pawnFilter.Traits)
            {
                var trait = tc?.trait;
                var td = trait?.def;
                if (td == null) continue;

                requiredWorkTags |= td.requiredWorkTags;
            }

            foreach (var sc in pawnFilter.Skills)
            {
                if (sc == null) continue;

                bool requiresLevel = sc.MinValue > 0;
                bool requiresPassion = sc.Passion > Passion.None;
                if (!requiresLevel && !requiresPassion) continue;

                var sd = sc.SkillDef;
                if (sd == null) continue;

                requiredWorkTags |= RequiredTagsFromSkill(sd);
            }
            
            swFirst.Restart();
            pawns = getStartingPawns();
            pawn = pawns[pawnIndex];

            SpouseRelationUtility.Notify_PawnRegenerated(pawn);
            pawn = StartingPawnUtility.RandomizeInPlace(pawn);

            // randomRerollCounter++;
            _req = StartingPawnUtility.GetGenerationRequest(StartingPawnUtility.PawnIndex(pawn));
            _req.ValidateAndFix();
            faction = _req.Faction
                              ?? (!Find.FactionManager.TryGetRandomNonColonyHumanlikeFaction(out var tmp, false, true)
                                  ? Faction.OfAncients
                                  : tmp);
            xen = ModsConfig.BiotechActive
                ? PawnGenerator.GetXenotypeForGeneratedPawn(_req)
                : null;

            Find.Scenario.Notify_NewPawnGenerating(pawn, _req.Context);
            PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, _req, xen);
            cachedChildBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
            cachedAdultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
            cachedName = new Dictionary<(XenotypeDef, Gender), Name>();
            cachedName[(pawn.genes.Xenotype, pawn.gender)] = pawn.Name;

            agePart = Find.Scenario.AllParts.OfType<ScenPart_PawnFilter_Age>().FirstOrDefault();
            traitPart = Find.Scenario.AllParts.OfType<ScenPart_ForcedTrait>().ToArray();
            hediffPart = Find.Scenario.AllParts.OfType<ScenPart_ForcedHediff>().ToArray();
            
            swFirst.Stop();

            bioFailCount = noPositiveGainCount = skillRerollCount =
                cacheCount1 = cacheCount2 = genderCount = ageCount =
                    traitCount = skillCount = workCount = healthCount =
                        passionCount = finalCount = 0;
        }

        public static bool StepOnce()
        {
            try
            {
                randomRerollCounter++;
                if (randomRerollCounter > PawnFilter.RerollLimit) 
                    return true;

                #region Backstory

                if (!CheckBackstoryIsSatisfied(cachedChildBs, cachedAdultBs, requiredWorkTags))
                {
                    cacheCount1++;
                    skillRerollCount = 0;
                    swCaching.Restart();
                    PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, _req, xen);
                    noPositiveGainCount = 0;
                    cachedChildBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                    cachedAdultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                    swCaching.Stop();
                    totalCaching += swCaching.Elapsed.TotalMilliseconds;
                    return false;
                }

                if (!CheckBackstoryIsSatisfied(pawn.story.GetBackstory(BackstorySlot.Childhood),
                        pawn.story.GetBackstory(BackstorySlot.Adulthood), requiredWorkTags))
                {
                    cacheCount2++;
                    pawn.story.Childhood = cachedChildBs;
                    pawn.story.Adulthood = cachedAdultBs;
                }

                if (IsAdult(pawn) && pawn.story.GetBackstory(BackstorySlot.Adulthood) == null)
                {
                    //성인인데 백스토리가 없는 경우 보정
                    if (cachedAdultBs != null)
                    {
                        //캐싱된 성인 백스토리가 있으면 덮어씀
                        pawn.story.Childhood = cachedChildBs;
                        pawn.story.Adulthood = cachedAdultBs;
                    }
                    else
                    {
                        //캐싱된 성인 백스토리가 없으면 셔플
                        SwapBackstoryWithShuffled(pawn, faction, _req, xen);
                        if (CheckBackstoryIsSatisfied(pawn.story.GetBackstory(BackstorySlot.Childhood),
                                pawn.story.GetBackstory(BackstorySlot.Adulthood), requiredWorkTags))
                        {
                            //셔플 결과가 필터에 맞으면 새로 캐싱
                            cachedChildBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                            cachedAdultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                        }
                    }
                }
                else if (!IsAdult(pawn) && pawn.story.GetBackstory(BackstorySlot.Adulthood) != null)
                {
                    //어린이인데 성인 백스토리가 있는 경우 보정
                    pawn.story.Adulthood = null;
                }

                #endregion

                #region Gender

                swGender.Restart();
                var oldGender = pawn.gender;
                pawn.gender =
                    _req.FixedGender
                    ?? _req.KindDef.fixedGender
                    ?? (!pawn.RaceProps.hasGenders
                        ? Gender.None
                        : (pawn.RaceProps.forceGender != Gender.None
                            ? pawn.RaceProps.forceGender
                            : (Rand.Value >= 0.5f ? Gender.Female : Gender.Male)));

                if (pawn.gender != oldGender)
                {
                    if (!cachedName.TryGetValue((pawn.genes.Xenotype, pawn.gender), out var name))
                    {
                        pawn.Name = PawnBioAndNameGenerator.GeneratePawnName(
                            pawn, forcedLastName: _req.FixedLastName, xenotype: xen);
                        cachedName[(pawn.genes.Xenotype, pawn.gender)] = pawn.Name;
                    }
                    else
                    {
                        pawn.Name = name;
                    }
                }

                swGender.Stop();
                totalGender += swGender.Elapsed.TotalMilliseconds;

                if (!CheckGenderIsSatisfied(pawn))
                {
                    genderCount++;
                    skillRerollCount = 0;
                    return false;
                }

                #endregion

                #region Age

                swAge.Restart();
                pawn.ageTracker = new Pawn_AgeTracker(pawn);
                if (agePart != null) SetRandomAgeInRange(pawn, agePart.allowedAgeRange);
                else genAge?.Invoke(pawn, _req);

                if (agePart != null && !agePart.AllowPlayerStartingPawn(pawn, false, _req))
                {
                    int bioYears = pawn.ageTracker.AgeBiologicalYears;
                    int chronoYears = pawn.ageTracker.AgeChronologicalYears;
                    Log.Warning(
                        $"[FasterRandomPlus][GenAgeWarning] Reroll #{randomRerollCounter}: " +
                        $"Generated age (biological={bioYears}y, chronological={chronoYears}y) " +
                        $"is outside the allowed range ({agePart.allowedAgeRange.min}-{agePart.allowedAgeRange.max}y). " +
                        $"Pawn={pawn.LabelShort}, Context={_req.Context}");
                    return false;
                }

                if (!IsAdult(pawn))
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
                            // PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, req, xen);
                            SwapBackstoryWithShuffled(pawn, faction, _req, xen);
                            noPositiveGainCount = 0;
                            if (pawn.story.GetBackstory(BackstorySlot.Adulthood) != null)
                            {
                                if (CheckBackstoryIsSatisfied(
                                        pawn.story.GetBackstory(BackstorySlot.Childhood),
                                        pawn.story.GetBackstory(BackstorySlot.Adulthood),
                                        requiredWorkTags))
                                {
                                    cachedChildBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                                    cachedAdultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                                }
                            }
                        }
                    }
                }

                swAge.Stop();
                totalAge += swAge.Elapsed.TotalMilliseconds;

                if (!CheckAgeIsSatisfied(pawn))
                {
                    ageCount++;
                    skillRerollCount = 0;
                    return false;
                }

                #endregion

                #region Trait

                swTraits.Restart();
                pawn.story.traits = new TraitSet(pawn);
                genTraits?.Invoke(pawn, _req);
                foreach (var part in traitPart)
                    part.Notify_PawnGenerated(pawn, _req.Context, false);
                swTraits.Stop();
                totalTraits += swTraits.Elapsed.TotalMilliseconds;

                if (!CheckTraitsIsSatisfied(pawn, requiredWorkTags))
                {
                    traitCount++;
                    skillRerollCount = 0;
                    return false;
                }

                #endregion

                #region Skill

                swSkills.Restart();
                pawn.skills = new Pawn_SkillTracker(pawn);
                genSkills?.Invoke(pawn, _req);
                swSkills.Stop();
                totalSkills += swSkills.Elapsed.TotalMilliseconds;

                if (!CheckSkillsIsSatisfied(pawn))
                {
                    skillCount++;
                    skillRerollCount++;
                    if (skillRerollCount > 100)
                    {
                        // PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, req, xen);
                        SwapBackstoryWithShuffled(pawn, faction, _req, xen);
                        skillRerollCount = 0;
                        noPositiveGainCount = 0;
                        return false;
                    }

                    var combinedDisabledTags = pawn.story.DisabledWorkTagsBackstoryAndTraits;

                    var blockedWorkTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading
                        .Where(wt => (combinedDisabledTags & wt.workTags) != WorkTags.None)
                        .ToList();

                    var activeSkillFilters = pawnFilter.Skills
                        .Where(f =>
                            f.MinValue > PawnFilter.SkillMinDefault
                            || f.Passion != Passion.None);
                    bool bioFail = activeSkillFilters.Any(f => //백스토리가 스킬 필터를 방해할 경우 true
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

                    // if (randomRerollCounter > PawnFilter.RerollLimit - 10) 
                    // {
                    //     var childBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                    //     var adultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                    //     string childId = childBs != null ? childBs.title : "Nothing";
                    //     string adultId = adultBs  != null ? adultBs.title  : "Nothing";
                    //     StringBuilder debugLog = new StringBuilder();
                    //     debugLog.Append(
                    //         $"[{randomRerollCounter}] Backstory: Childhood={childId}, Adulthood={adultId}\n");
                    //     
                    //     var disabledTagsLog = pawn.story.DisabledWorkTagsBackstoryAndTraits;
                    //     debugLog.Append(
                    //         $"[{randomRerollCounter}] WorkTags: {disabledTagsLog}\n");
                    //     
                    //     var traits = pawn.story.traits.allTraits
                    //         .Select(t => t.def.defName)
                    //         .ToList();
                    //     var traitList = traits.Select(t => $"\"{t}\"").ToList();
                    //     debugLog.Append(
                    //         $"[{randomRerollCounter}] Traits: {string.Join(", ", traitList)}\n");
                    //     
                    //     var skillLevels = pawn.skills.skills
                    //         .Select(r => $"{r.def.defName}:{r.Level}")
                    //         .ToList();
                    //     var skillList = skillLevels.Select(t => $"\"{t}\"").ToList();
                    //     debugLog.Append(
                    //         $"[{randomRerollCounter}] Skill Levels: {string.Join(", ", skillList)}");
                    //     Log.Message(debugLog.ToString());
                    // }

                    if (bioFail)
                    {
                        bioFailCount++;
                        swRandomize.Restart();

                        bool backstoryConflictsWithSkillFilters =
                            activeSkillFilters.Any(f => //bioFail의 원인이 백스토리인지 확인하기 위한 플래그
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

                        if (backstoryConflictsWithSkillFilters)
                        {
                            //백스토리가 원인이면 셔플
                            SwapBackstoryWithShuffled(pawn, faction, _req, xen);
                            var childBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                            var adultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                            if (CheckBackstoryIsSatisfied(childBs, adultBs, requiredWorkTags))
                            {
                                cachedChildBs = childBs;
                                cachedAdultBs = adultBs;
                            }

                            skillRerollCount = 0;
                            noPositiveGainCount = 0;
                        }

                        swRandomize.Stop();
                        totalRandomize += swRandomize.Elapsed.TotalMilliseconds;
                    }

                    bool noPositiveGainForAny =
                        activeSkillFilters.Any(f =>
                        {
                            var child = pawn.story.GetBackstory(BackstorySlot.Childhood);
                            var adult = pawn.story.GetBackstory(BackstorySlot.Adulthood);
                            bool pos =
                                (child?.skillGains.Any(g => g.skill == f.SkillDef && g.amount > 0) == true) ||
                                (adult?.skillGains.Any(g => g.skill == f.SkillDef && g.amount > 0) == true);
                            return !pos;
                        });

                    if (noPositiveGainForAny)
                        noPositiveGainCount++;

                    if (noPositiveGainCount >= 50)
                    {
                        // PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, faction.def, req, xen);
                        SwapBackstoryWithShuffled(pawn, faction, _req, xen);

                        skillRerollCount = 0;
                        noPositiveGainCount = 0;
                        cachedChildBs = pawn.story.GetBackstory(BackstorySlot.Childhood);
                        cachedAdultBs = pawn.story.GetBackstory(BackstorySlot.Adulthood);

                        // Log.Message("[FasterRandomPlus] 백스토리 스왑");
                    }

                    return false;
                }

                #endregion

                #region Work

                swWork.Restart();
                pawn.workSettings?.EnableAndInitialize();
                swWork.Stop();
                totalWork += swWork.Elapsed.TotalMilliseconds;

                if (!CheckWorkIsSatisfied(pawn))
                {
                    workCount++;
                    skillRerollCount = 0;
                    return false;
                }

                #endregion

                #region Health

                swHealth.Restart();
                bool okHealth = false;
                for (int h = 0; h < 100 && !okHealth; h++)
                {
                    pawn.health.Reset();
                    try
                    {
                        genHealth?.Invoke(pawn, _req);
                        okHealth = !pawn.Dead && !pawn.Destroyed && !pawn.Downed;
                    }
                    catch
                    {
                    }
                }

                foreach (var part in hediffPart)
                    part.Notify_NewPawnGenerating(pawn, _req.Context);

                swHealth.Stop();
                totalHealth += swHealth.Elapsed.TotalMilliseconds;

                //health
                if (!okHealth || !CheckHealthIsSatisfied(pawn))
                {
                    healthCount++;
                    skillRerollCount = 0;

                    return false;
                }

                #endregion

                #region Passion, Gene

                passionCount++;
                swPassion.Restart();
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

                swPassion.Stop();
                totalPassion += swPassion.Elapsed.TotalMilliseconds;

                swGene.Restart();
                if (ModsConfig.BiotechActive)
                {
                    pawn.genes = new Pawn_GeneTracker(pawn);
                    pawn.genes.ClearXenogenes();
                    pawn.genes.SetXenotype(xen);
                    genGenes?.Invoke(pawn, xen, _req);
                }

                swGene.Stop();
                totalGene += swGene.Elapsed.TotalMilliseconds;

                if (!CheckPawnIsSatisfied(pawn))
                {
                    skillRerollCount = 0;
                    return false;
                }
                return true;

                #endregion

                // break;
            }
            catch (Exception ex)
            {
                Log.Warning(
                    $"[Faster RandomPlus] Error during reroll {randomRerollCounter}: {ex.Message}\n{ex.StackTrace}");
                try
                {
                    Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
                    SpouseRelationUtility.Notify_PawnRegenerated(pawn);
                    // pawn = StartingPawnUtility.RandomizeInPlace(pawn);
                }
                catch
                {
                    // break;
                }
            }

            // randomRerollCounter++;
            if (randomRerollCounter >= PawnFilter.RerollLimit) return true;
            return false;
        }
        
        public static void AbortReroll()
        {
            // 취소
        }
        
        public static void EndReroll()
        {
            
            bool Mismatch(Gender g, BodyTypeDef bt)
                => (g == Gender.Male && bt == BodyTypeDefOf.Female)
                   || (g == Gender.Female && bt == BodyTypeDefOf.Male);

            finalCount++;

            swRelations.Restart();
            bool oldFlag = FasterRandomPlus.isRerolling;
            FasterRandomPlus.isRerolling = false;
            MiGeneratePawnRelations.Invoke(null, new object[] { pawn, _req });
            FasterRandomPlus.isRerolling = oldFlag;
            swRelations.Stop();
            totalRelations += swRelations.Elapsed.TotalMilliseconds;

            swStyle.Restart();
            genBodyType?.Invoke(pawn, _req);
            GeneratePawnStyle(pawn);
            if (Mismatch(pawn.gender, pawn.story.bodyType))
            {
                var savedAdult = pawn.story.Adulthood;
                pawn.story.Adulthood = null;
                var fallback = PawnGenerator.GetBodyTypeFor(pawn);
                pawn.story.Adulthood = savedAdult;

                pawn.story.bodyType = fallback
                                      ?? (pawn.gender == Gender.Female
                                          ? BodyTypeDefOf.Female
                                          : BodyTypeDefOf.Male);
            }
            swStyle.Stop();
            totalStyle += swStyle.Elapsed.TotalMilliseconds;
            
            swRedress.Restart();
            PawnGenerator.RedressPawn(pawn, _req);
            swRedress.Stop();
            totalRedress += swRedress.Elapsed.TotalMilliseconds;
            
            swFinalNotify.Restart();
            Find.Scenario.Notify_PawnGenerated(pawn, _req.Context, true);
            swFinalNotify.Stop();
            totalFinalNotify += swFinalNotify.Elapsed.TotalMilliseconds;

            if (randomRerollCounter >= PawnFilter.RerollLimit) genSkills?.Invoke(pawn, _req);

            swTotal.Stop();
            totalOverall += swTotal.Elapsed.TotalMilliseconds;
            countRuns++;

            //Debug Logging
            // Log.Message(
            //     $"[FasterRandomPlus] Reroll Complete #{randomRerollCounter}\n" +
            //     $"First: {swFirst.Elapsed.TotalMilliseconds:F1} ms," +
            //     $"Backstory Caching({cacheCount1}, {cacheCount2}): {swCaching.Elapsed.TotalMilliseconds:F1} ms,\n" +
            //     $"Gender({genderCount}): {totalGender:F1} ms, " +
            //     $"Age({ageCount}): {totalAge:F1} ms, " +
            //     $"Traits({traitCount}): {totalTraits:F1} ms, " +
            //     $"Skills({skillCount}): {totalSkills:F1} ms,\n" +
            //     $"Health({healthCount}): {totalHealth:F1} ms, " +
            //     $"Gene: {totalGene:F1} ms,\n" +
            //     $"Work({workCount}): {totalWork:F1} ms, " +
            //     $"passion({passionCount}): {totalPassion:F1} ms, " +
            //     $"style: {totalStyle:F1} ms, " +
            //     $"finalNotify({finalCount}): {totalFinalNotify:F1} ms,\n" +
            //     $"Redress: {totalRedress:F1} ms, " +
            //     $"Relations: {totalRelations:F1} ms, " +
            //     $"Randomize: {totalRandomize:F1} ms, " +
            //     $"Overall: {totalOverall:F1} ms,\n" +
            //     $"BioFailCount: {bioFailCount}, " +
            //     $"RerollCount: {randomRerollCounter}"
            // );
            totalAge = totalTraits = totalSkills =
                totalHealth = totalGene = totalGender =
                    totalWork = totalPassion = totalStyle = totalFinalNotify =
                        totalRedress = totalRelations = totalRandomize = totalOverall = 0;
        }


        public static void ResetRerollCounter() => randomRerollCounter = 0;

        static WorkTags RequiredTagsFromSkill(SkillDef skill)
        {
            WorkTags tags = WorkTags.None;

            foreach (var w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                var rel = w.relevantSkills;
                if (rel != null && rel.Contains(skill))
                    tags |= w.workTags;
            }

            if (skill == SkillDefOf.Shooting || skill == SkillDefOf.Melee)
                tags |= WorkTags.Violent;

            return tags;
        }

        private static void SetRandomAgeInRange(Pawn pawn, IntRange ageRangeYears)
        {
            long minTicks = ageRangeYears.min * GenDate.TicksPerYear;
            long maxTicks = ageRangeYears.max * GenDate.TicksPerYear;

            long randomTicks = (long)Rand.Range(minTicks, maxTicks);

            pawn.ageTracker.AgeBiologicalTicks = randomTicks;
            pawn.ageTracker.AgeChronologicalTicks = randomTicks;
        }

        private static bool CheckBackstoryIsSatisfied(BackstoryDef childBs, BackstoryDef adultBs, WorkTags req)
        {
            WorkTags disabled =
                (childBs?.workDisables ?? WorkTags.None) |
                (adultBs?.workDisables ?? WorkTags.None);

            if ((req & disabled) != WorkTags.None)
                return false;

            switch (PawnFilter.FilterIncapable)
            {
                case PawnFilter.IncapableOptions.NoDumbLabor:
                    return (disabled & WorkTags.ManualDumb) == WorkTags.None;
                case PawnFilter.IncapableOptions.AllowNone:
                    return disabled == WorkTags.None;
                case PawnFilter.IncapableOptions.AllowAll:
                default:
                    return true;
            }
        }

        private static bool CheckPawnIsSatisfied(Pawn pawn)
        {
            return randomRerollCounter >= pawnFilter.RerollLimit
                   || (CheckGenderIsSatisfied(pawn)
                       && CheckSkillsIsSatisfied(pawn)
                       && CheckTraitsIsSatisfied(pawn, WorkTags.None)
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

        private static bool CheckTraitsIsSatisfied(Pawn pawn, WorkTags req)
        {
            if (Page_RandomEditor.MOD_WELL_MET) return true;

            if ((pawn.story.DisabledWorkTagsBackstoryAndTraits & req) != WorkTags.None) return false;
            
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
                    if ((pawn.story.DisabledWorkTagsBackstoryAndTraits & WorkTags.ManualDumb) == WorkTags.ManualDumb)
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
        
        static List<BackstoryCategoryFilter> BuildBackstoryFilters(Pawn pawn, FactionDef factionDef)
        {
            if (pawn.DevelopmentalStage.Baby())
                return new List<BackstoryCategoryFilter> { FRP_NewbornCategoryGroup };

            if (pawn.DevelopmentalStage.Child())
            {
                if (factionDef == FactionDefOf.PlayerTribe)
                    return LifeStageWorker_HumanlikeChild.ChildTribalBackstoryFilters;
                return new List<BackstoryCategoryFilter> { PawnBioAndNameGenerator.ChildCategoryGroup };
            }

            if (!pawn.kindDef.backstoryFiltersOverride.NullOrEmpty())
                return pawn.kindDef.backstoryFiltersOverride;

            var list = new List<BackstoryCategoryFilter>();
            if (!pawn.kindDef.backstoryFilters.NullOrEmpty())
                list.AddRange(pawn.kindDef.backstoryFilters);
            if (!factionDef?.backstoryFilters.NullOrEmpty() ?? false)
                foreach (var b in factionDef.backstoryFilters)
                    if (!list.Contains(b)) list.Add(b);

            if (list.Count == 0)
                list.Add(FRP_FallbackCategoryGroup);

            return list;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<BackstoryCategoryFilter> ApplyHarIfAny(List<BackstoryCategoryFilter> filters, Pawn pawn)
        {
            var defType = pawn.def?.GetType();
            bool isAlienRace = defType != null && defType.FullName == "AlienRace.ThingDef_AlienRace";
            if (isAlienRace && pawn.DevelopmentalStage.Juvenile())
            {
                int index = pawn.DevelopmentalStage.Baby()
                    ? 3
                    : 0; // 0=Child, 3=Newborn
                return HarmonyPatches.LifeStageStartedHelper(filters, pawn, index);
            }

            return filters;
        }
        
        public static bool IsAdult(Pawn pawn)
        {
            float minAdult = (float)MinAgeForAdulthood.GetValue(null);

            if (FasterRandomPlus.hasHAR && MiGetMinAgeForAdulthood != null)
                minAdult = (float)MiGetMinAgeForAdulthood.Invoke(null, new object[] { pawn, minAdult });

            return pawn.ageTracker.AgeBiologicalYearsFloat >= minAdult;
        }

        //PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo()를 대체
        static void SwapBackstoryWithShuffled(Pawn pawn, Faction faction, PawnGenerationRequest req, XenotypeDef xen)
        {
            var filters = BuildBackstoryFilters(pawn, faction?.def);
            if (FasterRandomPlus.hasHAR) filters = ApplyHarIfAny(filters, pawn);

            bool isAdult = IsAdult(pawn);
            
            PawnBioAndNameGenerator.FillBackstorySlotShuffled(
                pawn,
                BackstorySlot.Childhood,
                filters,
                faction.def,
                isAdult ? BackstorySlot.Adulthood : (BackstorySlot?)null
            );
            
            if (isAdult)
            {
                PawnBioAndNameGenerator.FillBackstorySlotShuffled(
                    pawn,
                    BackstorySlot.Adulthood,
                    filters,
                    faction.def
                );
            }
        }
    }
}