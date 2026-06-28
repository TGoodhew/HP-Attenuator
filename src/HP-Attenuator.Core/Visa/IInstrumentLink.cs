using System;
using System.Collections.Generic;

namespace HpAttenuator.Visa
{
    /// <summary>
    /// Abstraction over the transport to an instrument so callers can drive either
    /// a real VISA session or an in-memory simulator with the same code path.
    /// </summary>
    public interface IInstrumentLink : IDisposable
    {
        string ResourceName { get; }
        bool IsSimulated { get; }

        /// <summary>Sends a raw command string (terminator added by the link).</summary>
        void Write(string command);

        /// <summary>Reads a response string from a talker instrument.</summary>
        string Read();

        /// <summary>Writes a command then reads the response (for query instruments).</summary>
        string Query(string command);

        /// <summary>The commands sent so far, most recent last.</summary>
        IReadOnlyList<string> History { get; }
    }
}
