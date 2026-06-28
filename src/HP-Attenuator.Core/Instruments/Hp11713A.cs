using System;
using System.Collections.Generic;
using System.Linq;
using HpAttenuator.Model;
using HpAttenuator.Visa;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// Driver for the HP 11713A Attenuator/Switch Driver. Wraps the dB-to-data-string
    /// solver (<see cref="CommandBuilder"/>) and tracks a software shadow of the
    /// driver state. Used by both the interactive app and the test harness so they
    /// exercise the same control path.
    /// </summary>
    public sealed class Hp11713A : IStepAttenuator
    {
        private readonly IInstrumentLink _link;

        public Hp11713A(IInstrumentLink link, AttenuatorConfig config)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public AttenuatorConfig Config { get; }
        public DeviceState State { get; } = new DeviceState();

        public string ResourceName => _link.ResourceName;
        public bool IsSimulated => _link.IsSimulated;
        public IReadOnlyList<string> History => _link.History;

        /// <summary>
        /// Sets total attenuation across both banks. Returns the data string sent.
        /// Throws <see cref="ArgumentOutOfRangeException"/> if the value is unreachable.
        /// </summary>
        public string SetAttenuationDb(int db)
        {
            var engaged = CommandBuilder.Solve(Config.AllSections.ToList(), db);
            if (engaged == null)
                throw new ArgumentOutOfRangeException(nameof(db),
                    $"{db} dB is not achievable (range 0-{Config.MaxDecibels} dB).");

            string command = CommandBuilder.BuildString(Config.AllSections, new HashSet<int>(engaged));
            _link.Write(command);

            State.Engaged.Clear();
            foreach (var d in engaged) State.Engaged.Add(d);
            return command;
        }

        /// <summary>
        /// Engages exactly the given section digits and bypasses all others (across
        /// both banks). Config-independent relay control used for identification.
        /// </summary>
        public string SetEngaged(IEnumerable<int> digits)
        {
            var set = new HashSet<int>(digits);
            string command = CommandBuilder.BuildString(Config.AllSections, set);
            _link.Write(command);

            State.Engaged.Clear();
            foreach (var d in set) State.Engaged.Add(d);
            return command;
        }

        /// <summary>Sets a single attenuator bank, leaving the other bank unchanged.</summary>
        public string SetBankDb(IReadOnlyList<Section> bank, int db)
        {
            var engaged = CommandBuilder.Solve(bank, db);
            if (engaged == null)
                throw new ArgumentOutOfRangeException(nameof(db),
                    $"{db} dB is not achievable on this bank.");

            string command = CommandBuilder.BuildString(bank, new HashSet<int>(engaged));
            _link.Write(command);

            foreach (var s in bank) State.Engaged.Remove(s.Digit);
            foreach (var d in engaged) State.Engaged.Add(d);
            return command;
        }

        /// <summary>Drives independent switch S9 (true = A9, false = B9).</summary>
        public string SetSwitch9(bool on)
        {
            string command = CommandBuilder.Switch9(on);
            _link.Write(command);
            State.Switch9 = on;
            return command;
        }

        /// <summary>Drives independent switch S0 (true = A0, false = B0).</summary>
        public string SetSwitch0(bool on)
        {
            string command = CommandBuilder.Switch0(on);
            _link.Write(command);
            State.Switch0 = on;
            return command;
        }

        /// <summary>Sends a raw data string verbatim (not reflected in tracked state).</summary>
        public void SendRaw(string command) => _link.Write(command);
    }
}
