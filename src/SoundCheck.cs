using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BlueAux
{
    // What Windows' audio system says about the phone's sound right now.
    // Windows plays a phone through a system stream (svchost, "<phone> A2DP SNK") that copies the phone's
    // hidden input device to your speakers. The stream's ID names that input, so we can read both ends:
    // Input = sound arriving from the phone, Output = sound going to the speakers.
    struct SoundLevels
    {
        public bool Found;
        public float Output;
        public float Input;
        public bool InputReadable;
        public float StreamVolume;
        public bool StreamMuted;
        public float SpeakerVolume;
        public bool SpeakerMuted;
        public bool OnDefaultSpeakers;
        public string Speakers;

        // The phone is coming through but the PC's own volume makes it silent.
        public bool PcSilenced
        {
            get { return Found && (SpeakerMuted || SpeakerVolume < 0.005f || StreamMuted || StreamVolume < 0.005f); }
        }
    }

    static class SoundCheck
    {
        const int Render = 0, ActiveOnly = 1, ClsctxAll = 23;
        static readonly Guid IidSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        static readonly Guid IidMeter = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
        static readonly Guid IidEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        static IMMDeviceEnumerator enumerator;

        public static SoundLevels Read()
        {
            var result = new SoundLevels();
            if (enumerator == null) enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();

            string defaultId = null;
            IMMDevice def = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(Render, 0, out def);
                def.GetId(out defaultId);
            }
            catch { }
            finally { Release(def); }

            IMMDeviceCollection devices = null;
            try
            {
                enumerator.EnumAudioEndpoints(Render, ActiveOnly, out devices);
                int count;
                devices.GetCount(out count);
                for (int i = 0; i < count && !result.Found; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        devices.Item(i, out device);
                        string id;
                        device.GetId(out id);
                        if (ReadPhoneStream(device, ref result))
                        {
                            result.OnDefaultSpeakers = id == defaultId;
                            result.Speakers = FriendlyName(device);
                            ReadSpeakerVolume(device, ref result);
                        }
                    }
                    finally { Release(device); }
                }
            }
            finally { Release(devices); }
            return result;
        }

        static bool ReadPhoneStream(IMMDevice device, ref SoundLevels result)
        {
            object manager = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                Guid iid = IidSessionManager2;
                device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out manager);
                ((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions);
                int count;
                sessions.GetCount(out count);
                for (int k = 0; k < count; k++)
                {
                    IAudioSessionControl2 session = null;
                    try
                    {
                        sessions.GetSession(k, out session);
                        string name, sid;
                        session.GetDisplayName(out name);
                        session.GetSessionIdentifier(out sid);
                        bool phone = (name != null && name.IndexOf("A2DP", StringComparison.OrdinalIgnoreCase) >= 0)
                            || (sid != null && sid.IndexOf("svchost.exe%b{", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!phone) continue;

                        result.Found = true;
                        float peak;
                        ((IAudioMeterInformation)session).GetPeakValue(out peak);
                        result.Output = peak;
                        var volume = (ISimpleAudioVolume)session;
                        float level;
                        bool muted;
                        volume.GetMasterVolume(out level);
                        volume.GetMute(out muted);
                        result.StreamVolume = level;
                        result.StreamMuted = muted;
                        ReadPhoneInput(sid, ref result);
                        return true;
                    }
                    finally { Release(session); }
                }
            }
            catch { }
            finally
            {
                Release(sessions);
                Release(manager);
            }
            return false;
        }

        // The stream ID ends in "%b{<guid>}": the phone's hidden input device {0.0.1.00000000}.{<guid>}.
        static void ReadPhoneInput(string sid, ref SoundLevels result)
        {
            if (sid == null) return;
            int start = sid.LastIndexOf("%b{", StringComparison.Ordinal);
            if (start < 0) return;
            int end = sid.IndexOf('}', start);
            if (end < 0) return;
            string inputId = "{0.0.1.00000000}." + sid.Substring(start + 2, end - start - 1).ToLowerInvariant();

            IMMDevice input = null;
            object meter = null;
            try
            {
                enumerator.GetDevice(inputId, out input);
                Guid iid = IidMeter;
                input.Activate(ref iid, ClsctxAll, IntPtr.Zero, out meter);
                float peak;
                ((IAudioMeterInformation)meter).GetPeakValue(out peak);
                result.Input = peak;
                result.InputReadable = true;
            }
            catch { }
            finally
            {
                Release(meter);
                Release(input);
            }
        }

        static void ReadSpeakerVolume(IMMDevice device, ref SoundLevels result)
        {
            object endpoint = null;
            try
            {
                Guid iid = IidEndpointVolume;
                device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out endpoint);
                float level;
                bool muted;
                ((IAudioEndpointVolume)endpoint).GetMasterVolumeLevelScalar(out level);
                ((IAudioEndpointVolume)endpoint).GetMute(out muted);
                result.SpeakerVolume = level;
                result.SpeakerMuted = muted;
            }
            catch
            {
                result.SpeakerVolume = 1;
            }
            finally { Release(endpoint); }
        }

        static string FriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                device.OpenPropertyStore(0, out store);
                var key = new PropertyKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
                PropVariant value;
                store.GetValue(ref key, out value);
                if (value.Vt == 31 && value.Pointer != IntPtr.Zero) return Marshal.PtrToStringUni(value.Pointer);
            }
            catch { }
            finally { Release(store); }
            return "speakers";
        }

        // Hand COM objects back right away instead of waiting for the garbage collector.
        static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid Fmtid; public int Pid; }

        [StructLayout(LayoutKind.Sequential)]
        struct PropVariant { public ushort Vt; public ushort R1, R2, R3; public IntPtr Pointer; public IntPtr Extra; }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            void GetCount(out int count);
            void Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            void OpenPropertyStore(int access, out IPropertyStore store);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            void GetCount(out int count);
            void GetAt(int index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            void GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            void GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            void GetCount(out int count);
            void GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            void GetState(out int state);
            void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            void GetGroupingParam(out Guid param);
            void SetGroupingParam(ref Guid param, ref Guid context);
            void RegisterAudioSessionNotification(IntPtr client);
            void UnregisterAudioSessionNotification(IntPtr client);
            void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation
        {
            void GetPeakValue(out float peak);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            void SetMasterVolume(float level, ref Guid context);
            void GetMasterVolume(out float level);
            void SetMute(bool mute, ref Guid context);
            void GetMute(out bool mute);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            void RegisterControlChangeNotify(IntPtr notify);
            void UnregisterControlChangeNotify(IntPtr notify);
            void GetChannelCount(out int count);
            void SetMasterVolumeLevel(float levelDb, ref Guid context);
            void SetMasterVolumeLevelScalar(float level, ref Guid context);
            void GetMasterVolumeLevel(out float levelDb);
            void GetMasterVolumeLevelScalar(out float level);
            void SetChannelVolumeLevel(int channel, float levelDb, ref Guid context);
            void SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
            void GetChannelVolumeLevel(int channel, out float levelDb);
            void GetChannelVolumeLevelScalar(int channel, out float level);
            void SetMute(bool mute, ref Guid context);
            void GetMute(out bool mute);
        }
    }

    // Watches the connected phone's sound once a second and repairs connections that are stuck silent:
    //  - The first connection we make to a phone that just came back often carries no sound at all, even
    //    once you press play, and from here that looks exactly like a paused phone. A second connection
    //    made right after it works, paused or not. So when we've connected and nothing has come through
    //    after a few seconds, we reconnect once ("settle").
    //  - Sound arrives from the phone but none reaches the speakers, or Windows never set up the stream
    //    that plays the phone at all: rebuilt too (at most a few times, so it can never loop).
    // Everything is written to sound-check.log for bug reports.
    sealed class SoundWatch
    {
        const float Audible = 0.01f;
        const float Silent = 0.002f;
        static readonly TimeSpan SettleAfter = TimeSpan.FromSeconds(6);
        static readonly TimeSpan SettleGap = TimeSpan.FromMinutes(1);
        static readonly TimeSpan HealGap = TimeSpan.FromSeconds(45);
        static readonly TimeSpan HealWindow = TimeSpan.FromMinutes(10);
        const int MaxHealsPerWindow = 3;

        readonly float[] recentInput = new float[3];
        DeviceSession watched;
        DateTime watchStart;
        int samples;
        int stuckSamples;
        int missingSamples;
        bool heardSound;
        bool streamSeen;
        bool pcSilenced;
        bool settlePending;
        bool flowed;
        DateTime lastSettle = DateTime.MinValue;
        DateTime lastHeal = DateTime.MinValue;
        DateTime healWindowStart = DateTime.MinValue;
        int healsInWindow;
        string lastDiag;
        DateTime lastDiagTime;
        DiagLog diag;

        public event Action Changed;

        // The phone is connected but this PC's volume is muted or at zero.
        public bool PcSilenced { get { return pcSilenced; } }

        // Sound has come through at least once since this connection started.
        public bool HeardSound { get { return heardSound; } }

        public void Init(string folder)
        {
            diag = new DiagLog(Path.Combine(folder, "sound-check.log"));
        }

        // Returns true when the phone is audibly playing through the PC.
        public bool Tick(DeviceSession active, DateTime now)
        {
            if (active == null)
            {
                if (watched != null) Diag(now, watched.DisplayName + ": not connected any more");
                watched = null;
                SetSilenced(false);
                return false;
            }
            if (active != watched)
            {
                watched = active;
                watchStart = now;
                samples = stuckSamples = missingSamples = 0;
                heardSound = false;
                flowed = false;
                settlePending = active.ShouldSettle;
                Array.Clear(recentInput, 0, recentInput.Length);
                lastDiag = null;
                Diag(now, active.DisplayName + (settlePending ? ": we connected it" : ": connected by the phone or a rebuild") + ", watching the sound");
            }

            SoundLevels levels;
            try
            {
                levels = SoundCheck.Read();
            }
            catch (Exception ex)
            {
                Diag(now, "couldn't read sound levels: " + ex.Message);
                return false;
            }
            samples++;
            DiagLevels(now, levels);
            SetSilenced(levels.PcSilenced);

            if (!levels.Found)
            {
                // Windows normally sets up the stream within a second or two of connecting, and keeps it
                // even while the music is paused. Missing for this long means the connection is broken.
                // (Only once we've seen that stream on this PC, so an unusual setup can't cause a reconnect loop.)
                missingSamples++;
                if (streamSeen && missingSamples >= 12 && now - watchStart > TimeSpan.FromSeconds(15))
                    Heal(now, "Windows isn't playing its sound");
                return false;
            }
            missingSamples = 0;
            streamSeen = true;

            // Anything at all coming through means this connection works (a broken one reads 0, or a
            // constant 0.0005). Otherwise reconnect once, a few seconds in.
            if (levels.Input > Silent || levels.Output > Silent) flowed = true;
            if (settlePending && (flowed || now - watchStart >= SettleAfter))
            {
                settlePending = false;
                if (!flowed)
                {
                    Settle(now);
                    return false;
                }
            }

            bool playing = levels.Output > Audible;
            if (playing && !heardSound)
            {
                heardSound = true;
                Diag(now, watched.DisplayName + ": sound is playing");
            }

            recentInput[samples % recentInput.Length] = levels.Input;
            if (InputIsLive() && levels.Output < Silent && !levels.StreamMuted && levels.StreamVolume > 0.05f) stuckSamples++;
            else stuckSamples = 0;
            if (stuckSamples >= 3) Heal(now, "its sound reaches the PC but doesn't play");
            return playing;
        }

        // Real music moves; a meter left over from before a pause sits still. So "live" means loud
        // enough and changing from one second to the next.
        bool InputIsLive()
        {
            if (samples < recentInput.Length) return false;
            float a = recentInput[0], b = recentInput[1], c = recentInput[2];
            if (Math.Max(a, Math.Max(b, c)) < 0.02f) return false;
            return a != b && b != c && a != c;
        }

        void Settle(DateTime now)
        {
            var session = watched;
            if (now - lastSettle < SettleGap)
            {
                Diag(now, session.DisplayName + ": no sound yet, but reconnected less than a minute ago, so leaving it");
                return;
            }
            lastSettle = now;
            Diag(now, session.DisplayName + ": no sound in the first seconds; reconnecting once so it works when you press play");
            watched = null;
            session.Settle();
        }

        void Heal(DateTime now, string reason)
        {
            stuckSamples = 0;
            missingSamples = 0;
            if (now - lastHeal < HealGap) return;
            if (now - healWindowStart > HealWindow)
            {
                healWindowStart = now;
                healsInWindow = 0;
            }
            if (healsInWindow >= MaxHealsPerWindow)
            {
                Diag(now, "would reconnect (" + reason + ") but already tried " + MaxHealsPerWindow + " times");
                return;
            }
            healsInWindow++;
            lastHeal = now;
            var session = watched;
            Diag(now, session.DisplayName + ": " + reason + "; reconnecting to fix it");
            Log.Info("{0}: connected but silent ({1}), so reconnecting it", session.DisplayName, reason);
            watched = null;
            session.FixCommand.Execute(null);
        }

        void SetSilenced(bool value)
        {
            if (pcSilenced == value) return;
            pcSilenced = value;
            var handler = Changed;
            if (handler != null) handler();
        }

        // Writes a line when something meaningful changes (silent / faint / sound, volume, speakers),
        // and every 30 s otherwise, so the file stays small but shows what happened.
        void DiagLevels(DateTime now, SoundLevels l)
        {
            string line;
            if (!l.Found) line = "no phone stream in Windows";
            else
            {
                line = string.Format("out={0} in={1} stream vol={2:0.00}{3} speakers='{4}'{5} vol={6:0.00}{7}",
                    Bucket(l.Output), l.InputReadable ? Bucket(l.Input) : "unreadable",
                    l.StreamVolume, l.StreamMuted ? " MUTED" : "",
                    l.Speakers, l.OnDefaultSpeakers ? " (default)" : " (NOT default)",
                    l.SpeakerVolume, l.SpeakerMuted ? " MUTED" : "");
            }
            if (line == lastDiag && now - lastDiagTime < TimeSpan.FromSeconds(30)) return;
            lastDiag = line;
            lastDiagTime = now;
            string detail = l.Found ? string.Format("  [{0:0.0000} / {1:0.0000}]", l.Output, l.Input) : "";
            Diag(now, line + detail);
        }

        static string Bucket(float peak)
        {
            if (peak < Silent) return "silent";
            if (peak < 0.02f) return "faint";
            return "sound";
        }

        void Diag(DateTime now, string text)
        {
            if (diag != null) diag.Write(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  " + text);
        }
    }

    // A small log that starts over (keeping one old copy) when it reaches 256 KB.
    sealed class DiagLog
    {
        const long MaxBytes = 256 * 1024;
        readonly string path;

        public DiagLog(string path)
        {
            this.path = path;
        }

        public void Write(string line)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
