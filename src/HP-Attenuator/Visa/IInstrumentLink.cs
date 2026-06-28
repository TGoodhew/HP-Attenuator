using System;
using System.Collections.Generic;

namespace HpAttenuator.Visa
{
    /// <summary>
    /// Abstraction over the transport to the 11713A so the UI can drive either a
    /// real VISA session or the in-memory simulator with the same code path.
    /// </summary>
    public interface IInstrumentLink : IDisposable
    {
        string ResourceName { get; }
        bool IsSimulated { get; }

        /// <summary>Sends a raw data string to the driver (terminator added by the link).</summary>
        void Write(string command);

        /// <summary>The data strings sent so far, most recent last.</summary>
        IReadOnlyList<string> History { get; }
    }
}
