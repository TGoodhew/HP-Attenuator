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
    /// zero status byte. A <see cref="PollHang"/> entry models a poll the instrument never answers:
    /// it blocks for the caller's timeout (or <see cref="HangMs"/> for the unbounded poll) and then
    /// throws — the shape of the 5 GHz hang, where one poll overran the whole read budget (#36).
    /// </summary>
    public sealed class ScriptedInstrumentLink : IInstrumentLink
    {
        /// <summary>A scripted serial poll that throws instead of returning a status byte.</summary>
        public const int PollFault = -1;

        /// <summary>A scripted serial poll that is never answered: blocks, then throws.</summary>
        public const int PollHang = -2;

        /// <summary>How long a <see cref="PollHang"/> blocks when the caller gives no timeout — stands
        /// in for the session timeout the parameterless poll inherits. Defaults to 2000 ms.</summary>
        public int HangMs { get; set; } = 2000;

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

        /// <summary>When set, <see cref="Write"/> throws — models a bus that will not accept the
        /// command, so the trace's behaviour on a failed write is testable (#36).</summary>
        public bool FailWrites { get; set; }

        public void Write(string command)
        {
            if (FailWrites) throw new TimeoutException("scripted write fault");
            _history.Add(command);
        }

        public string Read() => _reads.Count > 0 ? _reads.Dequeue() : string.Empty;

        public string Query(string command)
        {
            Write(command);
            return Read();
        }

        public byte SerialPoll() => Poll(HangMs);

        public byte SerialPoll(int timeoutMs) => Poll(timeoutMs);

        private byte Poll(int blockMs)
        {
            PollCount++;
            int sb = _polls.Count > 0 ? _polls.Dequeue() : TrailingPoll;
            if (sb == PollFault)
                throw new TimeoutException("scripted serial-poll fault");
            if (sb == PollHang)
            {
                System.Threading.Thread.Sleep(Math.Max(0, blockMs));
                throw new TimeoutException("scripted serial poll never answered");
            }
            return (byte)sb;
        }

        public void Dispose() { /* nothing to release */ }
    }
}
