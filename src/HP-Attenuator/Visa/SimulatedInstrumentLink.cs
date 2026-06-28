using System.Collections.Generic;

namespace HpAttenuator.Visa
{
    /// <summary>
    /// In-memory stand-in for the 11713A. Records every data string so the UI is
    /// fully exercisable without GPIB hardware attached.
    /// </summary>
    public sealed class SimulatedInstrumentLink : IInstrumentLink
    {
        private readonly List<string> _history = new List<string>();

        public string ResourceName => "SIMULATED";
        public bool IsSimulated => true;
        public IReadOnlyList<string> History => _history;

        public void Write(string command) => _history.Add(command);

        public void Dispose() { /* nothing to release */ }
    }
}
