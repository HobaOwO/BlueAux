using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BlueAux
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
}
