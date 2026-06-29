using System.Collections.Generic;
using HpAttenuator.Model;

namespace HpAttenuator.Instruments
{
    /// <summary>A CW signal source (e.g. HP 8340B).</summary>
    public interface ISignalSource
    {
        string ResourceName { get; }

        /// <summary>Device clear + preset to a known state (no stale errors/SRQ).</summary>
        void Initialize();

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

        /// <summary>Device clear + set to a known (0 dB) state.</summary>
        void Initialize();

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

    /// <summary>
    /// A tuned measuring receiver (e.g. HP 8902A) measuring attenuation as a relative
    /// (dB) Tuned RF Level, optionally via a converter. The workflow per frequency is:
    /// Begin -&gt; (Calibrate range while stepping down) -&gt; SetReference at 0 dB -&gt; read
    /// each step as dB relative. See the 8902A attenuation procedure.
    /// </summary>
    public interface IMeasuringReceiver
    {
        string ResourceName { get; }

        /// <summary>Device clear + preset to a known state (no stale errors/SRQ).</summary>
        void Initialize();

        /// <summary>Instrument preset to a known state.</summary>
        void Reset();

        /// <summary>Selects the RF Power (power-sensor) measurement.</summary>
        void SelectRfPower();

        /// <summary>
        /// Loads BOTH RF-Power cal-factor tables (Normal + Frequency-Offset) in a single
        /// pass. Must be called exactly once — it clears all cal-factor storage first.
        /// </summary>
        void LoadCalFactors(double referenceCf, IReadOnlyList<CalFactor> table);

        /// <summary>Zeroes the power sensor (calibrator off). Returns the zeroed reading in watts.</summary>
        double ZeroSensor();

        /// <summary>
        /// Calibrates the sensor against the 50 MHz / 1 mW reference (C1 → settle → SC → C0).
        /// Returns the reference power read back in watts (≈ 1e-3 W = 0 dBm).
        /// </summary>
        double CalibrateSensor();

        /// <summary>
        /// Begins a Tuned RF Level relative measurement at the given RF frequency, in the
        /// direct or converter (frequency-offset, with LO) regime.
        /// </summary>
        void BeginAttenuationMeasurement(double rfMHz, MeasurementRegime regime, double loMHz);

        /// <summary>
        /// Begins an absolute RF Power measurement at the given RF frequency, in the
        /// direct or converter (frequency-offset, with LO) regime. The RF-Power cal-factor
        /// table must already be loaded for accuracy on the converter path.
        /// </summary>
        void BeginRfPowerMeasurement(double rfMHz, MeasurementRegime regime, double loMHz);

        /// <summary>
        /// Triggers a settled RF Power measurement and returns the absolute power in dBm.
        /// Throws <see cref="Hp8902AException"/> on an instrument error.
        /// </summary>
        double ReadRfPowerDbm();

        /// <summary>Performs one range-calibration step (CALIBRATE) at the current level.</summary>
        void Calibrate();

        /// <summary>Clears a displayed error/condition on the instrument (8902A CL key).</summary>
        void ClearError();

        /// <summary>Sets the 0 dB reference (SET REF) at the current level.</summary>
        void SetReference();

        /// <summary>
        /// Triggers a settled measurement and returns the level in dB relative to the
        /// reference (≤ 0). Throws <see cref="Hp8902AException"/> on an instrument error.
        /// </summary>
        double ReadRelativeDb();

        /// <summary>
        /// Measures the input signal frequency (MHz) — used as a signal-presence check.
        /// Throws <see cref="Hp8902AException"/> (code 96) when no signal is sensed.
        /// </summary>
        double ReadSignalFrequencyMHz();
    }
}
