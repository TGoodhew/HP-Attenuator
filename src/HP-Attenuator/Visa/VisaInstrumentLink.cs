using System;
using System.Collections.Generic;
using Ivi.Visa;

namespace HpAttenuator.Visa
{
    /// <summary>
    /// Live link to a 11713A over VISA, using the vendor-neutral Ivi.Visa API.
    /// At runtime this dispatches to the installed VISA.NET provider (NI-VISA).
    /// The 11713A is a listen-only device, so this link only ever writes; it
    /// never reads.
    /// </summary>
    public sealed class VisaInstrumentLink : IInstrumentLink
    {
        private readonly IMessageBasedSession _session;
        private readonly List<string> _history = new List<string>();

        public string ResourceName { get; }
        public bool IsSimulated => false;
        public IReadOnlyList<string> History => _history;

        public VisaInstrumentLink(string resourceName)
        {
            ResourceName = resourceName;
            _session = (IMessageBasedSession)GlobalResourceManager.Open(resourceName);

            // Drive into a known terminator/timeout posture. The 11713A acts on the
            // bytes as they arrive; a trailing newline is harmless and matches the
            // CR/LF that classic controllers append.
            _session.TimeoutMilliseconds = 5000;
            _session.TerminationCharacterEnabled = true;
        }

        public void Write(string command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            _session.RawIO.Write(command + "\n");
            _history.Add(command);
        }

        /// <summary>Lists VISA INSTR resources visible to the resource manager.</summary>
        public static IEnumerable<string> FindResources()
        {
            try
            {
                return new List<string>(GlobalResourceManager.Find("?*INSTR"));
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        public void Dispose()
        {
            try { _session?.Dispose(); } catch { /* ignore */ }
        }
    }
}
