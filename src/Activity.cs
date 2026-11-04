using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PhoneSpeaker
{
    // Decides whether someone is using the PC, the way chat apps decide you're "idle":
    // keyboard or mouse input counts, a locked PC doesn't. Phone audio playing through the PC
    // also counts as use, so a song isn't cut off just because nobody touched the mouse.
    sealed class ActivityMonitor : IDisposable
    {
        DateTime lastAudio = DateTime.MinValue;
        DateTime lockedAt = DateTime.MinValue;
        volatile bool locked;

        public ActivityMonitor()
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        public void NoteAudio()
        {
            lastAudio = DateTime.UtcNow;
        }

        public TimeSpan IdleFor()
        {
            var now = DateTime.UtcNow;
            // Input on the lock screen doesn't mean anyone is at the PC.
            DateTime lastInput = locked ? lockedAt : now - SinceLastInput();
            DateTime last = lastInput > lastAudio ? lastInput : lastAudio;
            return now - last;
        }

        public void Dispose()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                lockedAt = DateTime.UtcNow;
                locked = true;
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                locked = false;
            }
        }

        static TimeSpan SinceLastInput()
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            // Both are milliseconds since boot and wrap after 49 days; unsigned subtraction handles that.
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LastInputInfo info);
    }

    // Windows plays the phone through a system audio stream named "<phone> A2DP SNK" on the output
    // device; its level meter tells us whether anything is actually playing.
    static class PhoneAudioMeter
    {
        const int Render = 0, Active = 1, ClsctxAll = 23;
        const float Audible = 0.01f;
        static readonly Guid IidSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        public static bool IsPlaying()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDeviceCollection devices;
                enumerator.EnumAudioEndpoints(Render, Active, out devices);
                int count;
                devices.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device;
                    devices.Item(i, out device);
                    object manager;
                    Guid iid = IidSessionManager2;
                    device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out manager);
                    IAudioSessionEnumerator sessions;
                    ((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions);
                    int sessionCount;
                    sessions.GetCount(out sessionCount);
                    for (int k = 0; k < sessionCount; k++)
                    {
                        IAudioSessionControl2 session;
                        sessions.GetSession(k, out session);
                        string name;
                        session.GetDisplayName(out name);
                        if (name == null || name.IndexOf("A2DP", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        float peak;
                        ((IAudioMeterInformation)session).GetPeakValue(out peak);
                        if (peak > Audible) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't read the phone's audio level", ex);
            }
            return false;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
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
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation
        {
            void GetPeakValue(out float peak);
        }
    }
}
