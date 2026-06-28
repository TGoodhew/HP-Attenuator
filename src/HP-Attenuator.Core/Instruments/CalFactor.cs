using System;
using System.Collections.Generic;

namespace HpAttenuator.Instruments
{
    /// <summary>A power-sensor / converter calibration factor at one frequency.</summary>
    public sealed class CalFactor
    {
        public double FreqMHz { get; }
        public double Cf { get; }   // percent, e.g. 96.3

        public CalFactor(double freqMHz, double cf)
        {
            FreqMHz = freqMHz;
            Cf = cf;
        }
    }

    /// <summary>
    /// The cal-factor table from the sensor/converter rear label (REF CF @ 50 MHz = 100%),
    /// supplied by the bench owner. Loaded into the 8902A's Frequency-Offset RF-Power table.
    /// </summary>
    public static class ConverterCalFactors
    {
        public const double ReferenceCf = 100.0; // REF CF (50 MHz)

        /// <summary>Sensor S/N 2407A00808, OPT 001 — 2 to 18 GHz.</summary>
        public static IReadOnlyList<CalFactor> Default { get; } = new[]
        {
            new CalFactor(2000, 96.3),  new CalFactor(3000, 94.8),  new CalFactor(4000, 93.9),
            new CalFactor(5000, 92.9),  new CalFactor(6000, 91.9),  new CalFactor(7000, 91.1),
            new CalFactor(8000, 90.3),  new CalFactor(9000, 89.3),  new CalFactor(10000, 88.5),
            new CalFactor(11000, 87.5), new CalFactor(12400, 87.0), new CalFactor(13000, 86.1),
            new CalFactor(14000, 85.6), new CalFactor(15000, 85.4), new CalFactor(16000, 84.9),
            new CalFactor(17000, 84.6), new CalFactor(18000, 84.1),
        };
    }

    /// <summary>An error reported by the 8902A via its sentinel data value.</summary>
    public sealed class Hp8902AException : Exception
    {
        public int Code { get; }

        public Hp8902AException(int code, string message) : base($"8902A Error {code}: {message}")
        {
            Code = code;
        }

        /// <summary>Known 8902A operating-error messages (Operation manual, p.3-286).</summary>
        public static string Describe(int code)
        {
            switch (code)
            {
                case 1: return "Input level too high";
                case 2: return "Input level too low";
                case 5: return "RF input overload";
                case 6: return "Voltmeter/display overload";
                case 15: return "Calibration factor error (load cal factors)";
                case 17: return "Tuned RF Level circuits underdriven";
                case 18: return "RF Power will not calibrate";
                case 96: return "No input signal sensed (cannot tune to a signal)";
                default: return "see 8902A manual error table";
            }
        }
    }
}
