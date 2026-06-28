using System.Collections.Generic;
using System.Linq;

namespace HpAttenuator.Model
{
    /// <summary>
    /// Tracks the last-commanded state of the driver. The 11713A cannot be queried
    /// (it is listen-only), so this is a software shadow of what we have sent.
    /// </summary>
    public sealed class DeviceState
    {
        /// <summary>Digits (1-8) of currently engaged attenuator sections.</summary>
        public HashSet<int> Engaged { get; } = new HashSet<int>();

        /// <summary>S9 switch: true = A9, false = B9, null = not yet set.</summary>
        public bool? Switch9 { get; set; }

        /// <summary>S0 switch: true = A0, false = B0, null = not yet set.</summary>
        public bool? Switch0 { get; set; }

        public int TotalDecibels(AttenuatorConfig config) =>
            config.AllSections.Where(s => Engaged.Contains(s.Digit)).Sum(s => s.Decibels);

        public int BankDecibels(IEnumerable<Section> bank) =>
            bank.Where(s => Engaged.Contains(s.Digit)).Sum(s => s.Decibels);
    }
}
