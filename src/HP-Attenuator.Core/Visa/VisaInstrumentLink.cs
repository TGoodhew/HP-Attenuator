using System;
using System.Collections.Generic;
using Ivi.Visa;

namespace HpAttenuator.Visa
{
    /// <summary>
    /// Live link to an instrument over VISA, using the vendor-neutral Ivi.Visa API.
    /// At runtime this dispatches to the installed VISA.NET provider (NI-VISA).
    /// Write-only listen devices (e.g. the 11713A) simply never call <see cref="Read"/>.
    /// </summary>
    public sealed class VisaInstrumentLink : IInstrumentLink
    {
        private readonly IMessageBasedSession _session;
        private readonly List<string> _history = new List<string>();

        public string ResourceName { get; }
        public bool IsSimulated => false;
        public IReadOnlyList<string> History => _history;

        public VisaInstrumentLink(string resourceName, int timeoutMs = 5000)
        {
            ResourceName = resourceName;
            _session = (IMessageBasedSession)GlobalResourceManager.Open(resourceName);

            // Drive into a known terminator/timeout posture. The timeout must exceed the
            // longest measurement (e.g. the 8902A's 10 s averaging), so it is caller-set.
            // A trailing newline on writes is harmless and matches classic controllers.
            _session.TimeoutMilliseconds = timeoutMs;
            _session.TerminationCharacterEnabled = true;
        }

        public void Clear()
        {
            // GPIB Selected Device Clear. Some listen-only devices ignore it; don't fail.
            try { _session.Clear(); } catch { /* device may not support clear */ }
        }

        public void Write(string command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            _session.RawIO.Write(command + "\n");
            _history.Add(command);
        }

        public string Read()
        {
            // Reads up to the termination character (LF) / EOI. Classic HP talkers
            // (e.g. the 8902A) terminate their ASCII output with CR/LF + EOI.
            return _session.RawIO.ReadString().Trim();
        }

        public string Query(string command)
        {
            Write(command);
            return Read();
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
