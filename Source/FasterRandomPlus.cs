using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FasterRandomPlus.Source;
using HarmonyLib;
using RandomPlus;
using RimWorld;
using UnityEngine;
using Verse;
using AlienRace;

namespace FasterRandomPlus
{
    [StaticConstructorOnStartup]
    public static class FasterRandomPlus
    {
        private static readonly Harmony harmony = new Harmony("balensiad.FasterRandomPlus");
        public static readonly bool hasHAR;
        
        static int lastRerollFrame = -1;
        static bool alreadyOptimized = false;
        internal static bool isRerolling = false;
        internal static PawnGenerationRequest savedRequest;
        internal static bool restoreUIRequest = false;

        static FasterRandomPlus()
        {
            var parts = VersionControl.CurrentVersionString.Split('.');
            int.TryParse(parts[0], out lastRerollFrame);
            
            var modType = Type.GetType("AlienRace.AlienRaceMod, AlienRace");
            if (modType != null)
            {
                var settings = modType.GetField("settings", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                if (settings != null)
                    hasHAR = (bool)settings.GetType()
                        .GetField("randomizeStartingPawnsOnReroll", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(settings);
            }
            Log.Message(hasHAR
                ? "[FasterRandomPlus] HAR found"
                : "[FasterRandomPlus] HAR not found");
            
            OptimizedRandomSettings.Init();
            
            var gpRelOrigin = AccessTools.Method(typeof(PawnGenerator), "GeneratePawnRelations", new[] { typeof(Pawn), typeof(PawnGenerationRequest).MakeByRefType() });
            var rpRelPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(SkipGeneratePawnRelations));
            harmony.Patch( gpRelOrigin, new HarmonyMethod(rpRelPrefix) );
            
            var rerollOrigin = AccessTools.Method(typeof(RandomSettings), "Reroll");
            var rerollPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(RerollPrefix));
            harmony.Patch(rerollOrigin, new HarmonyMethod(rerollPrefix));

            var setReq = AccessTools.Method(typeof(StartingPawnUtility), nameof(StartingPawnUtility.SetGenerationRequest));
            var setReqPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(SetGenerationRequestPrefix));
            harmony.Patch(setReq, new HarmonyMethod(setReqPrefix));
            
            var setupReq = AccessTools.Method(typeof(CharacterCardUtility), "SetupGenerationRequest");
            var setupReqPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(SetupGenerationRequestPrefix));
            harmony.Patch(setupReq, new HarmonyMethod(setupReqPrefix));

            if (!hasHAR) return;
            
            var pick = AccessTools.Method("AlienRace.HarmonyPatches:PickStartingPawnConfig");
            var pickPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(PickStartingPawnConfigPrefix));
            harmony.Patch(pick, new HarmonyMethod(pickPrefix));

            var getGen = AccessTools.Method(typeof(PawnGenerator), nameof(PawnGenerator.GetXenotypeForGeneratedPawn));
            var getGenPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(GetXenotypeForGeneratedPawnPrefix));
            harmony.Patch(getGen, new HarmonyMethod(getGenPrefix));
        }
        
        public static bool RerollPrefix(int pawnIndex)
        {
            int thisFrame = Time.frameCount;
            if (lastRerollFrame != thisFrame)
            {
                lastRerollFrame = thisFrame;
                alreadyOptimized = false;
            }
            
            if (alreadyOptimized) return false;
            alreadyOptimized = true;

            if (isRerolling) return false;
            isRerolling = true;

            if (ModsConfig.BiotechActive && hasHAR)
            {
                var req = StartingPawnUtility.GetGenerationRequest(pawnIndex);
                savedRequest = req;
                restoreUIRequest = true;
                
                bool harAny = req.AllowedXenotypes != null && req.AllowedXenotypes.Count > 0;
                OptimizedRandomSettings.SelectAny = harAny;
                
                if (harAny)
                {
                    var hpType = Type.GetType("AlienRace.HarmonyPatches, AlienRace");
                    var field  = hpType?.GetField("currentStartingRequest", BindingFlags.Static | BindingFlags.Public);
                    field?.SetValue(null, default(PawnGenerationRequest));
                }
            }
            
            var sw = Stopwatch.StartNew();
            try
            {
                OptimizedRandomSettings.PawnFilter = RandomSettings.PawnFilter;
                OptimizedRandomSettings.ResetRerollCounter();
                ResetPawnState(pawnIndex);
                OptimizedRandomSettings.Reroll(pawnIndex);
                RandomSettings.randomRerollCounter = OptimizedRandomSettings.randomRerollCounter;
            }
            finally
            {
                isRerolling = false;
                sw.Stop();
                
                if(restoreUIRequest)
                    restoreUIRequest = false;
                
                Log.Message(
                    $"[FasterRandomPlus] Optimized Reroll: {sw.Elapsed.TotalSeconds:F2}sec");
            }

            return false;
        }

        static void ResetPawnState(int pawnIndex)
        {
            var pi = typeof(StartingPawnUtility)
                .GetProperty("StartingAndOptionalPawns", BindingFlags.NonPublic | BindingFlags.Static);
            var pawns = (List<Pawn>)pi.GetValue(null);
            var pawn = pawns[pawnIndex];

            pawn.relations.ClearAllRelations();
            pawn.health.hediffSet.hediffs.Clear();
            pawn.story.traits.allTraits.Clear();
            pawn.skills = new Pawn_SkillTracker(pawn);
            pawn.workSettings?.EnableAndInitialize();
            if (ModsConfig.BiotechActive) pawn.genes = new Pawn_GeneTracker(pawn);

            RandomSettings.GeneratePawnStyle(pawn);
        }
        
        public static bool SkipGeneratePawnRelations(Pawn pawn, ref PawnGenerationRequest request)
        {
            return !isRerolling;
        }

        public static bool SetGenerationRequestPrefix(ref PawnGenerationRequest request)
        {
            if (restoreUIRequest)
            {
                request = savedRequest;
            }

            return true;
        }
        
        static bool PickStartingPawnConfigPrefix(
            PawnKindDef kindDef,
            out XenotypeDef xenotypeDef,
            out CustomXenotype xenotypeCustom,
            out DevelopmentalStage devStage,
            out bool allowDowned)
        {
            xenotypeDef = XenotypeDefOf.Baseliner;
            xenotypeCustom = null;
            devStage = DevelopmentalStage.Adult;
            allowDowned = false;

            if (!isRerolling)
                return true;

            if (OptimizedRandomSettings.SelectAny)
            {
                var choices = DefDatabase<XenotypeDef>.AllDefsListForReading
                    .Where(x => RaceRestrictionSettings.CanUseXenotype(x, kindDef.race))
                    .ToList();
                xenotypeDef = choices.RandomElement();
                return false;
            }

            return true;
        }
        
        public static bool GetXenotypeForGeneratedPawnPrefix(
            PawnGenerationRequest request,
            ref XenotypeDef __result)
        {
            if (ModsConfig.BiotechActive && hasHAR && OptimizedRandomSettings.SelectAny)
            {
                var pool = DefDatabase<XenotypeDef>.AllDefsListForReading
                    .Where(x => RaceRestrictionSettings.CanUseXenotype(x, request.KindDef.race))
                    .ToList();
                __result = pool.RandomElement();
                return false;
            }
            return true;
        }

        static void SetupGenerationRequestPrefix(
            int index,
            XenotypeDef xenotype,
            CustomXenotype customXenotype,
            List<XenotypeDef> allowedXenotypes,
            float forceBaselinerChance,
            Func<PawnGenerationRequest, bool> validator,
            ref Action randomizeCallback,
            bool randomize
        )
        {
            if (xenotype != null) OptimizedRandomSettings.SelectAny = false;
            
            if (ModsConfig.BiotechActive && xenotype != null && randomize)
            {
                var flagField = AccessTools.Field(
                    typeof(CharacterCardUtility),
                    "warnedChangingXenotypeWillRandomizePawn"
                );
                flagField.SetValue(null, false);

                void VanillaOnce()
                {
                    var pi = AccessTools.Property(typeof(StartingPawnUtility), "StartingAndOptionalPawns");
                    var pawns = (List<Pawn>)pi.GetValue(null);
                    var pawn = pawns[index];

                    SpouseRelationUtility.Notify_PawnRegenerated(pawn);
                    StartingPawnUtility.RandomizeInPlace(pawn);

                    var req = StartingPawnUtility.GetGenerationRequest(index);
                    var newXen = PawnGenerator.GetXenotypeForGeneratedPawn(req);
                    PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo(pawn, req.Faction.def, req, newXen);
                }

                randomizeCallback = VanillaOnce;
            }
        }
    }
}