using RimWorld;
using Verse;

namespace FasterRandomPlus.Source
{
    //이거 진짜 하기 싫었는데 방법이 안보인다
    public static class RaceFix
    {
        public static void Apply(Pawn pawn)
        {
            if (pawn?.story == null || pawn.abilities == null)
                return;

            ApplyRatkin(pawn);
            // ApplyCharlotte(pawn);
        }

        #region Ratkin

        //랫킨 수녀에게 기도회 추가
        private static void ApplyRatkin(Pawn pawn)
        {
            var adult = pawn.story.Adulthood;
            if (adult == null) return;
            
            if (adult.defName != "Ratkin_Sister")
                return;
            
            var abilityDef = DefDatabase<AbilityDef>.GetNamedSilentFail("RK_PrayerService");
            if (abilityDef == null)
                return;
            
            pawn.abilities.GainAbility(abilityDef);
        }

        #endregion
        
        // #region Charlotte
        //
        // //샤롯에게 고통의 환희 특성 및 전용 능력 추가
        // private static void ApplyCharlotte(Pawn pawn)
        // {
        //     if (pawn.genes?.Xenotype?.defName != "Charlotte_Xenotype")
        //         return;
        //
        //     var painTrait = DefDatabase<TraitDef>.GetNamedSilentFail("Charlotte_PainEcstasyTrait");
        //     if (painTrait != null && !pawn.story.traits.HasTrait(painTrait))
        //         pawn.story.traits.GainTrait(new Trait(painTrait));
        //
        //     var surgeAbility = DefDatabase<AbilityDef>.GetNamedSilentFail("Charlotte_CrimsonSurge");
        //     if (surgeAbility != null && pawn.abilities.GetAbility(surgeAbility) == null)
        //         pawn.abilities.GainAbility(surgeAbility);
        //
        //     var summonAbility = DefDatabase<AbilityDef>.GetNamedSilentFail("Charlotte_SummonFamiliar");
        //     if (summonAbility != null && pawn.abilities.GetAbility(summonAbility) == null)
        //         pawn.abilities.GainAbility(summonAbility);
        // }
        //
        // #endregion
    }
}