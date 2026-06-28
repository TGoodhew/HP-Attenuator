using System;
using System.Globalization;
using HpAttenuator.Visa;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// Driver for the HP 8673B Synthesized Signal Generator used as the external LO
    /// for the 11793A converter. HP-IB codes (8673B Operating manual): FR &lt;val&gt; MZ
    /// (frequency), LE &lt;val&gt; DM (level, dBm), RF1/RF0, IP.
    /// Frequency range 2-26.5 GHz.
    /// </summary>
    public sealed class Hp8673B : ILocalOscillator
    {
        private readonly IInstrumentLink _link;

        public Hp8673B(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

        public string ResourceName => _link.ResourceName;

        public double MinFrequencyMHz => 2000.0;
        public double MaxFrequencyMHz => 26500.0;

        public void Preset() => _link.Write("IP");

        public void SetFrequencyMHz(double mhz) =>
            _link.Write("FR " + mhz.ToString("0.######", CultureInfo.InvariantCulture) + " MZ");

        public void SetPowerDbm(double dbm) =>
            _link.Write("LE " + dbm.ToString("0.###", CultureInfo.InvariantCulture) + " DM");

        public void RfOn() => _link.Write("RF1");

        public void RfOff() => _link.Write("RF0");
    }
}
