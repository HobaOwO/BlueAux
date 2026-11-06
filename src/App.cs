using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BlueAux
{
    public static class Program
    {
        const string MutexName = "Local\\" + AppInfo.Id + ".SingleInstance";
        const string ShowEventName = "Local\\" + AppInfo.Id + ".Show";

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--make-icon")
            {
                IconFactory.SaveIco(args[1]);
                return 0;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Already running (probably in the tray): just bring that one up.
                    try
                    {
                        using (var show = EventWaitHandle.OpenExisting(ShowEventName)) show.Set();
                    }
                    catch
                    {
                    }
                    return 0;
                }

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    var app = new App(args.Contains("--minimized"), showEvent);
                    return app.Run();
                }
            }
        }
    }

    public sealed class App : Application
    {
        readonly bool startHidden;
        readonly EventWaitHandle showEvent;
        AppSettings settings;
        ReceiverEngine engine;
        MainViewModel viewModel;
        // Only exists while it's on screen; closing it to the tray throws it away to save memory.
        MainWindow window;
        TrayIcon tray;
        DispatcherTimer trimTimer;
        bool keepingAwake;
        bool exiting;

        public App(bool startHidden, EventWaitHandle showEvent)
        {
            this.startHidden = startHidden;
            this.showEvent = showEvent;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppSettings.MoveFromOldFolder();
            Directory.CreateDirectory(AppSettings.Folder);
            Log.Init(AppSettings.Folder);
            DispatcherUnhandledException += (s, args) =>
            {
                Log.Error("Unexpected error", args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                if (ex != null) Log.Error("Fatal error", ex);
            };
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log.Error("Background error", args.Exception);
                args.SetObserved();
            };

            settings = AppSettings.Load();
            AutoStart.Repair();
            // Windows only does this for apps that have been running at least a minute, so no restart loops.
            Native.RegisterApplicationRestart("--minimized", Native.RestartNoPatch | Native.RestartNoReboot);
            Resources.MergedDictionaries.Add((ResourceDictionary)Xaml.Load("Styles.xaml"));
            Theme.Initialize(this);

            engine = new ReceiverEngine(Dispatcher, settings);
            viewModel = new MainViewModel(engine, settings);
            Log.Written += entry => Dispatcher.BeginInvoke(new Action(() => viewModel.AddLogEntry(entry)));
            tray = new TrayIcon(engine, ShowMainWindow, ExitApp);
            trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            trimTimer.Tick += (s, args) =>
            {
                trimTimer.Stop();
                if (window == null && !exiting) Native.TrimMemory();
            };

            engine.Changed += OnEngineChanged;
            engine.IdleChanged += idle =>
            {
                if (idle) TrimMemorySoon();
            };
            // Clicking the "connected" notification rebuilds the connection: the quickest way out of
            // "connected but no sound" when the automatic check can't tell.
            engine.DeviceConnected += session => Notify(
                session.DisplayName + " connected",
                "Its audio now plays on this PC. No sound? Press play, then click here.",
                () => session.FixCommand.Execute(null));
            engine.DeviceDisconnected += (session, willReconnect) => Notify(
                session.DisplayName + " disconnected",
                willReconnect ? "It will reconnect by itself when it's back in range." : "Press Connect when you want it back.",
                null);
            engine.Start();

            var listener = new Thread(ListenForShowRequests) { IsBackground = true, Name = "Show listener" };
            listener.Start();

            if (!startHidden) ShowMainWindow();
            else TrimMemorySoon();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);
            Cleanup();
        }

        void ListenForShowRequests()
        {
            try
            {
                while (showEvent.WaitOne()) Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            }
            catch
            {
            }
        }

        void ShowMainWindow()
        {
            if (exiting) return;
            if (window == null)
            {
                window = new MainWindow(viewModel, settings, ExitApp);
                window.Closed += OnWindowClosed;
            }
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
        }

        void OnWindowClosed(object sender, EventArgs e)
        {
            var closed = (MainWindow)sender;
            closed.Closed -= OnWindowClosed;
            if (window == closed) window = null;
            viewModel.IsSettingsOpen = false;
            viewModel.IsMoreOpen = false;
            if (!exiting && !settings.ToldAboutTray)
            {
                // Only once: people expect closing to quit, so say where it went.
                settings.ToldAboutTray = true;
                settings.Save();
                tray.ShowBalloon(AppInfo.Name + " is still running", "It waits here in the tray so your phone can connect any time. Right-click the icon to exit.", null);
            }
            TrimMemorySoon();
        }

        // Give WPF a moment to let go of the window before handing memory back.
        void TrimMemorySoon()
        {
            trimTimer.Stop();
            trimTimer.Start();
        }

        void OnEngineChanged()
        {
            tray.Update();
            bool streaming = engine.Sessions.Any(s => s.State == LinkState.Streaming);
            bool wantAwake = settings.KeepAwake && streaming;
            if (wantAwake == keepingAwake) return;
            keepingAwake = wantAwake;
            Native.SetThreadExecutionState(wantAwake ? Native.EsContinuous | Native.EsSystemRequired : Native.EsContinuous);
        }

        void Notify(string title, string text, Action onClick)
        {
            if (!settings.Notifications) return;
            if (window != null && window.IsVisible && window.IsActive) return;
            tray.ShowBalloon(title, text, onClick);
        }

        void ExitApp()
        {
            if (exiting) return;
            Log.Info("{0} closed", AppInfo.Name);
            Cleanup();
            if (window != null)
            {
                window.AllowClose = true;
                window.Close();
            }
            Shutdown();
        }

        void Cleanup()
        {
            if (exiting) return;
            exiting = true;
            engine.Shutdown();
            tray.Dispose();
            Native.SetThreadExecutionState(Native.EsContinuous);
        }
    }
}
