using System;
using System.Collections.Generic;

namespace HpAttenuator.Visa
{
    /// <summary>
    /// An <see cref="IInstrumentLink"/> whose serial-poll responses are scripted, so the REAL
    /// instrument driver (<c>Hp8902A</c>) can be exercised headlessly against a chosen status-byte
    /// sequence.
    ///
    /// This exists because the CALIBRATE completion poll (#34) is not reachable in simulation mode —
    /// sim mode substitutes <c>SimulatedReceiver</c>/<c>SimulatedBench</c>, whose <c>Calibrate()</c>
    /// is an empty method, so the code that actually talks to the 8902A is never executed. Scripting
    /// the link instead of the receiver keeps the driver under test.
    ///
    /// A scripted entry can also be a <see cref="PollFault"/>, which makes <see cref="SerialPoll"/>
    /// throw — the case the old code rendered as "0x00" and could not be distinguished from a genuine
    /// zero status byte.
    /// </summary>
    public sealed class ScriptedInstrumentLink : IInstrumentLink
    {
        /// <summary>A scripted serial poll that throws instead of returning a status byte.</summary>
        public const int PollFault = -1;

        private readonly Queue<int> _polls = new Queue<int>();
        private readonly Queue<string> _reads = new Queue<string>();
        private readonly List<string> _history = new List<string>();

        /// <summary>Status byte returned once the script runs out. Defaults to 0x00 (nothing set).</summary>
        public int TrailingPoll { get; set; }

        public string ResourceName => "SCRIPTED";
        public bool IsSimulated => true;
        public IReadOnlyList<string> History => _history;

        /// <summary>Number of serial polls the driver has performed.</summary>
        public int PollCount { get; private set; }

        public ScriptedInstrumentLink(params int[] polls)
        {
            if (polls != null)
                foreach (int p in polls) _polls.Enqueue(p);
        }

        /// <summary>Queues a status byte (or <see cref="PollFault"/>) repeated <paramref name="times"/>.</summary>
        public ScriptedInstrumentLink Poll(int statusByte, int times = 1)
        {
            for (int i = 0; i < times; i++) _polls.Enqueue(statusByte);
            return this;
        }

        /// <summary>Queues a response for the next <see cref="Read"/>.</summary>
        public ScriptedInstrumentLink Reads(string response)
        {
            _reads.Enqueue(response);
            return this;
        }

        public void Clear() { _history.Add("<CLEAR>"); }

        public void Write(string command) => _history.Add(command);

        public string Read() => _reads.Count > 0 ? _reads.Dequeue() : string.Empty;

        public string Query(string command)
        {
            Write(command);
            return Read();
        }

        public byte SerialPoll()
        {
            PollCount++;
            int sb = _polls.Count > 0 ? _polls.Dequeue() : TrailingPoll;
            if (sb == PollFault)
                throw new TimeoutException("scripted serial-poll fault");
            return (byte)sb;
        }

        public void Dispose() { /* nothing to release */ }
    }
}
