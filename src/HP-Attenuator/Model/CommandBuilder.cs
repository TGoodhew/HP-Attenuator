using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HpAttenuator.Model
{
    /// <summary>
    /// Translates desired attenuation into 11713A data strings.
    ///
    /// 11713A data-message format (manual section 6, "Data Message Input Format"):
    ///   [Adm][Bdn]   where A/a = general ON (insert section), B/b = general OFF
    ///   (bypass section), and each d is a relay digit 0-9. A digit used in the A
    ///   field may not also appear in the B field. ATTEN X = digits 1-4, ATTEN Y =
    ///   digits 5-8, with independent RF switches on S9 and S0.
    /// </summary>
    public static class CommandBuilder
    {
        /// <summary>
        /// Finds the set of section digits to engage so the engaged sections sum to
        /// <paramref name="targetDb"/>. Returns null when the target is unreachable.
        /// Prefers the solution using the fewest sections.
        /// </summary>
        public static List<int> Solve(IReadOnlyList<Section> sections, int targetDb)
        {
            int n = sections.Count;
            List<int> best = null;
            for (int mask = 0; mask < (1 << n); mask++)
            {
                int sum = 0;
                for (int i = 0; i < n; i++)
                    if ((mask & (1 << i)) != 0) sum += sections[i].Decibels;

                if (sum != targetDb) continue;

                var engaged = new List<int>();
                for (int i = 0; i < n; i++)
                    if ((mask & (1 << i)) != 0) engaged.Add(sections[i].Digit);

                if (best == null || engaged.Count < best.Count)
                    best = engaged;
            }
            return best;
        }

        /// <summary>
        /// Builds an A/B data string over <paramref name="sections"/>, engaging the
        /// digits in <paramref name="engagedDigits"/> (A field) and bypassing the rest
        /// (B field). Sections not present in <paramref name="sections"/> are not
        /// addressed and retain their state on the driver.
        /// </summary>
        public static string BuildString(IEnumerable<Section> sections, ISet<int> engagedDigits)
        {
            var ordered = sections.OrderBy(s => s.Digit).ToList();
            var engaged = ordered.Where(s => engagedDigits.Contains(s.Digit)).Select(s => s.Digit).ToList();
            var bypassed = ordered.Where(s => !engagedDigits.Contains(s.Digit)).Select(s => s.Digit).ToList();

            var sb = new StringBuilder();
            if (engaged.Count > 0)
            {
                sb.Append('A');
                foreach (var d in engaged) sb.Append(d);
            }
            if (bypassed.Count > 0)
            {
                sb.Append('B');
                foreach (var d in bypassed) sb.Append(d);
            }
            return sb.ToString();
        }

        /// <summary>Command that drives independent switch S9 (true = A9, false = B9).</summary>
        public static string Switch9(bool on) => on ? "A9" : "B9";

        /// <summary>Command that drives independent switch S0 (true = A0, false = B0).</summary>
        public static string Switch0(bool on) => on ? "A0" : "B0";

        /// <summary>
        /// True if <paramref name="command"/> contains only legal 11713A data-string
        /// characters (A/a, B/b, digits 0-9, and whitespace).
        /// </summary>
        public static bool IsValidDataString(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            return command.All(c => c == 'A' || c == 'a' || c == 'B' || c == 'b'
                                    || (c >= '0' && c <= '9') || char.IsWhiteSpace(c));
        }
    }
}
