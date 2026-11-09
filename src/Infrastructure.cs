using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Foundation;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle(BlueAux.AppInfo.Name)]
[assembly: System.Reflection.AssemblyProduct(BlueAux.AppInfo.Name)]
[assembly: System.Reflection.AssemblyDescription(BlueAux.AppInfo.Tagline)]
[assembly: System.Reflection.AssemblyVersion(BlueAux.AppInfo.Version + ".0.0")]
[assembly: System.Reflection.AssemblyFileVersion(BlueAux.AppInfo.Version + ".0.0")]

namespace BlueAux
{
    public static class AppInfo
    {
        public const string Name = "BlueAux";
        public const string Tagline = "Use your PC as a Bluetooth speaker for your phone";
        // Used for the settings folder, the start-up entry and the single-instance lock.
        public const string Id = "BlueAux";
        public const string Version = "1.2";
        public const string RepoUrl = "https://github.com/HobaOwO/BlueAux";
        // Earlier Ids, so settings and the start-up entry carry over after a rename.
        public static readonly string[] OldIds = { "PhoneSpeaker" };
    }

    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    public sealed class RelayCommand : ICommand
    {
        readonly Action execute;

        public RelayCommand(Action execute)
        {
            this.execute = execute;
        }

        public event EventHandler CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object parameter)
        {
            return true;
        }

        public void Execute(object parameter)
        {
            execute();
        }
    }

    // System.Runtime.WindowsRuntime's await helpers are compiled against the merged Windows.winmd
    // from the Windows SDK, which a stock Windows install doesn't have, so we bring our own.
    public static class WinRtAwaitExtensions
    {
        public static Task<T> AsTask<T>(this IAsyncOperation<T> operation)
        {
            var tcs = new TaskCompletionSource<T>();
            operation.Completed = (op, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed) tcs.TrySetResult(op.GetResults());
                    else if (status == AsyncStatus.Error) tcs.TrySetException(op.ErrorCode ?? new Exception("Windows operation failed"));
                    else tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };
            return tcs.Task;
        }

        public static Task AsTask(this IAsyncAction action)
        {
            var tcs = new TaskCompletionSource<bool>();
            action.Completed = (op, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed)
                    {
                        op.GetResults();
                        tcs.TrySetResult(true);
                    }
                    else if (status == AsyncStatus.Error) tcs.TrySetException(op.ErrorCode ?? new Exception("Windows operation failed"));
                    else tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };
            return tcs.Task;
        }

        public static TaskAwaiter<T> GetAwaiter<T>(this IAsyncOperation<T> operation)
        {
            return operation.AsTask().GetAwaiter();
        }

        public static TaskAwaiter GetAwaiter(this IAsyncAction action)
        {
            return action.AsTask().GetAwaiter();
        }
    }

    public sealed class LogEntry
    {
        public DateTime Time { get; set; }
        public string Message { get; set; }
        public string TimeText { get { return Time.ToString("HH:mm"); } }
    }

    public static class Log
    {
        static readonly object Sync = new object();
        static string filePath;

        public static event Action<LogEntry> Written;

        public static string FilePath { get { return filePath; } }

        public static void Init(string folder)
        {
            filePath = Path.Combine(folder, "activity.log");
            try
            {
                var info = new FileInfo(filePath);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    File.Copy(filePath, filePath + ".old", true);
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }

        public static void Info(string format, params object[] args)
        {
            string message = args == null || args.Length == 0 ? format : string.Format(format, args);
            var entry = new LogEntry { Time = DateTime.Now, Message = message };
            lock (Sync)
            {
                if (filePath != null)
                {
                    try
                    {
                        File.AppendAllText(filePath, entry.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                    }
                    catch
                    {
                    }
                }
            }
            var handler = Written;
            if (handler != null) handler(entry);
        }

        public static void Error(string context, Exception ex)
        {
            Info("{0}: {1} (0x{2:X8})", context, ex.Message.Trim(), ex.HResult);
        }
    }

    static class Shell
    {
        public static void Open(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't open " + target, ex);
            }
        }
    }

    static class Native
    {
        public const uint EsContinuous = 0x80000000;
        public const uint EsSystemRequired = 0x00000001;

        public const int DwmUseImmersiveDarkMode = 20;
        public const int DwmSystemBackdropType = 38;
        public const int DwmBackdropMica = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct Margins
        {
            public int Left, Right, Top, Bottom;
        }

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint flags);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        // Ask Windows to start us again (in the tray) if we crash or hang, but not after updates or reboots.
        public const int RestartNoPatch = 4;
        public const int RestartNoReboot = 8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterApplicationRestart(string commandLine, int flags);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        // Free what the window used and hand idle memory back to Windows; it is paged back in if needed.
        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
    }
}
