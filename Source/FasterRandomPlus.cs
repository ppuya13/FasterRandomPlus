using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        private static readonly ConditionalWeakTable<PawnFilter, StrongBox<bool>> frpSelections =
            new ConditionalWeakTable<PawnFilter, StrongBox<bool>>();
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
            
            PatchRerollLimitOptionValues();

            var edOrigin = AccessTools.Method(typeof(PawnFilter), nameof(PawnFilter.ExposeData));
            var edPostfix = AccessTools.Method(typeof(FasterRandomPlus), nameof(PawnFilterExposeData_Postfix));
            harmony.Patch(edOrigin, postfix:new HarmonyMethod(edPostfix));

            var roOrigin = AccessTools.Method(typeof(PawnFilter), nameof(PawnFilter.ResetOther));
            var roPostfix = AccessTools.Method(typeof(FasterRandomPlus), nameof(ResetOther_Postfix));
            harmony.Patch(roOrigin, postfix:new HarmonyMethod(roPostfix));
            
            var drlOrigin = AccessTools.Method(typeof(PanelOthers), "drawRerollLimit");
            var drlPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(drawRerollLimit_Prefix));
            harmony.Patch(drlOrigin, new HarmonyMethod(drlPrefix));
            
            var draOrigin = AccessTools.Method(typeof(PanelOthers), "drawRerollAlgorithm");
            var draPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(drawRerollAlgorithm_Prefix));
            harmony.Patch(draOrigin, new HarmonyMethod(draPrefix));
            
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

            var dpOrigin = AccessTools.Method(typeof(StartingPawnUtility), nameof(StartingPawnUtility.DrawPortraitArea));
            var dpPostfix = AccessTools.Method(typeof(FasterRandomPlus), nameof(DrawPortraitArea_Postfix));
            harmony.Patch(dpOrigin, postfix: new HarmonyMethod(dpPostfix));
            
            var dwcOrigin = AccessTools.Method(typeof(Page_ConfigureStartingPawns), "DoWindowContents");
            var dwcPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(DoWindowContents_Prefix));
            harmony.Patch(dwcOrigin, new HarmonyMethod(dwcPrefix));

            if (!hasHAR) return;
            
            var pick = AccessTools.Method("AlienRace.HarmonyPatches:PickStartingPawnConfig");
            var pickPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(PickStartingPawnConfigPrefix));
            harmony.Patch(pick, new HarmonyMethod(pickPrefix));

            var getGen = AccessTools.Method(typeof(PawnGenerator), nameof(PawnGenerator.GetXenotypeForGeneratedPawn));
            var getGenPrefix = AccessTools.Method(typeof(FasterRandomPlus), nameof(GetXenotypeForGeneratedPawnPrefix));
            harmony.Patch(getGen, new HarmonyMethod(getGenPrefix));
            
        }
        
        static bool RerollPrefix(int pawnIndex)
        {
            if (!IsFrpSelected(RandomSettings.PawnFilter)
                || Find.WindowStack.currentlyDrawnWindow is Dialog_ChooseNewWanderers)
                return true;

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
                if (!ResetPawnState(pawnIndex)) return false;
                // OptimizedRandomSettings.Reroll(pawnIndex);
                RerollJob.Start(pawnIndex, OptimizedRandomSettings.PawnFilter.RerollLimit);
                RandomSettings.randomRerollCounter = OptimizedRandomSettings.randomRerollCounter;
            }
            finally
            {
                // isRerolling = false;
                sw.Stop();
                
                if(restoreUIRequest)
                    restoreUIRequest = false;
                
                // Log.Message(
                //     $"[FasterRandomPlus] Optimized Reroll: {sw.Elapsed.TotalSeconds:F2}sec");
            }

            return false;
        }

        static bool ResetPawnState(int pawnIndex)
        {
            try
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
                if (ModsConfig.BiotechActive)
                {
                    // OptimizedRandomSettings.RemoveAllGenesWithSideEffects(pawn);
                    pawn.genes = new Pawn_GeneTracker(pawn);
                }
                
                RandomSettings.GeneratePawnStyle(pawn);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[FasterRandomPlus] ResetPawnState failed: " + e);
                isRerolling = false;
                return false;
            }
        }
        
        private static bool IsFrpSelected(PawnFilter filter)
        {
            return frpSelections.GetValue(filter, _ => new StrongBox<bool>(true)).Value;
        }

        private static void SetFrpSelected(PawnFilter filter, bool selected)
        {
            frpSelections.GetValue(filter, _ => new StrongBox<bool>(true)).Value = selected;
        }
        
        static void PawnFilterExposeData_Postfix(PawnFilter __instance)
        {
            bool selected = IsFrpSelected(__instance);

            Scribe_Values.Look(
                ref selected,
                "fasterRandomPlusSelected",
                true
            );

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                SetFrpSelected(__instance, selected);
            }
        }

        static void ResetOther_Postfix(PawnFilter __instance)
        {
            // __instance.RerollAlgorithm = PawnFilter.RerollAlgorithmOptions.Normal;
            SetFrpSelected(__instance, true);
        }
        
        static bool SkipGeneratePawnRelations(Pawn pawn, ref PawnGenerationRequest request)
        {
            return !isRerolling;
        }

        static bool SetGenerationRequestPrefix(ref PawnGenerationRequest request)
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
        
        static bool GetXenotypeForGeneratedPawnPrefix(
            PawnGenerationRequest request,
            ref XenotypeDef __result)
        {
            if (isRerolling && ModsConfig.BiotechActive && hasHAR
                && OptimizedRandomSettings.SelectAny)
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
            if (!IsFrpSelected(RandomSettings.PawnFilter)) return;
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

        static void DrawPortraitArea_Postfix(
            Rect rect,
            int pawnIndex,
            bool renderClothes,
            bool renderHeadgear
        )
        {
            if (!isRerolling) return;
            
            RerollJob.TickStep();
            
            Rect rectArea = rect;
            rectArea.xMin -= 20f;
            rectArea.xMax += 20f;
            rectArea.yMin -= 20f;
            rectArea.yMax += 20f;

            Widgets.DrawWindowBackground(rectArea);

            Widgets.ButtonInvisible(rectArea, doMouseoverSound: false);

            float winW = Mathf.Min(rectArea.width - 60f, 520f);
            float winH = 120f;
            Rect win = new Rect(rectArea.center.x - winW / 2f, rectArea.center.y - winH / 2f, winW, winH);

            Widgets.DrawShadowAround(win);
            Widgets.DrawWindowBackground(win);

            Rect inner = win.ContractedBy(14f);

            var oldAnchor = Text.Anchor;
            var oldFont = Text.Font;
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = GameFont.Medium;

            Widgets.Label(inner, $"{OptimizedRandomSettings.randomRerollCounter}/{OptimizedRandomSettings.PawnFilter.RerollLimit} Rerolls...");

            Text.Anchor = oldAnchor;
            Text.Font = oldFont;
        }


        static void DoWindowContents_Prefix(Page_ConfigureStartingPawns __instance, Rect rect)
        {
            if (!isRerolling) return;

            var e = Event.current;
            if (e == null) return;

            if (!rect.Contains(e.mousePosition)) return;

            switch (e.type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.ScrollWheel:
                case EventType.KeyDown:
                case EventType.ExecuteCommand:
                    e.Use();
                    break;
            }
        }

        static void PatchRerollLimitOptionValues()
        {
            var fi = AccessTools.Field(typeof(PawnFilter), nameof(PawnFilter.RerollLimitOptionValues));

            var current = fi.GetValue(null) as string[];
            if (current == null)
            {
                Log.Error("[FasterRandomPlus] RerollLimitOptionValues is null");
                return;
            }

            var patched = current
                .Concat(new[]
                {
                    "100000",
                    "250000",
                    "500000"
                })
                .Distinct()
                .ToArray();
            fi.SetValue(null, patched);
        }
        
        static bool drawRerollLimit_Prefix(PanelOthers __instance, Rect rect)
        {
            var current = RandomSettings.PawnFilter.RerollLimit.ToString();

            if (!Widgets.ButtonText(rect, current))
                return false;

            var options = new List<FloatMenuOption>();

            foreach (var val in PawnFilter.RerollLimitOptionValues)
            {
                string label = val;

                options.Add(new FloatMenuOption(label, () =>
                {
                    RandomSettings.PawnFilter.RerollLimit = int.Parse(val);
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options));

            return false;
        }
        
        static bool drawRerollAlgorithm_Prefix(PanelOthers __instance, Rect rect)
        {
            PawnFilter filter = RandomSettings.PawnFilter;
            bool frpSelected = IsFrpSelected(filter);

            var originalOptions = PawnFilter.RerollAlgorithmOptionValues;
            int selectedIndex = (int)filter.RerollAlgorithm;

            string currentLabel;

            if (frpSelected)
            {
                currentLabel = "FasterRandomPlus";
            }
            else if (selectedIndex >= 0 && selectedIndex < originalOptions.Length)
            {
                currentLabel = originalOptions[selectedIndex]
                    .Translate().CapitalizeFirst().ToString();
            }
            else
            {
                currentLabel = filter.RerollAlgorithm.ToString();
            }

            if (!Widgets.ButtonText(rect, currentLabel))
                return false;

            var options = new List<FloatMenuOption>();

            for (int i = 0; i < originalOptions.Length; i++)
            {
                int index = i;

                string label = originalOptions[i]
                    .Translate().CapitalizeFirst().ToString();

                options.Add(new FloatMenuOption(label, () =>
                {
                    filter.RerollAlgorithm =
                        (PawnFilter.RerollAlgorithmOptions)index;

                    SetFrpSelected(filter, false);
                }));
            }

            options.Add(new FloatMenuOption("FasterRandomPlus", () =>
            {
                filter.RerollAlgorithm = PawnFilter.RerollAlgorithmOptions.Normal;
                SetFrpSelected(filter, true);
            }));

            Find.WindowStack.Add(new FloatMenu(options));
            return false;
        }
    }
}