using System;
using System.Globalization;
using HpAttenuator.Visa;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// Driver for the HP 8340B Synthesized Sweeper used as a CW signal source.
    /// HP-IB codes (8340B Operating manual, Table 3-2): CW &lt;val&gt; MZ (MHz),
    /// PL &lt;val&gt; DB (the DB terminator is dB(m)), RF1/RF0, IP.
    /// </summary>
    public sealed class Hp8340B : ISignalSource
    {
        private readonly IInstrumentLink _link;

        public Hp8340B(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

        public string ResourceName => _link.ResourceName;

        public void Preset() => _link.Write("IP");

        public void SetFrequencyMHz(double mhz) =>
            _link.Write("CW " + mhz.ToString("0.######", CultureInfo.InvariantCulture) + " MZ");

        public void SetPowerDbm(double dbm) =>
            _link.Write("PL " + dbm.ToString("0.###", CultureInfo.InvariantCulture) + " DB");

        public void RfOn() => _link.Write("RF1");

        public void RfOff() => _link.Write("RF0");
    }
}
