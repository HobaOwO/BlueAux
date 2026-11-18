using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace BlueAux
{
    // WASAPI in shared mode, in CoPlay's wire format (48 kHz, 16-bit stereo). Windows converts to and from
    // whatever the device really uses (AUTOCONVERTPCM), so no resampling code here.
    static class Wasapi
    {
        const int EventCallback = 0x00040000, AutoConvertPcm = unchecked((int)0x80000000), SrcDefaultQuality = 0x08000000;
        static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IidRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid IidCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WaveFormat
        {
            public ushort Tag, Channels;
            public uint Rate, BytesPerSecond;
            public ushort BlockAlign, Bits, Extra;
        }

        // A shared-mode client on `device` that signals `wake` whenever it has room or data.
        public static IAudioClient Open(SoundCheck.IMMDevice device, AutoResetEvent wake, long bufferTicks)
        {
            Guid iid = IidAudioClient;
            object o;
            device.Activate(ref iid, 23, IntPtr.Zero, out o);
            var client = (IAudioClient)o;
            var format = new WaveFormat
            {
                Tag = 1,
                Channels = 2,
                Rate = CoPlayWire.Rate,
                BytesPerSecond = CoPlayWire.Rate * CoPlayWire.FrameBytes,
                BlockAlign = CoPlayWire.FrameBytes,
                Bits = 16
            };
            client.Initialize(0, EventCallback | AutoConvertPcm | SrcDefaultQuality, bufferTicks, 0, ref format, IntPtr.Zero);
            client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());
            return client;
        }

        public static object Service(IAudioClient client, Guid iid)
        {
            object o;
            client.GetService(ref iid, out o);
            return o;
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }

        // Loudest sample, 0..1.
        public static float Peak(byte[] pcm, int offset, int frames)
        {
            int max = 0;
            for (int i = offset, end = offset + frames * CoPlayWire.FrameBytes; i < end; i += 2)
            {
                int v = Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8));
                if (v > max) max = v;
            }
            return max / 32768f;
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int flags, long bufferDuration, long periodicity, ref Wasapi.WaveFormat format, IntPtr session);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out IntPtr data);
        void ReleaseBuffer(uint frames, int flags);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out int flags, out long devicePosition, out long qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    // Where the host's audio comes from: called with the host time of the first frame, the PCM, where it
    // starts in the array, and how many frames.
    delegate void AudioSink(long time, byte[] pcm, int offset, int frames);

    // Reads the phone's audio from its hidden input device ("<phone> A2DP SNK"), the one Windows' own stream
    // copies to the speakers. Shared mode, so Windows' copy keeps working. Each chunk is stamped with the
    // time WASAPI says its first frame arrived (qpcPosition, the same clock as HostClock).
    sealed class PhoneCapture : IDisposable
    {
        const int Silent = 2, TimestampError = 4;
        readonly AudioSink sink;
        volatile bool stopped;
        // The phone's input device ({0.0.1.00000000}.{guid}); null while there's no phone. Set by the owner.
        public volatile string InputId;
        // Reading the phone right now (the host only mutes Windows' copy while this is true).
        public volatile bool Running;
        // Real sound has come through since it started reading this phone.
        public volatile bool HadAudio;

        public PhoneCapture(AudioSink sink)
        {
            this.sink = sink;
            new Thread(Run) { IsBackground = true, Name = "CoPlay capture" }.Start();
        }

        void Run()
        {
            var enumerator = (SoundCheck.IMMDeviceEnumerator)new SoundCheck.MMDeviceEnumeratorCo();
            using (var wake = new AutoResetEvent(false))
            {
                while (!stopped)
                {
                    string id = InputId;
                    if (id == null)
                    {
                        Thread.Sleep(500);
                        continue;
                    }
                    try
                    {
                        Capture(enumerator, id, wake);
                    }
                    catch (Exception ex)
                    {
                        CoPlayLog.Write("capture: stopped: {0}", ex.Message);
                        Thread.Sleep(1000);
                    }
                }
            }
            Wasapi.Release(enumerator);
        }

        void Capture(SoundCheck.IMMDeviceEnumerator enumerator, string id, AutoResetEvent wake)
        {
            SoundCheck.IMMDevice device = null;
            IAudioClient client = null;
            object service = null;
            var buffer = new byte[CoPlayWire.Rate * CoPlayWire.FrameBytes / 10];
            try
            {
                enumerator.GetDevice(id, out device);
                client = Wasapi.Open(device, wake, 100 * HostClock.Ms);
                service = Wasapi.Service(client, Wasapi.IidCaptureClient);
                var capture = (IAudioCaptureClient)service;
                client.Start();
                Running = true;
                HadAudio = false;
                int buffers = 0, silentBuffers = 0, zeroBuffers = 0, badStamps = 0;
                float loudest = 0;
                long skew = 0, nextStats = HostClock.Now + 2 * HostClock.Second;
                CoPlayLog.Write("capture: reading the phone's input device");
                while (!stopped && InputId == id)
                {
                    wake.WaitOne(200);
                    if (HostClock.Now > nextStats)
                    {
                        nextStats = HostClock.Now + 2 * HostClock.Second;
                        CoPlayLog.Write("capture: {0} buffers ({1} flagged silent, {2} all zero), peak {3:0.00}, {4} bad timestamps, stamps up to {5:0.0} ms old",
                            buffers, silentBuffers, zeroBuffers, loudest, badStamps, skew / (double)HostClock.Ms);
                        buffers = silentBuffers = zeroBuffers = badStamps = 0;
                        loudest = 0;
                        skew = 0;
                    }
                    uint next;
                    capture.GetNextPacketSize(out next);
                    while (next > 0)
                    {
                        IntPtr data;
                        uint frames;
                        int flags;
                        long position, qpc;
                        capture.GetBuffer(out data, out frames, out flags, out position, out qpc);
                        int bytes = (int)frames * CoPlayWire.FrameBytes;
                        if (bytes > buffer.Length) buffer = new byte[bytes];
                        bool silent = (flags & Silent) != 0;
                        if (!silent) Marshal.Copy(data, buffer, 0, bytes);
                        capture.ReleaseBuffer(frames);
                        // WASAPI's stamp should be on HostClock's clock; if it's missing or far off (some
                        // drivers), use "it just arrived" instead, or every player waits for a time that never comes.
                        long now = HostClock.Now, time = qpc;
                        if ((flags & TimestampError) != 0 || Math.Abs(now - qpc) > HostClock.Second)
                        {
                            time = now - frames * HostClock.Second / CoPlayWire.Rate;
                            badStamps++;
                        }
                        else skew = Math.Max(skew, now - qpc);
                        buffers++;
                        float peak = silent ? 0 : Wasapi.Peak(buffer, 0, (int)frames);
                        if (silent) silentBuffers++;
                        else if (peak == 0) zeroBuffers++;
                        if (peak > loudest) loudest = peak;
                        // Nothing to send while the phone is paused: guests play silence for missing audio anyway.
                        if (peak > 0)
                        {
                            HadAudio = true;
                            sink(time, buffer, 0, (int)frames);
                        }
                        capture.GetNextPacketSize(out next);
                    }
                }
                client.Stop();
            }
            finally
            {
                Running = false;
                Wasapi.Release(service);
                Wasapi.Release(client);
                Wasapi.Release(device);
            }
        }

        public void Dispose()
        {
            stopped = true;
        }
    }

    // Plays CoPlay audio so that audio stamped host time T leaves this PC's speakers at host time
    // T + Delay + Adjust. The guests and the host itself all use it, so they line up.
    // Each time Windows wants more audio, it works out which host time should go out next (the target) and
    // compares it with where it's reading (the cursor): far off, it jumps; a little off (clocks drifting
    // apart), it repeats or skips one frame in 250 until it's back; otherwise it plays straight on.
    sealed class CoPlayPlayer : IDisposable
    {
        // Room for Wi-Fi hiccups. The same on every PC, or they wouldn't line up.
        public static readonly long Delay = 300 * HostClock.Ms;
        const double FrameTicks = (double)HostClock.Second / CoPlayWire.Rate;
        const int CorrectEvery = 250;
        static readonly long JumpOver = 50 * HostClock.Ms, Deadband = 2 * HostClock.Ms;

        sealed class Chunk
        {
            public long Time;
            public byte[] Pcm;
            public int Frames;
            public double End { get { return Time + Frames * FrameTicks; } }
        }

        readonly Func<long, long> toHost;
        readonly bool quiet;
        readonly List<Chunk> chunks = new List<Chunk>();
        volatile bool stopped;
        long adjust;
        // Render thread only, under the chunks lock.
        double cursor, lastError, lastBeep;
        bool hasCursor;
        int jumps, corrections;
        float peak;

        // toHost: this PC's clock to the host's. quiet: do everything but send silence to the speakers (tests).
        public CoPlayPlayer(Func<long, long> toHost, bool quiet)
        {
            this.toHost = toHost;
            this.quiet = quiet;
            new Thread(Run) { IsBackground = true, Name = "CoPlay player" }.Start();
        }

        // This PC's own correction for speakers that lag (Bluetooth speakers), in 100 ns; later is positive.
        public long Adjust
        {
            get { return Interlocked.Read(ref adjust); }
            set { Interlocked.Exchange(ref adjust, value); }
        }

        public void Add(long time, byte[] pcm, int offset, int frames)
        {
            var chunk = new Chunk { Time = time, Frames = frames, Pcm = new byte[frames * CoPlayWire.FrameBytes] };
            Buffer.BlockCopy(pcm, offset, chunk.Pcm, 0, chunk.Pcm.Length);
            lock (chunks)
            {
                // Packets can arrive out of order; keep them sorted, drop repeats, hold no more than ~5 s.
                int i = chunks.Count;
                while (i > 0 && chunks[i - 1].Time > time) i--;
                if (i > 0 && chunks[i - 1].Time == time) return;
                chunks.Insert(i, chunk);
                if (chunks.Count > 1000) chunks.RemoveAt(0);
            }
        }

        void Run()
        {
            var enumerator = (SoundCheck.IMMDeviceEnumerator)new SoundCheck.MMDeviceEnumeratorCo();
            using (var wake = new AutoResetEvent(false))
            {
                while (!stopped)
                {
                    try
                    {
                        Render(enumerator, wake);
                    }
                    catch (Exception ex)
                    {
                        CoPlayLog.Write("player: stopped: {0}", ex.Message);
                        Thread.Sleep(1000);
                    }
                }
            }
            Wasapi.Release(enumerator);
        }

        static string DefaultSpeakers(SoundCheck.IMMDeviceEnumerator enumerator)
        {
            SoundCheck.IMMDevice device = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                string id;
                device.GetId(out id);
                return id;
            }
            catch
            {
                return null;
            }
            finally { Wasapi.Release(device); }
        }

        // Plays on the default speakers until they change or this stops.
        void Render(SoundCheck.IMMDeviceEnumerator enumerator, AutoResetEvent wake)
        {
            SoundCheck.IMMDevice device = null;
            IAudioClient client = null;
            object service = null;
            try
            {
                string speakers = DefaultSpeakers(enumerator);
                if (speakers == null)
                {
                    Thread.Sleep(1000);
                    return;
                }
                enumerator.GetDevice(speakers, out device);
                client = Wasapi.Open(device, wake, 40 * HostClock.Ms);
                uint size;
                long latency;
                client.GetBufferSize(out size);
                client.GetStreamLatency(out latency);
                service = Wasapi.Service(client, Wasapi.IidRenderClient);
                var render = (IAudioRenderClient)service;
                var pcm = new byte[size * CoPlayWire.FrameBytes];
                lock (chunks) hasCursor = false;
                client.Start();
                CoPlayLog.Write("player: playing on the default speakers, buffer {0:0.0} ms, stream latency {1:0.0} ms{2}",
                    size * 1000.0 / CoPlayWire.Rate, latency / (double)HostClock.Ms, quiet ? " (quiet test: sending silence)" : "");

                long nextStats = HostClock.Now + 2 * HostClock.Second;
                while (!stopped)
                {
                    wake.WaitOne(200);
                    uint padding;
                    client.GetCurrentPadding(out padding);
                    int frames = (int)(size - padding);
                    if (frames > 0)
                    {
                        // The host time at which the first frame written now leaves the speakers.
                        long speakerAt = toHost(HostClock.Now + padding * HostClock.Second / CoPlayWire.Rate + latency);
                        Fill(pcm, frames, speakerAt);
                        IntPtr data;
                        render.GetBuffer((uint)frames, out data);
                        if (quiet) Array.Clear(pcm, 0, frames * CoPlayWire.FrameBytes);
                        Marshal.Copy(pcm, 0, data, frames * CoPlayWire.FrameBytes);
                        render.ReleaseBuffer((uint)frames, 0);
                    }

                    long now = HostClock.Now;
                    if (now < nextStats) continue;
                    nextStats = now + 2 * HostClock.Second;
                    Stats();
                    if (DefaultSpeakers(enumerator) != speakers)
                    {
                        CoPlayLog.Write("player: the default speakers changed, switching");
                        break;
                    }
                }
                client.Stop();
            }
            finally
            {
                Wasapi.Release(service);
                Wasapi.Release(client);
                Wasapi.Release(device);
            }
        }

        void Fill(byte[] pcm, int frames, long speakerAt)
        {
            double target = speakerAt - Delay - Adjust;
            lock (chunks)
            {
                double error = hasCursor ? cursor - target : 0;
                if (!hasCursor || Math.Abs(error) > JumpOver)
                {
                    if (hasCursor) jumps++;
                    cursor = target;
                    hasCursor = true;
                    error = 0;
                }
                lastError = error;
                // Positive: we're ahead of the target, so repeat frames; negative: skip frames.
                int fix = Math.Abs(error) > Deadband ? (int)(error / FrameTicks) : 0;

                int old = 0;
                while (old < chunks.Count && chunks[old].End < cursor - HostClock.Second / 2) old++;
                if (old > 0) chunks.RemoveRange(0, old);

                int ci = 0;
                for (int f = 0; f < frames; f++)
                {
                    while (ci < chunks.Count && chunks[ci].End <= cursor + FrameTicks / 2) ci++;
                    int at = f * CoPlayWire.FrameBytes;
                    bool wrote = false;
                    if (ci < chunks.Count)
                    {
                        var c = chunks[ci];
                        int i = (int)Math.Floor((cursor - c.Time) / FrameTicks + 0.5);
                        if (i >= 0 && i < c.Frames)
                        {
                            Buffer.BlockCopy(c.Pcm, i * CoPlayWire.FrameBytes, pcm, at, CoPlayWire.FrameBytes);
                            wrote = true;
                        }
                    }
                    if (!wrote) pcm[at] = pcm[at + 1] = pcm[at + 2] = pcm[at + 3] = 0;
                    else Measure(pcm, at, speakerAt + f * FrameTicks);

                    double step = FrameTicks;
                    if (fix != 0 && f % CorrectEvery == CorrectEvery / 2)
                    {
                        step = fix > 0 ? 0 : 2 * FrameTicks;
                        fix += fix > 0 ? -1 : 1;
                        corrections++;
                    }
                    cursor += step;
                }
            }
        }

        // Level for the log, and for the test beep (ToneSource): when the start of each beep leaves the
        // speakers, in host time. PCs in sync log the same.
        void Measure(byte[] pcm, int at, double leavesAt)
        {
            float v = Math.Abs((short)(pcm[at] | pcm[at + 1] << 8)) / 32768f;
            if (v > peak) peak = v;
            if (v < 0.01f || cursor - lastBeep < HostClock.Second / 2) return;
            if (cursor % HostClock.Second > 2 * HostClock.Ms) return;
            lastBeep = cursor;
            long second = (long)Math.Round(cursor / HostClock.Second);
            CoPlayLog.Write("player: beep of host second {0} leaves the speakers at +{1:0.00} ms",
                second % 1000, (leavesAt - second * HostClock.Second) / HostClock.Ms);
        }

        void Stats()
        {
            double queued, error;
            float level;
            int j, c;
            lock (chunks)
            {
                queued = chunks.Count > 0 && hasCursor ? Math.Max(0, chunks[chunks.Count - 1].End - cursor) : 0;
                level = peak;
                peak = 0;
                j = jumps;
                c = corrections;
                error = lastError;
            }
            CoPlayLog.Write("player: level {0:0.00}, queued {1:0} ms, sync error {2:0.00} ms, jumps {3}, corrections {4}",
                level, queued / HostClock.Ms, error / HostClock.Ms, j, c);
        }

        public void Dispose()
        {
            stopped = true;
        }
    }

    // Test source (hidden switch): a 100 ms beep at every whole second of the host's clock, so each PC can
    // log when a known moment reaches its speakers.
    sealed class ToneSource : IDisposable
    {
        const int Chunk = 480;
        readonly AudioSink sink;
        volatile bool stopped;

        public ToneSource(AudioSink sink)
        {
            this.sink = sink;
            new Thread(Run) { IsBackground = true, Name = "CoPlay tone" }.Start();
        }

        public static bool IsBeep(long time)
        {
            return time % HostClock.Second < 100 * HostClock.Ms;
        }

        void Run()
        {
            var pcm = new byte[Chunk * CoPlayWire.FrameBytes];
            long next = HostClock.Now;
            while (!stopped)
            {
                // Hand over each 10 ms once it has "happened", like a capture would.
                while (next + Chunk * HostClock.Second / CoPlayWire.Rate <= HostClock.Now)
                {
                    for (int i = 0; i < Chunk; i++)
                    {
                        long t = next + i * HostClock.Second / CoPlayWire.Rate;
                        short v = IsBeep(t) ? (short)(8000 * Math.Sin(2 * Math.PI * 880 * (t % HostClock.Second) / HostClock.Second)) : (short)0;
                        int at = i * CoPlayWire.FrameBytes;
                        pcm[at] = pcm[at + 2] = (byte)v;
                        pcm[at + 1] = pcm[at + 3] = (byte)(v >> 8);
                    }
                    sink(next, pcm, 0, Chunk);
                    next += Chunk * HostClock.Second / CoPlayWire.Rate;
                }
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            stopped = true;
        }
    }
}
