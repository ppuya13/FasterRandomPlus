using System.Diagnostics;
using RandomPlus;
using RimWorld;
using Verse;

namespace FasterRandomPlus.Source
{
    public enum RerollState
    {
        Idle,
        Running,
        Completed,
        Cancelled,
        Error
    }

    public static class RerollJob
    {
        public static RerollState State { get; private set; } = RerollState.Idle;
        public static int PawnIndex { get; private set; } = -1;
        public static int Limit { get; private set; } = 0;
        public static int Counter => OptimizedRandomSettings.randomRerollCounter;

        public static int StepBudgetPerFrame = 1000; // 프레임당 최대 시도 수
        public static double TimeBudgetMs = 6.0; // 프레임당 최대 실행 시간

        private static Stopwatch _frameSw;
        private static Stopwatch _totalSw;

        public static void Start(int pawnIndex, int limit)
        {
            if (State == RerollState.Running) return;

            FasterRandomPlus.isRerolling = true;
            PawnIndex = pawnIndex;
            Limit = limit;
            
            if (_totalSw == null) _totalSw = new Stopwatch();
            _totalSw.Restart();

            OptimizedRandomSettings.BeginReroll(PawnIndex, Limit);
            State = RerollState.Running;
        }

        public static void Cancel()
        {
            if (State != RerollState.Running) return;
            State = RerollState.Cancelled;
            OptimizedRandomSettings.AbortReroll();
            OptimizedRandomSettings.ClearCache();
            FasterRandomPlus.isRerolling = false;
            
            if (_totalSw != null && _totalSw.IsRunning)
            {
                _totalSw.Stop();
                Log.Message($"[FasterRandomPlus] Reroll Cancelled after {_totalSw.Elapsed.TotalSeconds:F2} sec");
            }
        }

        public static void End()
        {
            System.Exception exception = null;

            try
            {
                OptimizedRandomSettings.EndReroll();
                State = RerollState.Completed;
            }
            catch (System.Exception ex)
            {
                exception = ex;
                State = RerollState.Error;
            }
            finally
            {
                try
                {
                    SocialCardUtility.ClearCaches();
                    OptimizedRandomSettings.ClearCache();
                }
                catch (System.Exception ex)
                {
                    Log.Warning("[FasterRandomPlus] ClearCaches failed: " + ex);
                }
                finally
                {
                    FasterRandomPlus.isRerolling = false;
                    _totalSw?.Stop();
                }
            }

            if (State == RerollState.Completed)
            {
                Log.Message($"[FasterRandomPlus] Reroll Completed: {_totalSw?.Elapsed.TotalSeconds ?? 0:F2} sec");
            }
            else
            {
                Log.Error("[FasterRandomPlus] EndReroll failed: " + exception);
            }
        }

        public static void TickStep()
        {
            if (State != RerollState.Running) return;

            if (_frameSw == null) _frameSw = new Stopwatch();
            _frameSw.Restart();

            int steps = 0;
            while (steps < StepBudgetPerFrame && _frameSw.Elapsed.TotalMilliseconds < TimeBudgetMs)
            {
                var finished = OptimizedRandomSettings.StepOnce();
                steps++;
                
                RandomSettings.randomRerollCounter = OptimizedRandomSettings.randomRerollCounter;
                
                if (finished)
                {
                    End();
                    break;
                }
            }
        }
    }
}