using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace BlueAux
{
    // A Start menu shortcut carrying BlueAux's own app ID. Windows takes a notification's name and icon from
    // it, and keeps them per app ID from the first time that app notified: without this, notifications kept
    // the icon of whichever BlueAux.exe first ran from that path. shortcut: a later icon change needs a new ID.
    static class Shortcut
    {
        const string AppId = "HobaOwO.BlueAux.App";

        public static void Register()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk");
                var link = (IShellLinkW)new ShellLink();
                link.SetPath(exe);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe));
                link.SetIconLocation(exe, 0);
                var store = (IPropertyStore)link;
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                var value = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(AppId) };
                try
                {
                    store.SetValue(ref key, ref value);
                    store.Commit();
                }
                finally
                {
                    Marshal.FreeCoTaskMem(value.Value);
                }
                ((IPersistFile)link).Save(path, true);
                SetCurrentProcessExplicitAppUserModelID(AppId);
            }
            catch (Exception)
            {
                // Only the notifications' icon depends on this.
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath(IntPtr file, int size, IntPtr data, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription(IntPtr name, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory(IntPtr dir, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments(IntPtr args, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation(IntPtr path, int size, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, IntPtr value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey
        {
            public Guid Format;
            public uint Id;
        }

        // Only the string case (VT_LPWSTR) is needed; the padding keeps it PROPVARIANT's size on 64-bit.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Value;
        }
    }
}
