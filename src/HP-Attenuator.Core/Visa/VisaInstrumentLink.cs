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
        /// <summary>When true, sound a short beep on every command sent to an instrument.</summary>
        public static bool BeepOnCommand = true;

        private readonly IMessageBasedSession _session;
        private readonly List<string> _history = new List<string>();

        public string ResourceName { get; }
        public bool IsSimulated => false;
        public IReadOnlyList<string> History => _history;

        private static void Beep()
        {
            if (!BeepOnCommand) return;
            try { Console.Beep(1000, 40); } catch { /* no console/audio device */ }
        }

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
            Beep();
        }

        public void Write(string command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            // Record the command even when the write fails (#36). This used to append to _history
            // only after a SUCCESSFUL write, so a command the bus rejected vanished from the history
            // entirely. The 11713A history is what the app shows the user as "what was sent", which
            // drives their mental model of the switch state — and a command that failed is exactly
            // the one they need to see, because it means the attenuator is NOT where they think.
            try
            {
                _session.RawIO.Write(command + "\n");
            }
            catch
            {
                _history.Add(command + "   <-- WRITE FAILED");
                throw;
            }

            _history.Add(command);
            Beep();
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

        public byte SerialPoll()
        {
            // GPIB serial poll: returns the device's status byte without a data transfer.
            return (byte)_session.ReadStatusByte();
        }

        /// <summary>
        /// Lists VISA INSTR resources visible to the resource manager.
        ///
        /// Throws if the resource manager itself cannot be reached. It used to swallow that and return
        /// an empty list, which made **"the bus is empty" and "VISA could not be reached" the same
        /// answer** (#36, the same conflation as rendering a failed serial poll as 0x00). A broken VISA
        /// install, a missing provider or a dead GPIB interface all reported as "no instruments found" —
        /// which sends the operator to check cabling when the fault is entirely on the PC side of the
        /// connector.
        ///
        /// An empty list now means what it says: VISA answered, and there is nothing on the bus.
        /// </summary>
        public static IEnumerable<string> FindResources()
        {
            return new List<string>(GlobalResourceManager.Find("?*INSTR"));
        }

        /// <summary>
        /// <see cref="FindResources"/> without the throw, for callers that want to tell the two cases
        /// apart: returns false and sets <paramref name="error"/> if the resource manager could not be
        /// reached, true with a (possibly empty) list if it answered.
        /// </summary>
        public static bool TryFindResources(out List<string> resources, out string error)
        {
            try
            {
                resources = new List<string>(GlobalResourceManager.Find("?*INSTR"));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                resources = new List<string>();
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        public void Dispose()
        {
            try { _session?.Dispose(); } catch { /* ignore */ }
        }
    }
}
