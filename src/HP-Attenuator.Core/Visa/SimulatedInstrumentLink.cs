using System.Collections.Generic;

namespace HpAttenuator.Visa
{
    /// <summary>
    /// In-memory stand-in for a write-only listen device (e.g. the 11713A).
    /// Records every data string so the UI is fully exercisable without GPIB
    /// hardware. <see cref="Read"/>/<see cref="Query"/> are not meaningful here;
    /// instrument-level simulation (the bench model) is used for talkers.
    /// </summary>
    public sealed class SimulatedInstrumentLink : IInstrumentLink
    {
        private readonly List<string> _history = new List<string>();

        public string ResourceName => "SIMULATED";
        public bool IsSimulated => true;
        public IReadOnlyList<string> History => _history;

        public void Clear() { /* nothing to clear */ }

        public void Write(string command) => _history.Add(command);

        public string Read() => string.Empty;

        public string Query(string command)
        {
            Write(command);
            return string.Empty;
        }

        public byte SerialPoll() => 0; // no condition bits in the simulated link

        public void Dispose() { /* nothing to release */ }
    }
}
