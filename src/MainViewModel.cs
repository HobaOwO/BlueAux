using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace BlueAux
{
    public sealed class MainViewModel : ObservableObject
    {
        const int MinIdleMinutes = 1;
        const int MaxIdleMinutes = 240;

        readonly ReceiverEngine engine;
        readonly CoPlaySession coPlay;
        readonly Updater updater;
        readonly ObservableCollection<CoPlayHostOption> coPlayHosts = new ObservableCollection<CoPlayHostOption>();
        readonly AppSettings settings;
        readonly ListCollectionView devices;
        readonly ListCollectionView allDevices;
        readonly ObservableCollection<LogEntry> logEntries = new ObservableCollection<LogEntry>();
        bool settingsOpen;
        bool moreOpen;
        string heroState = "Loading";
        string heroTitle = "";
        string heroSubtitle = "";
        string heroDevice = "";
        string heroGlyph = "";
        string heroAction;
        bool heroActionPrimary = true;
        bool showDeviceList;
        DeviceSession mainDevice;
        bool showSongSetup;
        string songSetupText;
        string songSetupAction;
        DeviceSession songSetupPhone;
        SetupStep setupStep;
        readonly ThemeOption[] themes;
        readonly AlbumArt art;
        bool heroSong;
        AlbumArt.Art heroArt;

        public MainViewModel(ReceiverEngine engine, AppSettings settings, CoPlaySession coPlay, Updater updater)
        {
            this.engine = engine;
            this.coPlay = coPlay;
            this.settings = settings;
            themes = Theme.All.Select(name => new ThemeOption(name)).ToArray();
            art = new AlbumArt(System.Windows.Application.Current.Dispatcher);
            art.Changed += Refresh;
            Theme.Changed += () =>
            {
                foreach (var theme in themes) theme.Refresh();
            };
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
            this.updater = updater;
            CheckUpdateCommand = new RelayCommand(updater.CheckNow);
            updater.StatusChanged += () => Raise("UpdateStatus");
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
            SongSetupCommand = new RelayCommand(() =>
            {
                if (songSetupPhone != null) engine.SetUpSongs(songSetupPhone);
            });
            // "Not now": stops offering it on the main page for this phone. Settings still has it.
            SongSetupDismissCommand = new RelayCommand(() =>
            {
                var phone = songSetupPhone;
                engine.Setup.Reset();
                if (phone != null) phone.SkipSongSetup();
                Refresh();
            });
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

            CoPlayHostCommand = new RelayCommand(coPlay.StartHost);
            CoPlayJoinCommand = new RelayCommand(coPlay.StartSearch);
            CoPlayStopCommand = new RelayCommand(coPlay.Stop);
            CoPlayAdjustLessCommand = new RelayCommand(() => SetCoPlayAdjust(coPlay.AdjustMs - 10));
            CoPlayAdjustMoreCommand = new RelayCommand(() => SetCoPlayAdjust(coPlay.AdjustMs + 10));
            coPlay.Changed += RefreshCoPlay;

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
        public ICommand CheckUpdateCommand { get; private set; }
        public string UpdateStatus { get { return updater.Status; } }
        public ICommand OpenLogCommand { get; private set; }
        public ICommand OpenRepoCommand { get; private set; }
        public ICommand ReportProblemCommand { get; private set; }
        public ICommand EnableStartupCommand { get; private set; }
        public ICommand DismissStartupCommand { get; private set; }
        public ICommand HeroActionCommand { get; private set; }
        public ICommand FixAudioCommand { get; private set; }
        public ICommand SongSetupCommand { get; private set; }
        public ICommand SongSetupDismissCommand { get; private set; }

        public bool ShowSongSetup { get { return showSongSetup; } }
        public string SongSetupText { get { return songSetupText; } }
        public string SongSetupAction { get { return songSetupAction; } }
        public bool ShowSongSetupAction { get { return songSetupAction != null; } }
        public bool ShowSongSetupDismiss { get { return !engine.Setup.IsBusy; } }
        public ICommand CoPlayHostCommand { get; private set; }
        public ICommand CoPlayJoinCommand { get; private set; }
        public ICommand CoPlayStopCommand { get; private set; }
        public ICommand CoPlayAdjustLessCommand { get; private set; }
        public ICommand CoPlayAdjustMoreCommand { get; private set; }
        public ICommand IdleLessCommand { get; private set; }
        public ICommand IdleMoreCommand { get; private set; }

        public ThemeOption[] Themes { get { return themes; } }

        public bool Animations
        {
            get { return settings.Animations; }
            set { settings.Animations = value; settings.Save(); Raise(); Motion.Refresh(); }
        }

        public bool AlbumArt
        {
            get { return settings.AlbumArt; }
            set { settings.AlbumArt = value; settings.Save(); Raise(); Refresh(); }
        }

        public bool CoPlay
        {
            get { return settings.CoPlay; }
            set
            {
                settings.CoPlay = value;
                settings.Save();
                if (!value) coPlay.Stop();
                Raise();
            }
        }

        public bool AutoUpdate
        {
            get { return settings.AutoUpdate; }
            set { settings.AutoUpdate = value; settings.Save(); Raise(); }
        }

        public bool AlwaysOnTop
        {
            get { return settings.AlwaysOnTop; }
            set { settings.AlwaysOnTop = value; settings.Save(); Raise(); }
        }

        public bool MonoTrayIcon
        {
            get { return settings.MonoTrayIcon; }
            set { settings.MonoTrayIcon = value; settings.Save(); Raise(); engine.NotifyChanged(); }
        }

        public string AppName { get { return AppInfo.Name; } }
        public string AboutText { get { return AppInfo.Name + " " + AppInfo.Version; } }

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
        // The phone a song is playing from (empty when the title already is the phone).
        public string HeroDevice { get { return heroDevice; } }
        public string HeroGlyph { get { return heroGlyph; } }
        // A song is showing (the title is a song, not a phone), so the card shows art and a record.
        public bool HeroSong { get { return heroSong; } }
        public ImageSource HeroArt { get { return heroArt == null ? null : heroArt.Image; } }
        public ImageSource HeroAmbient { get { return heroArt == null ? null : heroArt.Ambient; } }
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

        public bool DisconnectWhenAsleep
        {
            get { return settings.DisconnectWhenAsleep; }
            set { settings.DisconnectWhenAsleep = value; settings.Save(); Raise(); }
        }

        public bool DisconnectWhenLocked
        {
            get { return settings.DisconnectWhenLocked; }
            set { settings.DisconnectWhenLocked = value; settings.Save(); Raise(); }
        }

        // CoPlay card: Host / Join when off; the PCs playing along, or the hosts found; the Sync row.
        public bool IsCoPlayOff { get { return coPlay.Mode == CoPlaySession.Modes.Off; } }
        public bool IsCoPlayOn { get { return !IsCoPlayOff; } }
        public bool IsCoPlaySearching { get { return coPlay.Mode == CoPlaySession.Modes.Searching; } }
        public bool ShowCoPlaySync { get { return coPlay.Mode == CoPlaySession.Modes.Hosting || coPlay.Mode == CoPlaySession.Modes.Joined; } }
        public ObservableCollection<CoPlayHostOption> CoPlayHosts { get { return coPlayHosts; } }

        public string CoPlayStatus
        {
            get
            {
                switch (coPlay.Mode)
                {
                    case CoPlaySession.Modes.Hosting:
                        string[] guests = coPlay.GuestNames;
                        if (guests.Length == 0)
                            return "Hosting. On the other PCs, open BlueAux and press Join. If they can't find this one, allow BlueAux through Windows Firewall here (private networks).";
                        return "Playing on " + (guests.Length == 1 ? "1 other PC: " : guests.Length + " other PCs: ") + string.Join(", ", guests)
                            + (coPlay.PhoneFound ? "" : ". Connect your phone to this PC and play something.");
                    case CoPlaySession.Modes.Searching:
                        return coPlay.Hosts.Count == 0 ? "Looking for a PC that's hosting, on this Wi-Fi or network\u2026" : "Pick the PC to play along with";
                    case CoPlaySession.Modes.Joined:
                        if (!coPlay.HostAnswering)
                            return "No answer from " + coPlay.HostName + ". On that PC, allow BlueAux through Windows Firewall (private networks).";
                        return coPlay.Receiving ? "Playing along with " + coPlay.HostName : "Connected to " + coPlay.HostName + ". Waiting for music.";
                    default:
                        return coPlay.Problem ?? "Play your phone on other PCs at the same moment";
                }
            }
        }

        // Typed into the number box, in ms; later is positive.
        public string CoPlayAdjustText
        {
            get { return coPlay.AdjustMs.ToString(); }
            set
            {
                int ms;
                if (int.TryParse((value ?? "").Trim(), out ms)) SetCoPlayAdjust(ms);
                else Raise();
            }
        }

        void SetCoPlayAdjust(int ms)
        {
            coPlay.AdjustMs = ms;
            Raise("CoPlayAdjustText");
        }

        void RefreshCoPlay()
        {
            var found = coPlay.Mode == CoPlaySession.Modes.Searching ? coPlay.Hosts : new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Net.IPEndPoint>>();
            if (!found.Select(h => h.Key).SequenceEqual(coPlayHosts.Select(h => h.Name)))
            {
                coPlayHosts.Clear();
                foreach (var each in found)
                {
                    var host = each;
                    coPlayHosts.Add(new CoPlayHostOption(host.Key, new RelayCommand(() => coPlay.Join(host.Key, host.Value))));
                }
            }
            Raise("IsCoPlayOff");
            Raise("IsCoPlayOn");
            Raise("IsCoPlaySearching");
            Raise("ShowCoPlaySync");
            Raise("CoPlayStatus");
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

        // The song display offer under the now-playing card: for an iPhone missing its LE pairing, and
        // while PhoneSetup works on it (it shows its progress there, wherever it was started from).
        void RefreshSongSetup(System.Collections.Generic.List<DeviceSession> streaming)
        {
            var setup = engine.Setup;
            bool busy = setup.IsBusy, failed = setup.Step == SetupStep.Failed;
            if (busy && setupStep != SetupStep.Looking && setup.Step == SetupStep.Looking) IsSettingsOpen = false;
            setupStep = setup.Step;

            var offer = streaming.FirstOrDefault(s => s.OfferSongSetup);
            songSetupPhone = busy || failed ? setup.Phone : offer;
            Set(ref showSongSetup, (busy || failed || offer != null) && songSetupPhone != null, "ShowSongSetup");
            Set(ref songSetupText, busy || failed ? setup.Message : "Show what's playing", "SongSetupText");
            Set(ref songSetupAction, busy ? null : failed ? "Try again" : "Set up", "SongSetupAction");
            Raise("ShowSongSetupAction");
            Raise("ShowSongSetupDismiss");
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

            // The card at the top: one line for what it is (the song, or the phone), one for how it's doing,
            // and the phone's name under a song. Longer explanations live in tooltips.
            string state, title = name, subtitle = "", device = "", glyph = "", action = null;
            bool primary = true;
            bool isSong = false;
            AlbumArt.Art cover = null;
            if (engine.RadioStatus == RadioStatus.Missing)
            {
                state = "Off";
                title = "No Bluetooth found";
                subtitle = "It may be off or unplugged";
                action = "Bluetooth settings";
            }
            else if (!engine.RadioOn)
            {
                state = "Off";
                title = "Bluetooth is off";
                action = "Turn on";
            }
            else if (visible.Count == 0)
            {
                state = engine.EnumerationDone ? "Empty" : "Loading";
                title = !engine.EnumerationDone ? "Looking for phones…" : hiddenCount > 0 ? "All devices are hidden" : "No phone paired yet";
                if (engine.EnumerationDone) subtitle = hiddenCount > 0 ? "Unhide one in Settings" : "Pair it in Windows, then it shows up here";
                glyph = "";
                if (engine.EnumerationDone) action = "Pair a phone";
            }
            else if (streaming.Count > 0)
            {
                state = "Streaming";
                string phones = string.Join(" & ", streaming.Select(s => s.DisplayName));
                // An iPhone tells us the song while it plays. Only while playing: apps like TikTok don't
                // report themselves, so "paused" can mean the last music app is paused while another plays.
                string song = engine.Media.PlaybackOf(streaming[0]) == PhonePlayback.Playing ? engine.Media.TitleOf(streaming[0]) : null;
                if (song != null)
                {
                    title = song;
                    subtitle = engine.Media.ArtistOf(streaming[0]) ?? "";
                    device = phones;
                    isSong = true;
                    if (settings.AlbumArt) cover = art.Lookup(song, subtitle);
                }
                else
                {
                    title = phones;
                    subtitle = "Connected";
                }
                action = "Disconnect";
                primary = false;
            }
            else if (connecting != null)
            {
                state = "Connecting";
                title = connecting.DisplayName;
                subtitle = connecting.IsSettling ? "Making sure the sound works…"
                    : connecting.IsFixing ? "Reconnecting, keep it playing…"
                    : "Connecting…";
            }
            else if (mainDevice != null && mainDevice.RecentlyFailedToConnect)
            {
                state = "Failed";
                subtitle = mainDevice.LastError == "Not found nearby" || mainDevice.LastError == null
                    ? "Couldn't connect. Is Bluetooth on?"
                    : mainDevice.LastError;
                glyph = "";
                action = "Try again";
            }
            else if (engine.PcIdle)
            {
                state = "Resting";
                subtitle = "Resting while you're away";
                glyph = "";
                action = "Connect";
            }
            else if (engine.AutoPaused)
            {
                state = "Paused";
                subtitle = "Disconnected";
                glyph = "";
                action = "Connect";
            }
            else if (visible.Any(s => s.State == LinkState.Waiting))
            {
                state = "Waiting";
                subtitle = "Connects when it's nearby";
                action = "Connect";
                primary = false;
            }
            else
            {
                state = "Idle";
                subtitle = "Not connected";
                glyph = "";
                action = "Connect";
            }

            if (Set(ref heroState, state, "HeroState"))
            {
                Raise("ShowFixAudio");
                Raise("ShowConnectingHint");
            }
            Set(ref heroTitle, title, "HeroTitle");
            Set(ref heroSubtitle, subtitle, "HeroSubtitle");
            Set(ref heroDevice, device, "HeroDevice");
            Set(ref heroGlyph, glyph, "HeroGlyph");
            Set(ref heroSong, isSong, "HeroSong");
            if (heroArt != cover)
            {
                heroArt = cover;
                Raise("HeroArt");
                Raise("HeroAmbient");
            }
            bool actionChanged = Set(ref heroAction, action, "HeroActionText");
            if (Set(ref heroActionPrimary, primary, "HeroActionPrimary") || actionChanged)
            {
                Raise("ShowPrimaryAction");
                Raise("ShowSecondaryAction");
            }
            RefreshSongSetup(streaming);
            Set(ref showDeviceList, visible.Count > 1, "ShowDeviceList");
            Raise("ShowPcSilenced");
            Raise("ShowStartupPrompt");
        }
    }

    // A theme in the Settings swatch row.
    public sealed class CoPlayHostOption
    {
        public CoPlayHostOption(string name, ICommand join)
        {
            Name = name;
            JoinCommand = join;
        }

        public string Name { get; private set; }
        public ICommand JoinCommand { get; private set; }
    }

    public sealed class ThemeOption : ObservableObject
    {
        readonly string name;

        public ThemeOption(string name)
        {
            this.name = name;
            SelectCommand = new RelayCommand(() => Theme.Select(name));
            Refresh();
        }

        public string Name { get { return Theme.DisplayName(name); } }
        public ICommand SelectCommand { get; private set; }
        public bool IsSelected { get { return Theme.Current == name; } }
        public Brush Window { get; private set; }
        public Brush Card { get; private set; }
        public Brush Ink { get; private set; }

        public void Refresh()
        {
            Color window, card, text;
            Theme.Preview(name, out window, out card, out text);
            Window = Frozen(window);
            Card = Frozen(card);
            Ink = Frozen(text);
            Raise("Window");
            Raise("Card");
            Raise("Ink");
            Raise("IsSelected");
        }

        static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
