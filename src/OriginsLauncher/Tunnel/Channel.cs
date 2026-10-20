using System;
using System.Collections.Generic;

namespace Origins.Tunnel
{
    /// <summary>
    ///     One session's reliable, ordered sequence of frames over UDP (see <see cref="Wire" />); the TCP connections of
    ///     the session are streams in it (<see cref="Bridge" />). What it does against loss and latency, from what games
    ///     and game VPNs do:
    ///     <list type="bullet">
    ///         <item>every frame leaves twice, <see cref="RedundancyMs" /> apart (earlier, from <see cref="PiggybackMs" />, when a
    ///         datagram leaves anyway): one lost datagram costs nothing. Game traffic is small, so the double rate is cheap.
    ///         Resends leave twice as well;</item>
    ///         <item>a frame still unacknowledged after a later-sent frame was acknowledged, or after the peer echoed the time
    ///         stamp of a datagram sent well after it (an ACK or keepalive is enough: the side that only sends now and then
    ///         still finds out in one round trip), is resent at once (RACK);</item>
    ///         <item>otherwise after the timeout, srtt + 4 rttvar (80-1000 ms), doubling while nothing is acknowledged; the
    ///         first datagram after <see cref="SilenceMs" /> without any (a Wi-Fi hiccup) resends what is overdue at once;</item>
    ///         <item>each side acknowledges right away (cumulative + 32 frames of SACK in every DATA datagram) and measures
    ///         the round trip from the echo of the peer's time stamp (minus the peer's hold), resends included.</item>
    ///     </list>
    ///     Windows: <see cref="WindowBytes" /> or <see cref="MaxFlight" /> frames in flight, <see cref="Reorder" /> frames
    ///     ahead kept by the receiver. Keepalive every second; <see cref="DeadMs" /> of silence ends the session.
    ///     <para>
    ///         Thread safety: every method locks the session's object given to the constructor (bridges use the same one).
    ///         The callbacks run inside that lock and must not block: they queue (writes) or start tasks (connects).
    ///     </para>
    /// </summary>
    public sealed class Channel
    {
        public const int RedundancyMs = 20, PiggybackMs = 8;
        public const int InitialRtoMs = 400, MinRtoMs = 80, MaxRtoMs = 1000, MaxBackoff = 8;
        public const int KeepaliveMs = 1000, DeadMs = 10000, SilenceMs = 300;
        public const int WindowBytes = 512 * 1024, MaxFlight = 1024, Reorder = 1024;

        /// <summary>Readers wait while more than this is queued or in flight (<see cref="QueuedBytes" />).</summary>
        public const int QueueLimit = 1024 * 1024;

        private static readonly byte[] s_empty = new byte[0];

        private sealed class Frame
        {
            public uint Seq;
            public byte Kind;
            public ushort Stream;
            public byte[] Bytes;
            public long LastSent;
            public int Sends;
            public bool Lost;
            public bool CopyDue; // the last (re)send has not had its second copy yet
        }

        private readonly object m_sync;
        private readonly byte[] m_header;
        private readonly Action<byte[], int> m_transmit;
        private readonly byte[] m_out = new byte[Wire.Mtu];
        private int m_outLength; // 0 = no datagram being built
        private bool m_outFrames;

        // sending: frames get their seq when queued; [m_low, m_sentHigh) is the window, [m_sentHigh, m_nextSeq) waits
        private uint m_nextSeq = 1, m_sentHigh = 1, m_low = 1;
        private readonly Queue<Frame> m_queue = new Queue<Frame>();
        private readonly Dictionary<uint, Frame> m_flight = new Dictionary<uint, Frame>();
        private int m_queueBytes, m_flightBytes;
        private long m_rackSent; // the newest send time among acknowledged frames
        private int m_backoff = 1;

        // receiving
        private uint m_recvNext = 1;
        private readonly Dictionary<uint, Frame> m_early = new Dictionary<uint, Frame>();
        private bool m_ackDue;
        private uint m_peerTs;
        private long m_peerTsAt;

        private double m_srtt = -1, m_rttvar;
        private int m_rto = InitialRtoMs;
        private long m_lastSend, m_lastRecv;
        private bool m_closed;

        /// <summary>The peer opened a stream to a port (gateway side).</summary>
        public Action<ushort, ushort> Opened;

        /// <summary>A stream's bytes, in order.</summary>
        public Action<ushort, byte[]> Received;

        /// <summary>The peer closed a stream: false = after its last bytes (FIN), true = at once (RST).</summary>
        public Action<ushort, bool> Closed;

        // statistics (read under the lock or as a rough value)
        public long FramesSent, Resends, Copies, FramesReceived, Duplicates, SavedByResend, DatagramsSent, DatagramsReceived, BytesSent, BytesReceived;

        /// <param name="sync">The session's lock.</param>
        /// <param name="header">The 12-byte DATA header of the session (type, session, token).</param>
        /// <param name="transmit">Sends one datagram (the buffer is reused after the call).</param>
        /// <param name="now"><see cref="Clock.NowMs" />.</param>
        public Channel(object sync, byte[] header, Action<byte[], int> transmit, long now)
        {
            m_sync = sync;
            m_header = header;
            m_transmit = transmit;
            m_lastSend = m_lastRecv = now;
        }

        /// <summary>Smoothed round trip in ms, -1 before the first sample.</summary>
        public int RttMs
        {
            get
            {
                lock (m_sync) return m_srtt < 0 ? -1 : (int) Math.Round(m_srtt);
            }
        }

        public int QueuedBytes
        {
            get
            {
                lock (m_sync) return m_queueBytes + m_flightBytes;
            }
        }

        public bool IsClosed
        {
            get
            {
                lock (m_sync) return m_closed;
            }
        }

        public void Open(ushort stream, ushort port, long now)
        {
            var bytes = new byte[2];
            Wire.PutU16(bytes, 0, port);
            Enqueue(Wire.Open, stream, bytes, now);
        }

        public void Send(ushort stream, byte[] data, int offset, int count, long now)
        {
            lock (m_sync)
            {
                if (m_closed) return;
                while (count > 0)
                {
                    int n = Math.Min(count, Wire.MaxPayload);
                    var bytes = new byte[n];
                    Buffer.BlockCopy(data, offset, bytes, 0, n);
                    Add(Wire.Payload, stream, bytes);
                    offset += n;
                    count -= n;
                }

                Pump(now);
            }
        }

        public void Finish(ushort stream, long now) => Enqueue(Wire.Fin, stream, s_empty, now);

        public void Reset(ushort stream, long now) => Enqueue(Wire.Rst, stream, s_empty, now);

        /// <summary>Retransmissions, copies, acknowledgements and keepalives that are due: every few ms.</summary>
        public void Tick(long now)
        {
            lock (m_sync)
            {
                if (!m_closed) Pump(now);
            }
        }

        /// <summary>Nothing came from the peer for <see cref="DeadMs" />, or the channel was shut down.</summary>
        public bool IsDead(long now)
        {
            lock (m_sync) return m_closed || now - m_lastRecv >= DeadMs;
        }

        /// <summary>Stops everything; later calls do nothing.</summary>
        public void Shutdown()
        {
            lock (m_sync)
            {
                m_closed = true;
                m_queue.Clear();
                m_flight.Clear();
                m_early.Clear();
                m_queueBytes = m_flightBytes = 0;
            }
        }

        /// <summary>
        ///     A DATA datagram of this session (the caller checked the header: type, session, token). False when it is
        ///     malformed (nothing of it was used).
        /// </summary>
        public bool OnDatagram(byte[] b, int length, long now)
        {
            lock (m_sync)
            {
                if (m_closed || length < Wire.DataHeaderSize) return false;
                for (int p = Wire.DataHeaderSize; p < length;)
                {
                    if (length - p < Wire.FrameHeaderSize) return false;
                    int len = Wire.U16(b, p + 7);
                    if (len > length - p - Wire.FrameHeaderSize) return false;
                    p += Wire.FrameHeaderSize + len;
                }

                int o = Wire.HeaderSize;
                uint tsval = Wire.U32(b, o), tsecr = Wire.U32(b, o + 4);
                int echoDelay = Wire.U16(b, o + 8);
                uint ack = Wire.U32(b, o + 10), sack = Wire.U32(b, o + 14);
                bool afterSilence = now - m_lastRecv >= SilenceMs;
                m_lastRecv = now;
                DatagramsReceived++;
                BytesReceived += length;
                if (tsval != 0)
                {
                    m_peerTs = tsval;
                    m_peerTsAt = now;
                }

                if (tsecr != 0)
                    Sample((int) (Stamp(now) - tsecr) - echoDelay);
                OnAck(ack, sack);
                if (tsecr != 0)
                    LostByEcho(tsecr);
                if (afterSilence)
                    Revive(now);

                for (o = Wire.DataHeaderSize; o < length;)
                {
                    uint seq = Wire.U32(b, o);
                    byte kind = b[o + 4];
                    ushort stream = Wire.U16(b, o + 5);
                    int len = Wire.U16(b, o + 7);
                    var bytes = len == 0 ? s_empty : new byte[len];
                    Buffer.BlockCopy(b, o + Wire.FrameHeaderSize, bytes, 0, len);
                    o += Wire.FrameHeaderSize + len;
                    Arrive(seq, kind, stream, bytes);
                    if (m_closed) return true; // a callback shut the session down
                }

                Pump(now);
                return true;
            }
        }

        public string Stats()
        {
            lock (m_sync)
            {
                return $"rtt {(m_srtt < 0 ? -1 : (int) Math.Round(m_srtt))} rto {m_rto} | out frames {FramesSent} resent {Resends} copies {Copies} " +
                       $"datagrams {DatagramsSent} bytes {BytesSent} | in frames {FramesReceived} duplicates {Duplicates} " +
                       $"first-copy-lost {SavedByResend} datagrams {DatagramsReceived} bytes {BytesReceived}";
            }
        }

        // ------------------------------------------------------------------ sending

        private void Enqueue(byte kind, ushort stream, byte[] bytes, long now)
        {
            lock (m_sync)
            {
                if (m_closed) return;
                Add(kind, stream, bytes);
                Pump(now);
            }
        }

        private void Add(byte kind, ushort stream, byte[] bytes)
        {
            m_queue.Enqueue(new Frame {Seq = m_nextSeq++, Kind = kind, Stream = stream, Bytes = bytes});
            m_queueBytes += bytes.Length;
        }

        private void Pump(long now)
        {
            while (m_queue.Count > 0 && m_flight.Count < MaxFlight && m_flightBytes < WindowBytes)
            {
                var f = m_queue.Dequeue();
                m_queueBytes -= f.Bytes.Length;
                m_flight[f.Seq] = f;
                m_flightBytes += f.Bytes.Length;
                m_sentHigh = f.Seq + 1;
            }

            int rto = Math.Min(m_rto * m_backoff, MaxRtoMs * MaxBackoff);
            bool timedOut = false;
            for (uint s = m_low; s < m_sentHigh; s++)
            {
                if (!m_flight.TryGetValue(s, out var f)) continue;
                if (f.Sends == 0)
                {
                    Put(f, now, false, false);
                }
                else if (f.Lost)
                {
                    Resends++;
                    Put(f, now, true, false);
                }
                else if (now - f.LastSent >= rto)
                {
                    Resends++;
                    timedOut = true;
                    Put(f, now, true, false);
                }
                else if (f.CopyDue && now - f.LastSent >= RedundancyMs)
                {
                    Copies++;
                    Put(f, now, true, true);
                }
            }

            // a datagram is leaving anyway: second copies that fit in it go a little early, for free
            if (m_outFrames)
            {
                for (uint s = m_low; s < m_sentHigh; s++)
                {
                    if (!m_flight.TryGetValue(s, out var f) || !f.CopyDue || now - f.LastSent < PiggybackMs) continue;
                    if (m_outLength + Wire.FrameHeaderSize + f.Bytes.Length > Wire.Mtu) break;
                    Copies++;
                    Put(f, now, true, true);
                }
            }

            if (timedOut)
                m_backoff = Math.Min(m_backoff * 2, MaxBackoff);
            if (m_outLength == 0 && (m_ackDue || now - m_lastSend >= KeepaliveMs))
                Begin();
            Flush(now);
        }

        /// <param name="resend">Not the first transmission (the receiver counts what a later one saved).</param>
        /// <param name="copy">The second copy of the last transmission.</param>
        private void Put(Frame f, long now, bool resend, bool copy)
        {
            int need = Wire.FrameHeaderSize + f.Bytes.Length;
            if (m_outLength != 0 && m_outLength + need > Wire.Mtu)
                Flush(now);
            if (m_outLength == 0)
                Begin();
            int o = m_outLength;
            Wire.PutU32(m_out, o, f.Seq);
            m_out[o + 4] = (byte) (f.Kind | (resend ? Wire.ResendFlag : 0));
            Wire.PutU16(m_out, o + 5, f.Stream);
            Wire.PutU16(m_out, o + 7, f.Bytes.Length);
            Buffer.BlockCopy(f.Bytes, 0, m_out, o + Wire.FrameHeaderSize, f.Bytes.Length);
            m_outLength += need;
            m_outFrames = true;
            if (!resend) FramesSent++;
            f.Sends++;
            f.LastSent = now;
            f.Lost = false;
            f.CopyDue = !copy;
        }

        private void Begin()
        {
            Buffer.BlockCopy(m_header, 0, m_out, 0, Wire.HeaderSize);
            m_outLength = Wire.DataHeaderSize; // the rest of the DATA header is written by Flush
        }

        private void Flush(long now)
        {
            if (m_outLength == 0) return;
            int o = Wire.HeaderSize;
            Wire.PutU32(m_out, o, Stamp(now));
            Wire.PutU32(m_out, o + 4, m_peerTs);
            Wire.PutU16(m_out, o + 8, m_peerTs == 0 ? 0 : (int) Math.Min(65535, now - m_peerTsAt));
            Wire.PutU32(m_out, o + 10, m_recvNext);
            Wire.PutU32(m_out, o + 14, Sack());
            int length = m_outLength;
            m_outLength = 0;
            m_outFrames = false;
            m_ackDue = false;
            m_lastSend = now;
            DatagramsSent++;
            BytesSent += length;
            m_transmit(m_out, length);
        }

        private uint Sack()
        {
            if (m_early.Count == 0) return 0;
            uint bits = 0;
            for (int i = 0; i < 32; i++)
                if (m_early.ContainsKey(m_recvNext + 1 + (uint) i))
                    bits |= 1u << i;
            return bits;
        }

        private void OnAck(uint ack, uint sack)
        {
            if (ack > m_sentHigh) return; // acknowledges what was never sent: ignored
            bool progress = false;
            while (m_low < ack)
            {
                if (m_flight.TryGetValue(m_low, out var f))
                {
                    Acked(f);
                    progress = true;
                }

                m_low++;
            }

            for (int i = 0; i < 32 && sack != 0; i++)
            {
                if ((sack & (1u << i)) == 0) continue;
                uint s = ack + 1 + (uint) i;
                if (s >= m_sentHigh) break;
                if (m_flight.TryGetValue(s, out var f))
                {
                    Acked(f);
                    progress = true;
                }
            }

            if (!progress) return;
            m_backoff = 1;
            long reorder = ReorderMs();
            foreach (var f in m_flight.Values)
                if (f.Sends > 0 && !f.Lost && f.LastSent + reorder < m_rackSent)
                    f.Lost = true;
        }

        /// <summary>
        ///     The peer had our datagram stamped <paramref name="tsecr" /> when it sent this one, and its acknowledgement
        ///     still lacks frames whose last copy left well before that datagram: they were lost.
        /// </summary>
        private void LostByEcho(uint tsecr)
        {
            long reorder = ReorderMs();
            foreach (var f in m_flight.Values)
                if (f.Sends > 0 && !f.Lost && (int) (tsecr - Stamp(f.LastSent)) > reorder)
                    f.Lost = true;
        }

        /// <summary>The peer is heard again after a silence: what is overdue leaves now, not after the backed-off timeout.</summary>
        private void Revive(long now)
        {
            m_backoff = 1;
            long overdue = now - (m_srtt < 0 ? InitialRtoMs : (long) m_srtt);
            foreach (var f in m_flight.Values)
                if (f.Sends > 0 && f.LastSent <= overdue)
                    f.Lost = true;
        }

        /// <summary>How much later than a newer datagram an older one may still arrive (reordering, not loss).</summary>
        private long ReorderMs() => m_srtt < 0 ? 10 : Math.Max(4, (long) (m_srtt / 8));

        private void Acked(Frame f)
        {
            m_flight.Remove(f.Seq);
            m_flightBytes -= f.Bytes.Length;
            if (f.LastSent > m_rackSent) m_rackSent = f.LastSent;
        }

        private void Sample(int ms)
        {
            if (ms < 0 || ms > 30000) return;
            if (m_srtt < 0)
            {
                m_srtt = ms;
                m_rttvar = ms / 2.0;
            }
            else
            {
                m_rttvar = 0.75 * m_rttvar + 0.25 * Math.Abs(m_srtt - ms);
                m_srtt = 0.875 * m_srtt + 0.125 * ms;
            }

            m_rto = Math.Max(MinRtoMs, Math.Min(MaxRtoMs, (int) (m_srtt + Math.Max(4 * m_rttvar, 10))));
        }

        private static uint Stamp(long now)
        {
            uint stamp = (uint) now;
            return stamp == 0 ? 1 : stamp;
        }

        // ------------------------------------------------------------------ receiving

        private void Arrive(uint seq, byte kind, ushort stream, byte[] bytes)
        {
            FramesReceived++;
            m_ackDue = true;
            if (seq < m_recvNext || m_early.ContainsKey(seq))
            {
                Duplicates++;
                return;
            }

            if (seq - m_recvNext >= Reorder) return; // too far ahead: dropped, it comes again
            if ((kind & Wire.ResendFlag) != 0) SavedByResend++; // the first copy to arrive was a resend
            var frame = new Frame {Seq = seq, Kind = (byte) (kind & ~Wire.ResendFlag), Stream = stream, Bytes = bytes};
            if (seq != m_recvNext)
            {
                m_early[seq] = frame;
                return;
            }

            Deliver(frame);
            m_recvNext++;
            while (!m_closed && m_early.TryGetValue(m_recvNext, out frame))
            {
                m_early.Remove(m_recvNext);
                Deliver(frame);
                m_recvNext++;
            }
        }

        private void Deliver(Frame f)
        {
            switch (f.Kind)
            {
                case Wire.Open:
                    Opened?.Invoke(f.Stream, f.Bytes.Length >= 2 ? Wire.U16(f.Bytes, 0) : (ushort) 0);
                    break;
                case Wire.Payload:
                    Received?.Invoke(f.Stream, f.Bytes);
                    break;
                case Wire.Fin:
                    Closed?.Invoke(f.Stream, false);
                    break;
                case Wire.Rst:
                    Closed?.Invoke(f.Stream, true);
                    break;
            }
        }
    }
}
