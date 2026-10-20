using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Origins.Tunnel
{
    /// <summary>
    ///     The launcher's UDP tunnel to the server (owner 2026-10-20, ping item 5: an ExitLag-style route for players far
    ///     from the server). The game's TCP connections go to the launcher on 127.0.0.1, which carries their bytes over
    ///     UDP to the gateway next to the servers (Comet.Tunnel), which opens the real TCP connection there. A lost TCP
    ///     segment on a long route costs a retransmission timeout (often 200-400 ms, more with backoff) during which
    ///     every later byte waits; the tunnel sends each frame twice 20 ms apart and resends on the first sign of loss,
    ///     so one lost datagram costs nothing or a few milliseconds.
    ///     <para>
    ///         Shared code: the launcher (net48) and the gateway (net10) compile the same files, byte[] only, little-endian.
    ///         The canonical copy is the launcher repo's src\OriginsLauncher\Tunnel; server-6609's src\Comet.Tunnel\Shared
    ///         is a copy (C:\conquer\scripts\tunnel-shared-check.ps1 compares them).
    ///     </para>
    ///     Every datagram starts with 12 bytes: [0] 'O' 0x4F, [1] type, [2] version, [3] flags (0), [4] u32 session,
    ///     [8] u32 token (both 0 before the session exists). After it:
    ///     <list type="bullet">
    ///         <item>HELLO (client): nonce u64, zero padding up to <see cref="HandshakeSize" /> (the answer is smaller: no amplification).</item>
    ///         <item>CHALLENGE (gateway): nonce u64, time u32, cookie[16] (an HMAC of the client's address: no state before CONNECT).</item>
    ///         <item>CONNECT (client): nonce u64, time u32, cookie[16], the launcher's local game port u16, padding up to 64.</item>
    ///         <item>ACCEPT (gateway): nonce u64; the header carries the new session and its token.</item>
    ///         <item>REJECT (gateway): nonce u64, reason u8.</item>
    ///         <item>DATA (both): tsval u32, tsecr u32, echo delay u16, ack u32, sack u32, then frames (<see cref="Channel" />).</item>
    ///         <item>CLOSE (both): nothing; the session ends.</item>
    ///     </list>
    ///     A frame: seq u32, kind u8 (OPEN, DATA, FIN, RST; +0x80 when it is a resend), stream u16, length u16, bytes.
    ///     OPEN's bytes are the target port u16 (the server port the game asked for).
    /// </summary>
    public static class Wire
    {
        public const byte Magic = 0x4F, Version = 1;

        public const byte Hello = 1, Challenge = 2, Connect = 3, Accept = 4, Reject = 5, Data = 6, Close = 7;

        public const byte Open = 1, Payload = 2, Fin = 3, Rst = 4, ResendFlag = 0x80;

        public const byte RejectFull = 1, RejectVersion = 2, RejectLimit = 3, RejectCookie = 4;

        public const int HeaderSize = 12, DataHeaderSize = HeaderSize + 18, FrameHeaderSize = 9;

        /// <summary>No datagram is larger: fits any path MTU (IPv4 576 is the floor, real paths are 1400+).</summary>
        public const int Mtu = 1200;

        public const int MaxPayload = Mtu - DataHeaderSize - FrameHeaderSize;

        public const int HandshakeSize = 64, CookieSize = 16;

        public static void PutU16(byte[] b, int o, int v)
        {
            b[o] = (byte) v;
            b[o + 1] = (byte) (v >> 8);
        }

        public static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte) v;
            b[o + 1] = (byte) (v >> 8);
            b[o + 2] = (byte) (v >> 16);
            b[o + 3] = (byte) (v >> 24);
        }

        public static void PutU64(byte[] b, int o, ulong v)
        {
            PutU32(b, o, (uint) v);
            PutU32(b, o + 4, (uint) (v >> 32));
        }

        public static ushort U16(byte[] b, int o) => (ushort) (b[o] | b[o + 1] << 8);

        public static uint U32(byte[] b, int o) => (uint) (b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

        public static ulong U64(byte[] b, int o) => U32(b, o) | (ulong) U32(b, o + 4) << 32;

        public static void PutHeader(byte[] b, byte type, uint session, uint token)
        {
            b[0] = Magic;
            b[1] = type;
            b[2] = Version;
            b[3] = 0;
            PutU32(b, 4, session);
            PutU32(b, 8, token);
        }

        /// <summary>The datagram's type, or 0 when it is not one of ours.</summary>
        public static byte TypeOf(byte[] b, int length) =>
            length >= HeaderSize && b[0] == Magic && b[2] == Version ? b[1] : (byte) 0;
    }

    /// <summary>Milliseconds from a high resolution clock (TickCount moves in 15.6 ms steps on Windows).</summary>
    public static class Clock
    {
        private static readonly double s_msPerTick = 1000.0 / Stopwatch.Frequency;

        public static long NowMs => (long) (Stopwatch.GetTimestamp() * s_msPerTick);

        /// <summary>
        ///     Asks Windows for 1 ms timers while the tunnel runs: its 5 ms timer and Thread.Sleep would otherwise wake
        ///     every 15.6 ms. False where there is no winmm (not Windows).
        /// </summary>
        public static bool HighResolution()
        {
            try
            {
                return timeBeginPeriod(1) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);
    }
}
