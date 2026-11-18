using System;
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
                CoPlayLog.Write("capture: reading the phone's input device");
                while (!stopped && InputId == id)
                {
                    wake.WaitOne(200);
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
                        long time = (flags & TimestampError) != 0 ? HostClock.Now - frames * HostClock.Second / CoPlayWire.Rate : qpc;
                        // Nothing to send while the phone is paused: guests play silence for missing audio anyway.
                        if (!silent && Wasapi.Peak(buffer, 0, (int)frames) > 0) sink(time, buffer, 0, (int)frames);
                        capture.GetNextPacketSize(out next);
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
