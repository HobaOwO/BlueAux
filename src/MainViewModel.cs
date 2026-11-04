using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;

namespace PhoneSpeaker
{
    public sealed class MainViewModel : ObservableObject
    {
        const int MinIdleMinutes = 1;
        const int MaxIdleMinutes = 240;

        readonly ReceiverEngine engine;
        readonly AppSettings settings;
        readonly ListCollectionView devices;
        readonly ObservableCollection<LogEntry> logEntries = new ObservableCollection<LogEntry>();
        bool settingsOpen;
        string heroState = "Loading";
        string heroTitle = "";
        string heroSubtitle = "";
        string heroGlyph = "";
        string heroAction;
        string deviceCountText = "";

        public MainViewModel(ReceiverEngine engine, AppSettings settings)
        {
            this.engine = engine;
            this.settings = settings;
            devices = new ListCollectionView(engine.Sessions);
            devices.Filter = o => settings.ShowHidden || !((DeviceSession)o).IsHidden;

            OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
            CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
            PairCommand = new RelayCommand(() => Shell.Open("ms-settings:bluetooth"));
            SoundSettingsCommand = new RelayCommand(() => Shell.Open("ms-settings:sound"));
            RestartCommand = new RelayCommand(engine.ResetAll);
            OpenLogCommand = new RelayCommand(() => Shell.Open(Log.FilePath));
            EnableStartupCommand = new RelayCommand(() =>
            {
                StartWithWindows = true;
                AnswerStartupPrompt();
            });
            DismissStartupCommand = new RelayCommand(AnswerStartupPrompt);
            HeroActionCommand = new RelayCommand(RunHeroAction);
            FixAudioCommand = new RelayCommand(() =>
            {
                if (engine.Active != null) engine.Active.FixCommand.Execute(null);
            });
            // Small steps for short times, bigger ones after 5 minutes.
            IdleLessCommand = new RelayCommand(() =>
            {
                int m = settings.IdleMinutes;
                SetIdleMinutes(m <= 5 ? m - 1 : (m - 1) / 5 * 5);
            });
            IdleMoreCommand = new RelayCommand(() =>
            {
                int m = settings.IdleMinutes;
                SetIdleMinutes(m < 5 ? m + 1 : (m / 5 + 1) * 5);
            });

            engine.Changed += Refresh;
            engine.Ticked += Refresh;
            engine.VisibilityChanged += () => devices.Refresh();
            Refresh();
        }

        public ICommand OpenSettingsCommand { get; private set; }
        public ICommand CloseSettingsCommand { get; private set; }
        public ICommand PairCommand { get; private set; }
        public ICommand SoundSettingsCommand { get; private set; }
        public ICommand RestartCommand { get; private set; }
        public ICommand OpenLogCommand { get; private set; }
        public ICommand EnableStartupCommand { get; private set; }
        public ICommand DismissStartupCommand { get; private set; }
        public ICommand HeroActionCommand { get; private set; }
        public ICommand FixAudioCommand { get; private set; }
        public ICommand IdleLessCommand { get; private set; }
        public ICommand IdleMoreCommand { get; private set; }

        public ListCollectionView Devices { get { return devices; } }
        public ObservableCollection<LogEntry> LogEntries { get { return logEntries; } }

        public bool IsSettingsOpen
        {
            get { return settingsOpen; }
            set
            {
                if (Set(ref settingsOpen, value)) Raise("IsMainOpen");
            }
        }

        public bool IsMainOpen { get { return !settingsOpen; } }

        public string HeroState { get { return heroState; } }
        public string HeroTitle { get { return heroTitle; } }
        public string HeroSubtitle { get { return heroSubtitle; } }
        public string HeroGlyph { get { return heroGlyph; } }
        public string HeroActionText { get { return heroAction; } }
        public bool HasHeroAction { get { return heroAction != null; } }
        public string DeviceCountText { get { return deviceCountText; } }
        public bool HasDevices { get { return engine.Sessions.Count > 0; } }

        public bool ShowStartupPrompt
        {
            get { return !settings.AskedAboutStartup && !AutoStart.IsEnabled; }
        }

        public bool StartWithWindows
        {
            get { return AutoStart.IsEnabled; }
            set
            {
                AutoStart.Set(value);
                Raise();
                Raise("ShowStartupPrompt");
            }
        }

        public bool CloseToTray
        {
            get { return settings.CloseToTray; }
            set { settings.CloseToTray = value; settings.Save(); Raise(); }
        }

        public bool KeepAwake
        {
            get { return settings.KeepAwake; }
            set { settings.KeepAwake = value; settings.Save(); Raise(); engine.NotifyChanged(); }
        }

        public bool Notifications
        {
            get { return settings.Notifications; }
            set { settings.Notifications = value; settings.Save(); Raise(); }
        }

        public bool ShowHidden
        {
            get { return settings.ShowHidden; }
            set { settings.ShowHidden = value; settings.Save(); Raise(); devices.Refresh(); Refresh(); }
        }

        public bool RestWhenIdle
        {
            get { return settings.RestWhenIdle; }
            set { settings.RestWhenIdle = value; settings.Save(); Raise(); }
        }

        // Typed into the number box. Anything that isn't a number in range snaps back to the current value.
        public string IdleMinutesText
        {
            get { return settings.IdleMinutes.ToString(); }
            set
            {
                int minutes;
                if (int.TryParse((value ?? "").Trim(), out minutes)) SetIdleMinutes(minutes);
                else Raise();
            }
        }

        public bool DisconnectWhenIdle
        {
            get { return settings.DisconnectWhenIdle; }
            set { settings.DisconnectWhenIdle = value; settings.Save(); Raise(); }
        }

        public bool ShowFixAudio { get { return heroState == "Streaming"; } }

        public string LogPath { get { return Log.FilePath; } }

        void SetIdleMinutes(int minutes)
        {
            settings.IdleMinutes = Math.Max(MinIdleMinutes, Math.Min(MaxIdleMinutes, minutes));
            settings.Save();
            Raise("IdleMinutesText");
        }

        public void AddLogEntry(LogEntry entry)
        {
            logEntries.Insert(0, entry);
            while (logEntries.Count > 40) logEntries.RemoveAt(logEntries.Count - 1);
        }

        void AnswerStartupPrompt()
        {
            settings.AskedAboutStartup = true;
            settings.Save();
            Raise("ShowStartupPrompt");
        }

        void RunHeroAction()
        {
            if (heroState == "Off") engine.TurnOnBluetooth();
            else if (heroState == "Paused") engine.SetAutoPaused(false);
            else Shell.Open("ms-settings:bluetooth");
        }

        void Refresh()
        {
            var visible = engine.Sessions.Where(s => !s.IsHidden).ToList();
            var streaming = visible.Where(s => s.State == LinkState.Streaming).ToList();
            var connecting = visible.FirstOrDefault(s => s.State == LinkState.Connecting);
            var hiddenCount = engine.Sessions.Count - visible.Count;

            string state, title, subtitle, glyph = "", action = null;
            if (engine.RadioStatus == RadioStatus.Missing)
            {
                state = "Off";
                title = "No Bluetooth found";
                subtitle = "This PC's Bluetooth adapter seems to be off or unplugged.";
                action = "Open Bluetooth settings";
            }
            else if (!engine.RadioOn)
            {
                state = "Off";
                title = "Bluetooth is off";
                subtitle = "Turn it on so your phone can play through this PC.";
                action = "Turn on Bluetooth";
            }
            else if (visible.Count == 0)
            {
                state = engine.EnumerationDone ? "Empty" : "Loading";
                title = engine.EnumerationDone ? "Pair your phone first" : "Looking for devices…";
                subtitle = engine.EnumerationDone
                    ? (hiddenCount > 0 ? "All your devices are hidden. Turn on \"Show hidden devices\" in settings, or pair another phone." : "In Bluetooth settings, add your phone to this PC once. After that it shows up here.")
                    : "";
                glyph = "";
                if (engine.EnumerationDone) action = "Pair a phone";
            }
            else if (streaming.Count > 0)
            {
                state = "Streaming";
                title = "Playing from " + string.Join(" & ", streaming.Select(s => s.DisplayName));
                var since = streaming[0].StreamingSince;
                subtitle = "Connected for " + DeviceSession.FormatDuration(since.HasValue ? DateTime.Now - since.Value : TimeSpan.Zero);
            }
            else if (connecting != null)
            {
                state = "Connecting";
                title = "Connecting to " + connecting.DisplayName + "…";
                subtitle = "Keep the phone nearby with Bluetooth on.";
            }
            else if (engine.PcIdle)
            {
                state = "Resting";
                title = "Resting";
                subtitle = "Nobody has used this PC for a while, so Phone Speaker disconnected and stopped looking for your phone. It starts again as soon as you're back.";
                glyph = "";
            }
            else if (engine.AutoPaused)
            {
                state = "Paused";
                title = "Paused";
                subtitle = "You disconnected, so nothing reconnects by itself until you press Connect.";
                glyph = "";
                action = "Resume auto-connect";
            }
            else if (visible.Any(s => s.State == LinkState.Waiting))
            {
                state = "Waiting";
                title = "Waiting for your phone";
                subtitle = "It connects by itself when it's nearby. You can also pick this PC in your phone's Bluetooth menu.";
            }
            else
            {
                state = "Idle";
                title = "Ready to play";
                subtitle = "Press Connect below, or pick this PC in your phone's Bluetooth menu.";
                glyph = "";
            }

            if (Set(ref heroState, state, "HeroState")) Raise("ShowFixAudio");
            Set(ref heroTitle, title, "HeroTitle");
            Set(ref heroSubtitle, subtitle, "HeroSubtitle");
            Set(ref heroGlyph, glyph, "HeroGlyph");
            if (Set(ref heroAction, action, "HeroActionText")) Raise("HasHeroAction");

            string count = hiddenCount > 0 && !settings.ShowHidden ? string.Format("{0} hidden", hiddenCount) : "";
            Set(ref deviceCountText, count, "DeviceCountText");
            Raise("HasDevices");
        }
    }
}
