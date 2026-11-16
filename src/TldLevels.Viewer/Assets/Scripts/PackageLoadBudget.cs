using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace TldLevels.Viewer
{
    /// <summary>
    /// Frame-time budget shared by every package import.
    /// <para>
    /// glTFast calls <see cref="IDeferAgent"/> at points where it can pause, and
    /// its own <see cref="TimeBudgetPerFrameDeferAgent"/> cannot be shared
    /// between a cell import and the asset imports running beside it. This
    /// agent lets all of them draw from one 1/30 s allowance, so a frame spent
    /// starting a new cell still leaves the rest of the loop to the game.
    /// </para>
    /// <para>
    /// The owning update loop calls <see cref="BeginFrame"/> once per frame so
    /// the allowance is restored even in batch mode, where
    /// <see cref="Time.frameCount"/> may not advance. The lazy frame-count
    /// check remains as a fallback for callers that only poll.
    /// </para>
    /// </summary>
    public sealed class PackageLoadBudget : IDeferAgent
    {
        public const float DefaultBudgetSeconds = 1f / 30f;

        private readonly double budgetSeconds;
        private int frame = -1;
        private double frameStart;

        public PackageLoadBudget(float budgetSeconds = DefaultBudgetSeconds)
        {
            this.budgetSeconds = Mathf.Max(0.001f, budgetSeconds);
        }

        public float BudgetSeconds => (float)budgetSeconds;

        /// <summary>Starts a new frame window with the full allowance.</summary>
        public void BeginFrame()
        {
            frame = Time.frameCount;
            frameStart = Time.realtimeSinceStartupAsDouble;
        }

        /// <summary>Seconds still available in the current frame.</summary>
        public double Remaining
        {
            get
            {
                RefreshFrame();
                return budgetSeconds - (Time.realtimeSinceStartupAsDouble - frameStart);
            }
        }

        public bool HasTime => Remaining > 0d;

        /// <summary>
        /// True when a predicted chunk of work would overrun the budget.
        /// </summary>
        public bool ShouldDefer() => Remaining <= 0d;

        public bool ShouldDefer(float duration) => duration >= Remaining;

        public async Task BreakPoint()
        {
            if (ShouldDefer())
            {
                await Task.Yield();
            }
        }

        public async Task BreakPoint(float duration)
        {
            if (ShouldDefer(duration))
            {
                await Task.Yield();
            }
        }

        private void RefreshFrame()
        {
            int current = Time.frameCount;
            if (current == frame)
            {
                return;
            }

            frame = current;
            frameStart = Time.realtimeSinceStartupAsDouble;
        }
    }
}
