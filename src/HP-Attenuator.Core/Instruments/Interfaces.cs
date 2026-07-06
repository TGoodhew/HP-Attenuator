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

    /// <summary>
    /// Which 8902A IF detector a Tuned RF Level measurement uses. The choice sets both the noise
    /// bandwidth and the usable depth (8902A O&amp;C, Tuned RF Level ranges):
    /// <list type="bullet">
    /// <item><b>Average</b> (SF 4.4, 30 kHz BW) — floor ≈ −100 dBm; tolerant of a noisy / drifting
    /// source (residual FM), so it holds through the 8673B-LO + 11793A converter path. RF ranges
    /// calibrate at 0 / −15 / −50 dBm. This is the default for the ≤~95 dB sweeps.</item>
    /// <item><b>Synchronous</b> (SF 4.0, 200 Hz BW) — floor ≈ −127 dBm, the depth needed to reach a
    /// full 110 dB (≈ −112 dBm at a −2 dBm reference). RF ranges calibrate at 0 / −40 / −80 dBm. Its
    /// narrow band needs a spectrally clean signal, so it can lose lock (Error 96) through the
    /// converter path — used for the deep sweep (#14).</item>
    /// </list>
    /// </summary>
    public enum TrflDetector
    {
        /// <summary>IF Average detector (SF 4.4, 30 kHz BW, floor ≈ −100 dBm).</summary>
        Average,

        /// <summary>IF Synchronous detector (SF 4.0, 200 Hz BW, floor ≈ −127 dBm).</summary>
        Synchronous
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
        /// direct or converter (frequency-offset, with LO) regime, using the given IF
        /// <paramref name="detector"/> (Average by default; Synchronous for the deep sweep, #14).
        /// When <paramref name="trackMode"/> is set, the receiver uses Track Mode (8902A SF 32.9,
        /// the Microwave Product Note's low-level converter method) to hold lock on the drifting
        /// converted signal; Track Mode implies the Average detector and supersedes
        /// <paramref name="detector"/>.
        /// </summary>
        void BeginAttenuationMeasurement(double rfMHz, MeasurementRegime regime, double loMHz,
            TrflDetector detector = TrflDetector.Average, bool trackMode = false);

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

        /// <summary>
        /// Prepares for the range-calibration pass: enables the RECAL/UNCAL status
        /// condition (so <see cref="RecalRequested"/> can poll it) and puts the receiver
        /// in free-run so that state tracks the live level as the attenuator steps down.
        /// </summary>
        void BeginRangeCalibration();

        /// <summary>
        /// Enables just the RECAL/UNCAL status condition (so <see cref="RecalRequested"/> can poll
        /// it) WITHOUT forcing free-run. Used on the Track-Mode path, where free-run would auto-range
        /// the receiver and shift the relative reference by a whole RF range.
        /// </summary>
        void EnableRecalStatus();

        /// <summary>
        /// True if the receiver is asking for a range calibration (8902A RECAL/UNCAL).
        /// Read by a serial poll; only CALIBRATE when this is set, per the 8902A
        /// procedure — calibrating a range that doesn't need it raises Error 35.
        /// </summary>
        bool RecalRequested();

        /// <summary>Raw serial-poll status byte, for diagnostics. Returns -1 if not applicable.</summary>
        int PollStatusByte();

        /// <summary>Performs one range-calibration step (CALIBRATE) at the current level.</summary>
        void Calibrate();

        /// <summary>Clears a displayed error/condition on the instrument (8902A CL key).</summary>
        void ClearError();

        /// <summary>
        /// Forces a retune of the Tuned-RF-Level VCO to recapture the signal after the receiver
        /// has lost lock (8902A Error 96). This is the manual's remedy (O&amp;C 3-116, "Blue Key,
        /// CLEAR" = HP-IB code <c>BC</c>): it retunes the VCO and recaptures the signal provided it
        /// has not drifted more than 5 MHz — unlike <see cref="ClearError"/> (CL), which only clears
        /// the displayed error without re-acquiring lock.
        /// </summary>
        void RetuneToSignal();

        /// <summary>
        /// Releases the GPIB bus after a hung / timed-out measurement by issuing a device clear
        /// (SDC). The 8902A inhibits the bus handshake until a triggered measurement cycle completes
        /// (O&amp;C 3-22); if a read times out mid-cycle the instrument keeps holding the bus, so the
        /// next write to ANY instrument times out too. A device clear aborts the cycle and frees the
        /// bus — it also resets the receiver to its preset state, so the caller must re-establish the
        /// measurement before using it again.
        /// </summary>
        void ReleaseBus();

        /// <summary>Sets the 0 dB reference (SET REF) at the current level.</summary>
        void SetReference();

        /// <summary>
        /// Triggers a settled measurement and returns the level in dB relative to the
        /// reference (≤ 0). Throws <see cref="Hp8902AException"/> on an instrument error.
        /// </summary>
        double ReadRelativeDb();

        /// <summary>
        /// Triggers a settled Tuned RF Level measurement and returns the <b>absolute</b> level in
        /// dBm — the level BEFORE <see cref="SetReference"/> is taken (S4/LG shows absolute dBm until
        /// SET REF re-zeroes it to relative dB). Used by adaptive reference leveling (issue #16) to
        /// read where the 0 dB reference currently sits so the source can be nudged to keep it just
        /// under the 8902A's 0 dBm relative-measurement ceiling. Throws <see cref="Hp8902AException"/>
        /// on an instrument error (e.g. 96, no signal).
        /// </summary>
        double ReadTunedLevelDbm();

        /// <summary>
        /// Measures the input signal frequency (MHz) — used as a signal-presence check.
        /// Throws <see cref="Hp8902AException"/> (code 96) when no signal is sensed.
        /// </summary>
        double ReadSignalFrequencyMHz();
    }
}
