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

        /// <summary>
        /// How long each queued 0x00 entry stands for when <see cref="WaitForServiceRequest"/> skips
        /// over it, ms: the script's quiet stretches are expressed as runs of 0x00, as they were for
        /// the polling implementation, so one script describes the same timeline to both.
        /// </summary>
        public int QuietStepMs { get; set; } = 5;

        /// <summary>Service-request waits performed.</summary>
        public int SrqWaits { get; private set; }

        public void DiscardServiceRequests() { _history.Add("<DISCARD SRQ>"); }

        /// <summary>
        /// SRQ arrives when the next scripted status byte is non-zero (or a fault). Leading 0x00 entries
        /// are quiet time — consumed at <see cref="QuietStepMs"/> each, without counting as polls —
        /// and an exhausted script with a 0x00 trailing poll never requests service.
        /// </summary>
        public bool WaitForServiceRequest(int timeoutMs)
        {
            SrqWaits++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int head = _polls.Count > 0 ? _polls.Peek() : TrailingPoll;
                if (head != 0) return true;
                if (_polls.Count == 0)
                {
                    System.Threading.Thread.Sleep((int)Math.Max(0, timeoutMs - sw.ElapsedMilliseconds));
                    return false;
                }
                _polls.Dequeue();
                System.Threading.Thread.Sleep(QuietStepMs);
            }
            return false;
        }

        public byte SerialPoll() => Poll(HangMs);

        public byte SerialPoll(int timeoutMs) => Poll(timeoutMs);

        /// <summary>
        /// Models the 8902A's documented limit (O&amp;C 3-32): it "cannot alter the Status Byte while in
        /// serial poll mode". When &gt; 0, a poll arriving within this many ms of the previous one
        /// returns 0x00 and leaves a pending non-zero byte unrevealed — so a loop that polls
        /// continuously never sees the bit, exactly as on the bench (V17, 2026-10-15). 0 = off.
        /// </summary>
        public int PollSuppressionMs { get; set; }

        private readonly System.Diagnostics.Stopwatch _sincePoll = new System.Diagnostics.Stopwatch();

        private byte Poll(int blockMs)
        {
            PollCount++;
            bool suppressed = PollSuppressionMs > 0 && _sincePoll.IsRunning
                              && _sincePoll.ElapsedMilliseconds < PollSuppressionMs;
            _sincePoll.Restart();
            int head = _polls.Count > 0 ? _polls.Peek() : TrailingPoll;
            if (suppressed && head > 0)
            {
                return 0x00;                       // the instrument could not update the byte
            }
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
