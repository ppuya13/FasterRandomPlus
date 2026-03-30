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
    }
}