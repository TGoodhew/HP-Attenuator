using System.Collections.Generic;
using HpAttenuator.Model;

namespace HpAttenuator.Instruments
{
    /// <summary>A CW signal source (e.g. HP 8340B).</summary>
    public interface ISignalSource
    {
        string ResourceName { get; }
        void Preset();
        void SetFrequencyMHz(double mhz);
        void SetPowerDbm(double dbm);
        void RfOn();
        void RfOff();
    }

    /// <summary>An external local oscillator (e.g. HP 8673B). Same shape as a source.</summary>
    public interface ILocalOscillator : ISignalSource
    {
        /// <summary>Highest LO frequency the generator can produce, in MHz.</summary>
        double MaxFrequencyMHz { get; }

        /// <summary>Lowest LO frequency the generator can produce, in MHz.</summary>
        double MinFrequencyMHz { get; }
    }

    /// <summary>The 11713A-driven step attenuator.</summary>
    public interface IStepAttenuator
    {
        string ResourceName { get; }
        AttenuatorConfig Config { get; }

        /// <summary>Sets total attenuation in dB; returns the data string sent.</summary>
        string SetAttenuationDb(int db);

        /// <summary>
        /// Engages exactly the given section digits (1-8) and bypasses all others.
        /// Config-independent relay control, used by attenuator identification.
        /// Returns the data string sent.
        /// </summary>
        string SetEngaged(IEnumerable<int> digits);
    }

    /// <summary>The two 11713A attenuator ports.</summary>
    public enum AttenuatorBank
    {
        /// <summary>ATTEN X — relay digits 1-4.</summary>
        X,

        /// <summary>ATTEN Y — relay digits 5-8.</summary>
        Y
    }

    /// <summary>A tuned measuring receiver (e.g. HP 8902A), optionally via a converter.</summary>
    public interface IMeasuringReceiver
    {
        string ResourceName { get; }

        /// <summary>Puts the receiver into Tuned RF Level mode and a known state.</summary>
        void PrepareTunedRfLevel();

        /// <summary>Configures a direct (no converter) measurement at the given RF frequency.</summary>
        void ConfigureDirect(double rfMHz);

        /// <summary>Configures a frequency-offset (converter) measurement: LO frequency + tuned RF.</summary>
        void ConfigureConverted(double rfMHz, double loMHz);

        /// <summary>Triggers a settled measurement and returns the level in dBm.</summary>
        double ReadLevelDbm();
    }
}
