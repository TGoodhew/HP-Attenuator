using System;
using System.Globalization;
using System.IO;

namespace HpAttenuator.TestHarness
{
    /// <summary>
    /// Tracks when the 8902A power sensor was last zeroed + calibrated, so the cal can be
    /// done once per session and reused. The 8902A keeps its sensor cal while powered, so a
    /// recent marker means the resident cal is still good (the manual recommends recalibrating
    /// roughly every 8 hours or on ambient change). Stored as a timestamp file in the temp dir.
    /// </summary>
    internal static class SensorCalSession
    {
        private static string MarkerPath =>
            Path.Combine(Path.GetTempPath(), "hp-attenuator-sensorcal.marker");

        /// <summary>Records that a sensor cal just completed successfully.</summary>
        public static void Mark()
        {
            try { File.WriteAllText(MarkerPath, DateTime.Now.ToString("o", CultureInfo.InvariantCulture)); }
            catch { /* best effort */ }
        }

        /// <summary>The time of the last recorded sensor cal, or null if none.</summary>
        public static DateTime? LastCal()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return null;
                string s = File.ReadAllText(MarkerPath).Trim();
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
                    return t;
            }
            catch { /* treat as no marker */ }
            return null;
        }

        /// <summary>True if a sensor cal was done within <paramref name="maxAge"/> (and not in the future).</summary>
        public static bool IsFresh(TimeSpan maxAge)
        {
            DateTime? t = LastCal();
            if (!t.HasValue) return false;
            TimeSpan age = DateTime.Now - t.Value;
            return age >= TimeSpan.Zero && age <= maxAge;
        }
    }
}
