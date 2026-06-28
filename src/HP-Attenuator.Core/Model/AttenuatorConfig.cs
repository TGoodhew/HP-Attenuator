using System.Collections.Generic;
using System.Linq;

namespace HpAttenuator.Model
{
    /// <summary>
    /// Describes which step attenuators are wired to the 11713A and the dB weight
    /// of each section. ATTEN X uses relay digits 1-4, ATTEN Y uses digits 5-8.
    /// </summary>
    public sealed class AttenuatorConfig
    {
        public string XModel { get; set; }
        public string YModel { get; set; }

        /// <summary>ATTEN X sections (digits 1-4).</summary>
        public List<Section> X { get; }

        /// <summary>ATTEN Y sections (digits 5-8).</summary>
        public List<Section> Y { get; }

        public AttenuatorConfig(string xModel, string yModel, IEnumerable<Section> x, IEnumerable<Section> y)
        {
            XModel = xModel;
            YModel = yModel;
            X = x.ToList();
            Y = y.ToList();
        }

        public IEnumerable<Section> AllSections => X.Concat(Y);

        public int MaxDecibels => AllSections.Sum(s => s.Decibels);

        public Section ForDigit(int digit) => AllSections.FirstOrDefault(s => s.Digit == digit);

        /// <summary>
        /// Default configuration: an HP 8494 (0-11 dB, 1 dB steps) on ATTEN X and an
        /// HP 8496 (0-110 dB, 10 dB steps) on ATTEN Y. This is the classic 11713A
        /// 4-section pairing from Table 6-3 of the 11713A manual, giving a combined
        /// 0-121 dB range in 1 dB steps.
        /// </summary>
        public static AttenuatorConfig Default() => Build(xIs8494: true);

        /// <summary>The swapped wiring: the 8496 (10 dB) on ATTEN X and the 8494 (1 dB) on ATTEN Y.</summary>
        public static AttenuatorConfig Swapped() => Build(xIs8494: false);

        /// <summary>
        /// Builds a config for the two standard step attenuators. The 8494/8496 can be
        /// cabled to either 11713A port, so <paramref name="xIs8494"/> selects which
        /// physical attenuator is on ATTEN X. The relay digits are fixed by the 11713A
        /// (1-4 = ATTEN X, 5-8 = ATTEN Y); only the dB weights/model swap.
        /// </summary>
        public static AttenuatorConfig Build(bool xIs8494)
        {
            string fineModel = "HP 8494 (0-11 dB)";
            string coarseModel = "HP 8496 (0-110 dB)";
            int[] fine = { 1, 2, 4, 4 };
            int[] coarse = { 10, 20, 40, 40 };

            int[] x = xIs8494 ? fine : coarse;
            int[] y = xIs8494 ? coarse : fine;

            return new AttenuatorConfig(
                xIs8494 ? fineModel : coarseModel,
                xIs8494 ? coarseModel : fineModel,
                new[] { new Section(1, x[0]), new Section(2, x[1]), new Section(3, x[2]), new Section(4, x[3]) },
                new[] { new Section(5, y[0]), new Section(6, y[1]), new Section(7, y[2]), new Section(8, y[3]) });
        }
    }
}
