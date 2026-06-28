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
        public static AttenuatorConfig Default()
        {
            return new AttenuatorConfig(
                "HP 8494 (0-11 dB)",
                "HP 8496 (0-110 dB)",
                new[]
                {
                    new Section(1, 1),
                    new Section(2, 2),
                    new Section(3, 4),
                    new Section(4, 4),
                },
                new[]
                {
                    new Section(5, 10),
                    new Section(6, 20),
                    new Section(7, 40),
                    new Section(8, 40),
                });
        }
    }
}
