using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;

namespace BlueAux
{
    public sealed class MainViewModel : ObservableObject
    {
        const int MinIdleMinutes = 1;
        const int MaxIdleMinutes = 240;

        readonly ReceiverEngine engine;
        readonly AppSettings settings;
        readonly ListCollectionView devices;
        readonly ListCollectionView allDevices;
        readonly ObservableCollection<LogEntry> logEntries = new ObservableCollection<LogEntry>();
        bool settingsOpen;
        bool moreOpen;
        string heroState = "Loading";
        string heroTitle = "";
        string heroSubtitle = "";
        string heroGlyph = "";
        string heroAction;
        bool heroActionPrimary = true;
        bool showDeviceList;
        DeviceSession mainDevice;

        public MainViewModel(ReceiverEngine engine, AppSettings settings)
        {
            this.engine = engine;
            this.settings = settings;
            // The main page lists your other devices (the big button already covers the main one);
            // Settings lists all of them, hidden ones too.
            devices = new ListCollectionView(engine.Sessions);
            devices.Filter = o => !((DeviceSession)o).IsHidden && o != mainDevice;
            allDevices = new ListCollectionView(engine.Sessions);

            OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
            CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
            ToggleMoreCommand = new RelayCommand(() => IsMoreOpen = !IsMoreOpen);
            PairCommand = new RelayCommand(() => Shell.Open("ms-settings:bluetooth"));
            SoundSettingsCommand = new RelayCommand(() => Shell.Open("ms-settings:sound"));
            RestartCommand = new RelayCommand(engine.ResetAll);
            OpenLogCommand = new RelayCommand(() => Shell.Open(AppSettings.Folder));
            OpenRepoCommand = new RelayCommand(() => Shell.Open(AppInfo.RepoUrl));
            ReportProblemCommand = new RelayCommand(() => Shell.Open(AppInfo.RepoUrl + "/issues"));
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
            engine.VisibilityChanged += () =>
            {
                devices.Refresh();
                allDevices.Refresh();
            };
            Refresh();
        }

        public ICommand OpenSettingsCommand { get; private set; }
        public ICommand CloseSettingsCommand { get; private set; }
        public ICommand ToggleMoreCommand { get; private set; }
        public ICommand PairCommand { get; private set; }
        public ICommand SoundSettingsCommand { get; private set; }
        public ICommand RestartCommand { get; private set; }
        public ICommand OpenLogCommand { get; private set; }
        public ICommand OpenRepoCommand { get; private set; }
        public ICommand ReportProblemCommand { get; private set; }
        public ICommand EnableStartupCommand { get; private set; }
        public ICommand DismissStartupCommand { get; private set; }
        public ICommand HeroActionCommand { get; private set; }
        public ICommand FixAudioCommand { get; private set; }
        public ICommand IdleLessCommand { get; private set; }
        public ICommand IdleMoreCommand { get; private set; }

        public string AppName { get { return AppInfo.Name; } }
        public string AboutText { get { return AppInfo.Name + " " + AppInfo.Version + " · free and open source"; } }

        public ListCollectionView Devices { get { return devices; } }
        public ListCollectionView AllDevices { get { return allDevices; } }
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

        public bool IsMoreOpen
        {
            get { return moreOpen; }
            set { Set(ref moreOpen, value); }
        }

        public string HeroState { get { return heroState; } }
        public string HeroTitle { get { return heroTitle; } }
        public string HeroSubtitle { get { return heroSubtitle; } }
        public string HeroGlyph { get { return heroGlyph; } }
        public string HeroActionText { get { return heroAction; } }
        public bool ShowPrimaryAction { get { return heroAction != null && heroActionPrimary; } }
        public bool ShowSecondaryAction { get { return heroAction != null && !heroActionPrimary; } }
        public bool ShowFixAudio { get { return heroState == "Streaming"; } }
        public bool ShowConnectingHint { get { return heroState == "Connecting"; } }

        // With one phone, the big status at the top says it all; the list only helps with several.
        public bool ShowDeviceList { get { return showDeviceList; } }

        public bool ShowPcSilenced { get { return heroState == "Streaming" && engine.PcSilenced; } }

        public bool ShowStartupPrompt
        {
            get { return !settings.AskedAboutStartup && !AutoStart.IsEnabled && engine.Sessions.Count > 0; }
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
            switch (heroState)
            {
                case "Off":
                    if (engine.RadioStatus == RadioStatus.Missing) Shell.Open("ms-settings:bluetooth");
                    else engine.TurnOnBluetooth();
                    break;
                case "Empty":
                    Shell.Open("ms-settings:bluetooth");
                    break;
                case "Streaming":
                    if (engine.Active != null) engine.Active.Disconnect();
                    break;
                default:
                    if (mainDevice != null) mainDevice.Connect(true);
                    break;
            }
        }

        void Refresh()
        {
            var visible = engine.Sessions.Where(s => !s.IsHidden).ToList();
            var streaming = visible.Where(s => s.State == LinkState.Streaming).ToList();
            var connecting = visible.FirstOrDefault(s => s.State == LinkState.Connecting);
            var hiddenCount = engine.Sessions.Count - visible.Count;
            // The phone the big button acts on: the one playing or connecting, otherwise the one used last.
            var main = streaming.FirstOrDefault() ?? connecting ?? visible.OrderByDescending(s => s.LastConnected).FirstOrDefault();
            if (main != mainDevice)
            {
                mainDevice = main;
                devices.Refresh();
            }
            string name = mainDevice != null ? mainDevice.DisplayName : "your phone";

            string state, title, subtitle, glyph = "\uE702", action = null;
            bool primary = true;
            if (engine.RadioStatus == RadioStatus.Missing)
            {
                state = "Off";
                title = "No Bluetooth found";
                subtitle = "This PC's Bluetooth seems to be off or unplugged.";
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
                title = engine.EnumerationDone ? "Pair your phone first" : "Looking for your phone…";
                subtitle = engine.EnumerationDone
                    ? (hiddenCount > 0
                        ? "All your devices are hidden. Unhide one in Settings, or pair another phone."
                        : "Add your phone in Windows Bluetooth settings once. After that it shows up here.")
                    : "";
                glyph = "\uE8EA";
                if (engine.EnumerationDone) action = "Pair a phone";
            }
            else if (streaming.Count > 0)
            {
                state = "Streaming";
                title = "Playing from " + string.Join(" & ", streaming.Select(s => s.DisplayName));
                var since = streaming[0].StreamingSince;
                subtitle = "Connected for " + DeviceSession.FormatDuration(since.HasValue ? DateTime.Now - since.Value : TimeSpan.Zero);
                action = "Disconnect";
                primary = false;
            }
            else if (connecting != null)
            {
                state = "Connecting";
                title = "Connecting to " + connecting.DisplayName + "…";
                subtitle = connecting.IsSettling ? "Making sure the sound works."
                    : connecting.IsFixing ? "Keep the music playing on your phone."
                    : "Keep the phone nearby with Bluetooth on.";
            }
            else if (mainDevice != null && mainDevice.RecentlyFailedToConnect)
            {
                state = "Failed";
                title = "Couldn't connect to " + name;
                subtitle = mainDevice.LastError == "Not found nearby" || mainDevice.LastError == null
                    ? "Make sure it's nearby with Bluetooth on, then try again."
                    : mainDevice.LastError + ". Try again in a moment.";
                glyph = "\uE7BA";
                action = "Try again";
            }
            else if (engine.PcIdle)
            {
                state = "Resting";
                title = "Resting";
                subtitle = "Nobody is using this PC, so it stopped looking for " + name + ". It starts again when you're back.";
                glyph = "\uE708";
                action = "Connect";
            }
            else if (engine.AutoPaused)
            {
                state = "Paused";
                title = "Disconnected";
                subtitle = name + " won't reconnect by itself until you press Connect.";
                glyph = "\uE769";
                action = "Connect";
            }
            else if (visible.Any(s => s.State == LinkState.Waiting))
            {
                state = "Waiting";
                title = "Waiting for " + name;
                subtitle = "It connects by itself when it's nearby. You can also pick this PC in your phone's Bluetooth settings.";
                action = "Connect now";
                primary = false;
            }
            else
            {
                state = "Idle";
                title = "Ready to play";
                subtitle = "Press Connect, or pick this PC in your phone's Bluetooth settings.";
                glyph = "\uE767";
                action = "Connect";
            }

            if (Set(ref heroState, state, "HeroState"))
            {
                Raise("ShowFixAudio");
                Raise("ShowConnectingHint");
            }
            Set(ref heroTitle, title, "HeroTitle");
            Set(ref heroSubtitle, subtitle, "HeroSubtitle");
            Set(ref heroGlyph, glyph, "HeroGlyph");
            bool actionChanged = Set(ref heroAction, action, "HeroActionText");
            if (Set(ref heroActionPrimary, primary, "HeroActionPrimary") || actionChanged)
            {
                Raise("ShowPrimaryAction");
                Raise("ShowSecondaryAction");
            }
            Set(ref showDeviceList, visible.Count > 1, "ShowDeviceList");
            Raise("ShowPcSilenced");
            Raise("ShowStartupPrompt");
        }
    }
}
