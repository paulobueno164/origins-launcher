using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Origins.Tunnel
{
    /// <summary>
    ///     One TCP connection carried by a stream of a <see cref="Channel" />: what the socket reads goes out as frames,
    ///     what the peer sends is written to the socket. The launcher attaches the game's connection at once; the gateway
    ///     after its connect to the server (bytes from the peer wait here meanwhile). The local side closing sends FIN
    ///     (the peer closes after writing what it has); an error sends RST. The game protocol never half-closes, so a
    ///     FIN closes the whole connection.
    ///     <para>
    ///         Bytes from the peer are written on the thread that received them when the socket is idle (no hop to the
    ///         thread pool: on a busy machine that hop alone cost 1-7 ms per packet); only what queues up behind a write
    ///         still in progress is written later, by that write's continuation.
    ///     </para>
    /// </summary>
    public sealed class Bridge
    {
        public const int ReadSize = 16 * 1024;

        /// <summary>Bytes from the peer waiting for the local socket; past this the local side stopped reading: RST.</summary>
        public const int WriteLimit = 4 * 1024 * 1024;

        private readonly Channel m_channel;
        private readonly object m_sync;
        private readonly Queue<byte[]> m_writes = new Queue<byte[]>();
        private Socket m_socket;
        private NetworkStream m_stream;
        private int m_queued;
        private bool m_writing, m_finishing, m_ended;

        public readonly ushort Id;

        /// <summary>Called once when the bridge ends (inside the session's lock): the owner forgets it.</summary>
        public Action<Bridge> Ended;

        public long BytesFromSocket, BytesToSocket;

        /// <summary>Tests: a line per read and write with its Stopwatch timestamp (the same clock in every process).</summary>
        public static Action<string> Trace;

        public Bridge(Channel channel, object sync, ushort id)
        {
            m_channel = channel;
            m_sync = sync;
            Id = id;
        }

        public bool IsEnded
        {
            get
            {
                lock (m_sync) return m_ended;
            }
        }

        /// <summary>Starts moving bytes both ways.</summary>
        public void Attach(Socket socket)
        {
            try
            {
                socket.NoDelay = true;
            }
            catch (Exception)
            {
                // closed meanwhile: the read below finds out
            }

            lock (m_sync)
            {
                if (m_ended)
                {
                    CloseQuietly(socket, true);
                    return;
                }

                m_socket = socket;
                m_stream = new NetworkStream(socket, false);
                if (m_writes.Count > 0 || m_finishing) StartWriting(); // what the peer sent while the gateway connected
            }

            _ = ReadLoopAsync();
        }

        /// <summary>Bytes from the peer (the channel's callback, inside the lock).</summary>
        public void Deliver(byte[] data)
        {
            if (m_ended || m_finishing) return;
            if (m_queued + data.Length > WriteLimit)
            {
                Close(true);
                return;
            }

            m_writes.Enqueue(data);
            m_queued += data.Length;
            Trace?.Invoke($"{System.Diagnostics.Stopwatch.GetTimestamp()} deliver {Id} {data.Length}");
            StartWriting();
        }

        /// <summary>The peer closed the stream (the channel's callback, inside the lock).</summary>
        public void PeerClosed(bool reset)
        {
            if (m_ended) return;
            if (reset)
            {
                End(true);
                return;
            }

            m_finishing = true;
            StartWriting();
        }

        /// <summary>Ends the bridge now; with <paramref name="reset" /> the peer is told to drop its side (RST).</summary>
        public void Close(bool reset)
        {
            lock (m_sync)
            {
                if (m_ended) return;
                if (reset) m_channel.Reset(Id, Clock.NowMs);
                End(reset);
            }
        }

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[ReadSize];
            try
            {
                while (true)
                {
                    int n = await m_stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (n == 0) break;
                    BytesFromSocket += n;
                    Trace?.Invoke($"{System.Diagnostics.Stopwatch.GetTimestamp()} read {Id} {n}");
                    lock (m_sync)
                    {
                        if (m_ended) return; // nothing more for a stream that was closed or reset
                        m_channel.Send(Id, buffer, 0, n, Clock.NowMs);
                    }

                    while (m_channel.QueuedBytes > Channel.QueueLimit && !IsEnded && !m_channel.IsClosed)
                        await Task.Delay(5).ConfigureAwait(false);
                    if (IsEnded) return;
                }

                lock (m_sync)
                {
                    if (m_ended) return;
                    m_channel.Finish(Id, Clock.NowMs); // the local side closed: so does the peer, after its last bytes
                    End(false);
                }
            }
            catch (Exception)
            {
                Close(true);
            }
        }

        /// <summary>Inside the lock: writes what is queued, starting on this thread, unless a write is in progress.</summary>
        private void StartWriting()
        {
            if (m_writing || m_stream == null) return;
            m_writing = true;
            _ = WriteLoopAsync();
        }

        /// <summary>
        ///     Runs synchronously up to the first write that does not complete at once (a small write to a socket with
        ///     room completes at once); its continuation goes on with what queued up meanwhile.
        /// </summary>
        private async Task WriteLoopAsync()
        {
            try
            {
                while (true)
                {
                    byte[] data;
                    lock (m_sync)
                    {
                        if (m_ended) return;
                        if (m_writes.Count == 0)
                        {
                            m_writing = false;
                            if (m_finishing) End(false); // the peer finished and everything it sent was written
                            return;
                        }

                        data = Take();
                    }

                    await m_stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                    BytesToSocket += data.Length;
                    Trace?.Invoke($"{System.Diagnostics.Stopwatch.GetTimestamp()} wrote {Id} {data.Length}");
                }
            }
            catch (Exception)
            {
                Close(true);
            }
        }

        /// <summary>Everything queued (up to 64 KB) as one write.</summary>
        private byte[] Take()
        {
            var first = m_writes.Dequeue();
            int total = first.Length;
            if (m_writes.Count == 0 || total >= 65536)
            {
                m_queued -= total;
                return first;
            }

            foreach (var w in m_writes)
            {
                if (total + w.Length > 65536) break;
                total += w.Length;
            }

            var all = new byte[total];
            Buffer.BlockCopy(first, 0, all, 0, first.Length);
            int at = first.Length;
            while (m_writes.Count > 0 && at + m_writes.Peek().Length <= total)
            {
                var w = m_writes.Dequeue();
                Buffer.BlockCopy(w, 0, all, at, w.Length);
                at += w.Length;
            }

            m_queued -= total;
            return all;
        }

        /// <summary>Inside the lock. <paramref name="abortive" />: the local side gets a reset, not an orderly end.</summary>
        private void End(bool abortive)
        {
            if (m_ended) return;
            m_ended = true;
            m_writes.Clear();
            m_queued = 0;
            if (m_socket != null) CloseQuietly(m_socket, abortive);
            Ended?.Invoke(this);
        }

        private static void CloseQuietly(Socket socket, bool abortive)
        {
            try
            {
                // .NET turns a close with a read still pending (the read loop's) into a reset, unless the sending side
                // was shut down first: the server must see the end of the stream, not an error
                if (!abortive) socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception)
            {
                // already gone
            }

            try
            {
                socket.Close();
            }
            catch (Exception)
            {
                // already gone
            }
        }
    }
}
