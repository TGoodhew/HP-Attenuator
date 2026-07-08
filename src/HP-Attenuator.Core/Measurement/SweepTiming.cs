using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HpAttenuator.Measurement
{
    /// <summary>
    /// Attributes sweep wall-clock to categories so the real hotspot can be identified before optimizing
    /// (issue #2 — "profile first"). Each category accumulates elapsed milliseconds and a call count; the
    /// harness prints the breakdown under <c>--profile</c>. The point is to prove where the time goes —
    /// settled reads almost always dominate, fixed <c>Thread.Sleep</c> waits (post-calibrate, per-step
    /// settle) are the next candidates, and per-command GPIB I/O (batching) is usually negligible unless
    /// <c>--debug</c>'s per-command serial poll is on. Uses <see cref="Stopwatch"/> (monotonic), so it
    /// adds no measurable overhead to the measurement itself.
    /// </summary>
    public sealed class SweepTiming
    {
        // Canonical category names (stable so the report ordering/labels are predictable).
        public const string RangeCal = "range-cal pre-pass";
        public const string AttenSet = "attenuator set";
        public const string Read = "settled read";
        public const string Settle = "per-step settle";
        public const string Level = "reference level";

        private readonly Dictionary<string, long> _ms = new Dictionary<string, long>();
        private readonly Dictionary<string, int> _n = new Dictionary<string, int>();

        /// <summary>Total wall-clock measured across all categories, ms.</summary>
        public long TotalMs => _ms.Values.Sum();

        /// <summary>Record a pre-measured block of <paramref name="ms"/> in <paramref name="category"/>.</summary>
        public void Add(string category, long ms, int count = 1)
        {
            _ms.TryGetValue(category, out long m); _ms[category] = m + ms;
            _n.TryGetValue(category, out int c); _n[category] = c + count;
        }

        /// <summary>Times <paramref name="f"/>, accrues it to <paramref name="category"/>, returns its result.</summary>
        public T Time<T>(string category, Func<T> f)
        {
            var sw = Stopwatch.StartNew();
            try { return f(); }
            finally { sw.Stop(); Add(category, sw.ElapsedMilliseconds); }
        }

        /// <summary>Times <paramref name="a"/> and accrues it to <paramref name="category"/>.</summary>
        public void Time(string category, Action a)
        {
            var sw = Stopwatch.StartNew();
            try { a(); }
            finally { sw.Stop(); Add(category, sw.ElapsedMilliseconds); }
        }

        /// <summary>Folds another timing's totals into this one (to aggregate per-frequency into a sweep).</summary>
        public void Merge(SweepTiming other)
        {
            if (other == null) return;
            foreach (var kv in other._ms) Add(kv.Key, kv.Value, other._n.TryGetValue(kv.Key, out int c) ? c : 0);
        }

        /// <summary>Categories with their (ms, count), busiest first — for the <c>--profile</c> report.</summary>
        public IEnumerable<(string category, long ms, int count)> Breakdown() =>
            _ms.OrderByDescending(kv => kv.Value)
               .Select(kv => (kv.Key, kv.Value, _n.TryGetValue(kv.Key, out int c) ? c : 0));
    }
}
