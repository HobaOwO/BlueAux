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
        // Windows' state for the phone's stream: 0 inactive, 1 active, 2 expired.
        public int StreamState;
        public float Output;
        public float Input;
        public bool InputReadable;
        // The same for the stream that reads the phone's hidden input (for the log).
        public bool InputStateKnown;
        public int InputState;
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
                        int streamState;
                        session.GetState(out streamState);
                        result.StreamState = streamState;
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
                ReadInputState(input, ref result);
            }
            catch { }
            finally
            {
                Release(meter);
                Release(input);
            }
        }

        static void ReadInputState(IMMDevice input, ref SoundLevels result)
        {
            object manager = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                Guid iid = IidSessionManager2;
                input.Activate(ref iid, ClsctxAll, IntPtr.Zero, out manager);
                ((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions);
                int count;
                sessions.GetCount(out count);
                result.InputStateKnown = true;
                result.InputState = -1;
                for (int k = 0; k < count; k++)
                {
                    IAudioSessionControl2 session = null;
                    try
                    {
                        sessions.GetSession(k, out session);
                        int state;
                        session.GetState(out state);
                        if (state == 1 || result.InputState < 0) result.InputState = state;
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
    //    once you press play. A second connection fixes it: always when made while the phone plays, sometimes
    //    when made while nothing plays. So when we've connected and nothing has come through after a few
    //    seconds, we reconnect once ("settle").
    //  - An iPhone tells us when it plays (PhoneMedia). Playing, but nothing has reached the speakers
    //    since this connection started: that's the broken kind, so reconnect right then. If the phone
    //    paused when its audio went away for a moment, press play on it again.
    //  - Apps like TikTok and Instagram don't tell us they're playing, but a press of the phone's volume
    //    buttons does reach us. Pressed while nothing has come through on this connection: you're trying
    //    to listen and hear nothing, so reconnect then, while it plays.
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
        // A working connection carries sound within a second of pressing play; this leaves room for
        // quiet intros without making you wait long.
        static readonly TimeSpan PlayingSilentFor = TimeSpan.FromSeconds(3);
        static readonly TimeSpan PlayHealMemory = TimeSpan.FromMinutes(10);
        static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(20);

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
        bool playCheckDone;
        DateTime volumeHandled = DateTime.MinValue;
        DateTime playingSince = DateTime.MaxValue;
        PhonePlayback lastPlayback;
        DeviceSession playHealed;
        DateTime playHealAt;
        DeviceSession resumeFor;
        DateTime resumeFrom;
        DateTime lastSettle = DateTime.MinValue;
        DateTime lostAt = DateTime.UtcNow;
        string lastLostLine;
        static readonly TimeSpan WatchAfterLoss = TimeSpan.FromMinutes(3);
        // Connecting can change the phone's volume by itself; and sound that's just starting gets a moment.
        static readonly TimeSpan VolumeSettle = TimeSpan.FromSeconds(5);
        static readonly TimeSpan VolumeWait = TimeSpan.FromSeconds(2);
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

        public void Note(string text)
        {
            Diag(DateTime.UtcNow, text);
        }

        // Returns true when the phone is audibly playing through the PC.
        public bool Tick(DeviceSession active, PhoneMedia media, DateTime now)
        {
            if (active == null)
            {
                if (watched != null)
                {
                    Diag(now, watched.DisplayName + ": not connected any more");
                    lostAt = now;
                    lastLostLine = null;
                }
                watched = null;
                SetSilenced(false);
                // For a few minutes after losing the phone, note what Windows does with its stream.
                if (now - lostAt < WatchAfterLoss) DiagWhileDisconnected(now);
                return false;
            }
            if (active != watched)
            {
                watched = active;
                watchStart = now;
                samples = stuckSamples = missingSamples = 0;
                heardSound = false;
                flowed = false;
                playCheckDone = false;
                playingSince = DateTime.MaxValue;
                lastPlayback = PhonePlayback.Unknown;
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

            var playback = media != null ? media.PlaybackOf(active) : PhonePlayback.Unknown;
            if (playback != lastPlayback)
            {
                lastPlayback = playback;
                Diag(now, active.DisplayName + (playback == PhonePlayback.Playing ? ": the phone says it's playing"
                    : playback == PhonePlayback.Paused ? ": the phone says it's paused" : ": can't see whether the phone is playing"));
            }

            // Anything at all reaching the speakers means this connection works (a broken one reads 0).
            // The very first reading can be left over from before, so it doesn't count.
            if (samples > 1 && levels.Found && levels.Output > Silent)
            {
                flowed = true;
                if (playHealed == active) playHealed = null;
            }
            if (resumeFor != null) Resume(now, media, playback);
            if (CheckPlayingButSilent(now, playback, levels)) return false;
            if (CheckVolumePressed(now, media, levels)) return false;

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

            // Nothing has come through a few seconds in: reconnect once.
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

        // The phone says it's playing, and nothing has reached the speakers since this connection started:
        // the broken kind of connection. Reconnecting while it plays fixes it.
        // A connection that has played before is fine, so silence on it means the phone is playing
        // somewhere else (AirPods, its own speaker): leave that alone. Same if reconnecting didn't help.
        bool CheckPlayingButSilent(DateTime now, PhonePlayback playback, SoundLevels levels)
        {
            if (playCheckDone || flowed || playback != PhonePlayback.Playing)
            {
                playingSince = DateTime.MaxValue;
                return false;
            }
            // Muted on this PC: silent on purpose. No stream yet: the missing-stream rule handles that.
            if (!levels.Found || levels.StreamMuted || levels.StreamVolume < 0.05f) return false;
            if (playingSince == DateTime.MaxValue) playingSince = now;
            if (now - playingSince < PlayingSilentFor) return false;

            playCheckDone = true;
            var session = watched;
            // Windows only marks the phone's stream active once the phone starts sending to this PC. A
            // broken connection is active but silent; an idle stream means the phone isn't sending here
            // at all, so it's playing somewhere else, and reconnecting would only pull the music over.
            if (levels.StreamState != 1)
            {
                Diag(now, session.DisplayName + ": the phone says it's playing, but it isn't sending to this PC; it's probably playing somewhere else (AirPods, its own speaker), so leaving it");
                return false;
            }
            if (playHealed == session && now - playHealAt < PlayHealMemory)
            {
                Diag(now, session.DisplayName + ": still no sound after reconnecting while it played; it's probably playing somewhere else (AirPods, its own speaker), so leaving it");
                return false;
            }
            if (!Heal(now, "it's playing but no sound comes through")) return false;
            playHealed = session;
            playHealAt = now;
            return true;
        }

        // You pressed a volume button on the phone and nothing has reached the speakers since this
        // connection started: you're listening to something and hearing nothing. That's the broken kind
        // of connection, whatever app is playing, and reconnecting while it plays fixes it.
        // Not if Windows' stream for the phone is idle: then the phone is playing somewhere else (AirPods,
        // its own speaker), and reconnecting would only pull the sound over.
        bool CheckVolumePressed(DateTime now, PhoneMedia media, SoundLevels levels)
        {
            if (media == null || flowed) return false;
            var pressed = media.VolumeChangedAt(watched);
            if (pressed <= volumeHandled || pressed < watchStart + VolumeSettle) return false;
            if (now - pressed < VolumeWait) return false;
            volumeHandled = pressed;
            var session = watched;
            if (!levels.Found || levels.StreamMuted || levels.StreamVolume < 0.05f) return false;
            if (levels.StreamState != 1)
            {
                Diag(now, session.DisplayName + ": volume pressed with no sound, but the phone isn't sending to this PC; it's probably playing somewhere else, so leaving it");
                return false;
            }
            if (now - lastHeal < HealGap)
            {
                Diag(now, session.DisplayName + ": volume pressed with no sound, but we reconnected less than " + (int)HealGap.TotalSeconds + " seconds ago, so leaving it");
                return false;
            }
            return Heal(now, "you pressed a volume button but no sound comes through");
        }

        // We're about to reconnect this phone ourselves.
        void Drop(DateTime now)
        {
            watched = null;
            lostAt = now;
            lastLostLine = null;
        }

        // Reconnecting takes the phone's audio away for a moment, and iPhones pause when that happens.
        // If it paused since then, press play on it again (once, and only shortly after).
        void Resume(DateTime now, PhoneMedia media, PhonePlayback playback)
        {
            if (now - resumeFrom > ResumeWindow || resumeFor != watched)
            {
                if (now - resumeFrom > ResumeWindow) resumeFor = null;
                return;
            }
            if (samples < 2 || playback != PhonePlayback.Paused || media.PlaybackChangedAt < resumeFrom) return;
            resumeFor = null;
            if (media.Send(watched, PhoneMedia.PlayCommand))
                Diag(now, watched.DisplayName + ": the phone paused while we reconnected, so pressing play on it");
        }

        void NoteReconnect(DateTime now, DeviceSession session)
        {
            if (lastPlayback != PhonePlayback.Playing) return;
            resumeFor = session;
            resumeFrom = now;
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
            NoteReconnect(now, session);
            Drop(now);
            session.Settle();
        }

        bool Heal(DateTime now, string reason)
        {
            stuckSamples = 0;
            missingSamples = 0;
            if (now - lastHeal < HealGap) return false;
            if (now - healWindowStart > HealWindow)
            {
                healWindowStart = now;
                healsInWindow = 0;
            }
            if (healsInWindow >= MaxHealsPerWindow)
            {
                Diag(now, "would reconnect (" + reason + ") but already tried " + MaxHealsPerWindow + " times");
                return false;
            }
            healsInWindow++;
            lastHeal = now;
            var session = watched;
            Diag(now, session.DisplayName + ": " + reason + "; reconnecting to fix it");
            Log.Info("{0}: connected but silent ({1}), so reconnecting it", session.DisplayName, reason);
            NoteReconnect(now, session);
            Drop(now);
            session.FixCommand.Execute(null);
            return true;
        }

        void DiagWhileDisconnected(DateTime now)
        {
            SoundLevels l;
            try
            {
                l = SoundCheck.Read();
            }
            catch
            {
                return;
            }
            string line = !l.Found ? "no phone stream in Windows" : "stream " + StateName(l.StreamState)
                + (!l.InputStateKnown ? "" : l.InputState == -1 ? " no in-stream" : " in-stream " + StateName(l.InputState));
            if (line == lastLostLine) return;
            lastLostLine = line;
            Diag(now, "while disconnected: " + line + (l.Found ? string.Format("  [{0:0.0000} / {1:0.0000}]", l.Output, l.Input) : ""));
        }

        static string StateName(int state)
        {
            return state == 1 ? "active" : state == 0 ? "inactive" : "expired";
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
                line = string.Format("out={0} in={1} stream {8}{9} vol={2:0.00}{3} speakers='{4}'{5} vol={6:0.00}{7}",
                    Bucket(l.Output), l.InputReadable ? Bucket(l.Input) : "unreadable",
                    l.StreamVolume, l.StreamMuted ? " MUTED" : "",
                    l.Speakers, l.OnDefaultSpeakers ? " (default)" : " (NOT default)",
                    l.SpeakerVolume, l.SpeakerMuted ? " MUTED" : "",
                    l.StreamState == 1 ? "active" : l.StreamState == 0 ? "inactive" : "expired",
                    !l.InputStateKnown ? "" : l.InputState == 1 ? " in-stream active" : l.InputState == 0 ? " in-stream inactive"
                        : l.InputState == 2 ? " in-stream expired" : " no in-stream");
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
