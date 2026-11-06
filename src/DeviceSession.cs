using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Devices.Bluetooth;
using Windows.Foundation;
using Windows.Media.Audio;

namespace BlueAux
{
    public enum LinkState { Off, Idle, Waiting, Paused, Connecting, Streaming }

    // One paired phone/tablet. Owns the Windows AudioPlaybackConnection for it and keeps it healthy:
    //  - while it is the engine's "listener", its connection stays started, so the phone can pick this PC
    //    from its own Bluetooth menu;
    //  - when auto-connect is on, we reach out to the phone ourselves (like a car stereo does);
    //  - whenever the link drops or an attempt fails, the connection object is thrown away and a fresh one
    //    is made. Re-opening an old object is what leaves you "connected" with no sound.
    // Windows keeps one shared Opened/Closed state for every connection object in the process, so the
    // engine makes sure only one session holds a connection at a time (see ReceiverEngine.TakeSlot).
    // Everything here runs on the UI thread; Windows callbacks are posted back to it.
    public sealed class DeviceSession : ObservableObject
    {
        static readonly int[] RetrySeconds = { 3, 5, 8, 12, 16, 20 };

        readonly ReceiverEngine engine;
        readonly DeviceSettings settings;
        readonly string deviceId;
        readonly string address;
        readonly string interfaceName;
        readonly TypedEventHandler<AudioPlaybackConnection, object> stateHandler;

        AudioPlaybackConnection connection;
        AudioPlaybackConnection startingConnection;
        AudioPlaybackConnection openingConnection;
        BluetoothDevice bluetoothDevice;
        string bluetoothName;
        bool isTablet;
        bool isOpen;
        bool starting;
        bool attempting;
        bool manualAttempt;
        bool paused;
        bool fixing;
        bool settling;
        bool openedByRebuild;
        DateTime openedAt = DateTime.MinValue;
        bool expanded;
        bool shutDown;
        int failures;
        string lastError;
        DateTime nextAttempt;
        DateTime nextStart;
        DateTime lostAt = DateTime.MinValue;
        DateTime manualFailedAt = DateTime.MinValue;
        DateTime? streamingSince;
        LinkState state = LinkState.Idle;
        string statusText = "";

        public DeviceSession(ReceiverEngine engine, string deviceId, string address, string interfaceName, DeviceSettings settings)
        {
            this.engine = engine;
            this.deviceId = deviceId;
            this.address = address;
            this.interfaceName = interfaceName;
            this.settings = settings;
            stateHandler = OnConnectionStateChanged;
            // Give the Bluetooth lookup a moment to decide the auto-connect default before the first attempt.
            nextAttempt = DateTime.UtcNow.AddSeconds(2);

            ConnectCommand = new RelayCommand(() => Connect(true));
            DisconnectCommand = new RelayCommand(Disconnect);
            FixCommand = new RelayCommand(FixAudio);
            HideCommand = new RelayCommand(ToggleHidden);
            ExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
            UpdateState();
        }

        public ICommand ConnectCommand { get; private set; }
        public ICommand DisconnectCommand { get; private set; }
        public ICommand FixCommand { get; private set; }
        public ICommand HideCommand { get; private set; }
        public ICommand ExpandCommand { get; private set; }

        public string DeviceId { get { return deviceId; } }
        public DateTime NextAttempt { get { return nextAttempt; } }
        public bool IsAttempting { get { return attempting; } }
        public bool IsOpen { get { return isOpen; } }
        public DateTime LastConnected { get { return settings.LastConnected; } }

        // Can stand by for the phone to connect by itself (the user hasn't hidden or disconnected it).
        public bool CanListen { get { return !shutDown && !settings.Hidden && !paused; } }

        public LinkState State { get { return state; } }
        public string StateName { get { return state.ToString(); } }
        public string StatusText { get { return statusText; } }
        public DateTime? StreamingSince { get { return streamingSince; } }
        public bool ShowConnect { get { return state == LinkState.Idle || state == LinkState.Waiting || state == LinkState.Paused; } }
        public bool ShowConnecting { get { return state == LinkState.Connecting; } }
        public bool ShowDisconnect { get { return state == LinkState.Streaming; } }
        public string Glyph { get { return isTablet ? "" : ""; } }
        public bool IsHidden { get { return settings.Hidden; } }
        public bool IsFixing { get { return fixing; } }
        public bool IsSettling { get { return fixing && settling; } }

        // The current connection is one we opened ourselves (not the phone, not a rebuild),
        // so SoundWatch should reconnect once if it stays silent (see Settle).
        public bool ShouldSettle { get; private set; }
        public string HideText { get { return settings.Hidden ? "Unhide" : "Hide"; } }

        // One line under the device's name in Settings.
        public string SettingsHint
        {
            get
            {
                if (settings.Hidden) return "Hidden: never connects and isn't listed";
                return settings.AutoConnect ? "Connects by itself when it's nearby" : "Connects only when you press Connect";
            }
        }
        public string HideGlyph { get { return settings.Hidden ? "" : ""; } }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(settings.Nickname)) return settings.Nickname;
                if (!string.IsNullOrWhiteSpace(bluetoothName)) return bluetoothName;
                return interfaceName;
            }
        }

        public string EditableName
        {
            get { return DisplayName; }
            set
            {
                string name = (value ?? "").Trim();
                string original = string.IsNullOrWhiteSpace(bluetoothName) ? interfaceName : bluetoothName;
                settings.Nickname = name.Length == 0 || name == original ? null : name;
                engine.SaveSettings();
                RaiseNames();
            }
        }

        public string AddressText
        {
            get
            {
                if (address == null || address.Length != 12) return "";
                var parts = new string[6];
                for (int i = 0; i < 6; i++) parts[i] = address.Substring(i * 2, 2);
                return "Bluetooth address " + string.Join(":", parts).ToUpperInvariant();
            }
        }

        public bool AutoConnect
        {
            get { return settings.AutoConnect; }
            set
            {
                if (settings.AutoConnect == value) return;
                settings.AutoConnect = value;
                engine.SaveSettings();
                Log.Info("{0}: auto-connect {1}", DisplayName, value ? "on" : "off");
                if (value)
                {
                    failures = 0;
                    nextAttempt = DateTime.UtcNow;
                }
                Raise();
                Raise("SettingsHint");
                UpdateState();
            }
        }

        public bool IsExpanded
        {
            get { return expanded; }
            set { Set(ref expanded, value); }
        }

        public void Initialize()
        {
            LookUpBluetoothDevice();
        }

        public void Tick(DateTime now, bool isListener)
        {
            if (shutDown) return;
            // The listener keeps a started connection so the phone can connect to us by itself;
            // everyone else lets go of theirs.
            if (!isListener) ReleaseIdleConnection();
            else if (engine.RadioOn && connection == null && !starting && !attempting && now >= nextStart) StartInBackground();
            if (isOpen) UpdateStatusText();
        }

        public void ReleaseIdleConnection()
        {
            if (connection != null && !isOpen && !attempting && !starting) CloseConnection();
        }

        public bool WantsAutoAttempt(DateTime now)
        {
            return !shutDown && engine.RadioOn && settings.AutoConnect && !settings.Hidden && !paused
                && !isOpen && !attempting && now >= nextAttempt;
        }

        public void Connect(bool manual)
        {
            if (shutDown) return;
            if (manual)
            {
                paused = false;
                engine.SetAutoPaused(false);
                if (attempting)
                {
                    // An automatic attempt is already under way; just show it as the user's.
                    manualAttempt = true;
                    UpdateState();
                    return;
                }
            }
            if (attempting || isOpen || !engine.RadioOn)
            {
                UpdateState();
                return;
            }
            var playing = engine.Active;
            if (manual && playing != null && playing != this)
            {
                // Like a car stereo: picking another phone takes over from the one that's playing.
                Log.Info("Switching from {0} to {1}", playing.DisplayName, DisplayName);
                playing.SwitchAway();
            }
            AttemptAsync(manual);
        }

        // Another device was picked: let go of the audio, but don't count it as "paused".
        public void SwitchAway()
        {
            CloseConnection();
            nextStart = DateTime.UtcNow.AddSeconds(1.5);
            UpdateState();
        }

        public void Disconnect()
        {
            Log.Info("{0}: disconnected by you", DisplayName);
            paused = true;
            CloseConnection();
            engine.SetAutoPaused(true);
            UpdateState();
        }

        public void Refresh()
        {
            UpdateState();
        }

        // The PC went idle: let go of the phone without counting it as "disconnected by you".
        public void Rest()
        {
            Log.Info("{0}: disconnected because the PC is idle", DisplayName);
            CloseConnection();
            UpdateState();
        }

        // Someone is using the PC again: try right away instead of waiting out the retry delay.
        public void WakeUp()
        {
            failures = 0;
            nextStart = DateTime.UtcNow;
            nextAttempt = DateTime.UtcNow;
            UpdateState();
        }

        // Throw away the connection and build it again from scratch.
        public void Reset(TimeSpan delay)
        {
            CloseConnection();
            failures = 0;
            nextStart = DateTime.UtcNow + delay;
            nextAttempt = nextStart.AddSeconds(1);
            UpdateState();
        }

        public void OnRadioChanged(bool on)
        {
            if (on) Reset(TimeSpan.FromSeconds(2));
            else
            {
                CloseConnection();
                UpdateState();
            }
        }

        public void Shutdown()
        {
            shutDown = true;
            CloseConnection();
            if (bluetoothDevice != null)
            {
                try { bluetoothDevice.Dispose(); }
                catch { }
                bluetoothDevice = null;
            }
        }

        // Rebuild the connection from scratch: what gets a "connected but silent" phone going again
        // (see SoundWatch, which also does it automatically).
        void FixAudio()
        {
            Rebuild(false);
        }

        // The first connection we make to a phone that just came back (in range again, Bluetooth back on)
        // can come up connected but silent, while one made right after it works. SoundWatch calls this
        // once, a few seconds after such a connection, if no sound has come through yet.
        public void Settle()
        {
            Rebuild(true);
        }

        async void Rebuild(bool settle)
        {
            if (fixing) return;
            Log.Info(settle ? "{0}: no sound yet, so reconnecting once to make sure it works" : "{0}: rebuilding the audio connection", DisplayName);
            fixing = true;
            settling = settle;
            paused = false;
            // Hold off the background restart while we rebuild it ourselves.
            Reset(TimeSpan.FromSeconds(30));
            await Task.Delay(1500);
            nextStart = DateTime.UtcNow;
            Connect(true);
            if (!attempting)
            {
                fixing = false;
                settling = false;
                UpdateState();
            }
        }

        void ToggleHidden()
        {
            settings.Hidden = !settings.Hidden;
            if (settings.Hidden)
            {
                CloseConnection();
                IsExpanded = false;
            }
            else nextStart = DateTime.UtcNow;
            engine.SaveSettings();
            Raise("IsHidden");
            Raise("HideText");
            Raise("HideGlyph");
            Raise("SettingsHint");
            UpdateState();
            engine.NotifyVisibilityChanged();
        }

        async void AttemptAsync(bool manual)
        {
            attempting = true;
            manualAttempt = manual;
            UpdateState();
            try
            {
                engine.TakeSlot(this);
                await EnsureStartedAsync();
                var conn = connection;
                if (conn == null)
                {
                    Fail(lastError ?? "Couldn't prepare the connection");
                    return;
                }

                // Always open, even if Windows already claims the link is up: that claim may be left over
                // from another connection, and only OpenAsync sets up the audio for this phone.
                Log.Info("{0}: {1}", DisplayName, manualAttempt ? "connecting" : "looking for it nearby");
                AudioPlaybackConnectionOpenResult result;
                openingConnection = conn;
                try
                {
                    result = await conn.OpenAsync();
                }
                finally
                {
                    openingConnection = null;
                }
                if (conn != connection)
                {
                    // Dropped while opening; CloseConnection left the disposing to us.
                    Release(conn);
                    return;
                }

                if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success)
                {
                    failures = 0;
                    lastError = null;
                    openedAt = DateTime.UtcNow;
                    openedByRebuild = fixing;
                    if (conn.State == AudioPlaybackConnectionState.Opened) engine.ClaimStream(this);
                    else nextAttempt = DateTime.UtcNow.AddSeconds(10);
                }
                else
                {
                    string reason = Describe(result.Status);
                    string detail = result.ExtendedError != null ? " (" + result.ExtendedError.Message.Trim() + ")" : "";
                    // Windows says "a device attached to the system is not functioning" when the phone is out of range.
                    if (result.ExtendedError != null && result.ExtendedError.HResult == unchecked((int)0x8007001F))
                    {
                        reason = "Not found nearby";
                        detail = "";
                    }
                    Log.Info("{0}: {1}{2}", DisplayName, reason.ToLowerInvariant(), detail);
                    Fail(reason);
                    // Next attempt starts from a fresh object.
                    if (!isOpen) CloseConnection();
                }
            }
            catch (Exception ex)
            {
                Log.Error(DisplayName + ": connection attempt failed", ex);
                Fail("Connection failed");
                if (!isOpen) CloseConnection();
            }
            finally
            {
                attempting = false;
                manualAttempt = false;
                fixing = false;
                settling = false;
                UpdateState();
            }
        }

        static string Describe(AudioPlaybackConnectionOpenResultStatus status)
        {
            switch (status)
            {
                case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut: return "Not found nearby";
                case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem: return "Windows blocked the connection";
                default: return "Connection failed";
            }
        }

        void Fail(string reason)
        {
            failures++;
            lastError = reason;
            nextAttempt = DateTime.UtcNow + RetryDelay();
            if (manualAttempt) manualFailedAt = DateTime.UtcNow;
        }

        // You pressed Connect and it didn't work in the last few seconds: worth saying so on screen.
        public bool RecentlyFailedToConnect
        {
            get { return DateTime.UtcNow - manualFailedAt < TimeSpan.FromSeconds(15) && !isOpen && !attempting; }
        }

        public string LastError { get { return lastError; } }

        TimeSpan RetryDelay()
        {
            if (failures >= 1 && failures <= RetrySeconds.Length) return TimeSpan.FromSeconds(RetrySeconds[failures - 1]);
            // Just walked out of range? Keep checking often for a while, then settle down.
            bool recentlyLost = DateTime.UtcNow - lostAt < TimeSpan.FromMinutes(15);
            return TimeSpan.FromSeconds(recentlyLost ? 20 : 30);
        }

        async void StartInBackground()
        {
            await EnsureStartedAsync();
            UpdateState();
        }

        // Opening a connection whose StartAsync hasn't finished makes Windows refuse it, and throwing that
        // half-started object away then crashes Windows.Media.Devices.dll. So always wait for a start in progress.
        async Task EnsureStartedAsync()
        {
            while (starting) await Task.Delay(100);
            if (connection != null) return;
            starting = true;
            try
            {
                await StartConnectionAsync();
            }
            finally
            {
                starting = false;
            }
        }

        async Task StartConnectionAsync()
        {
            AudioPlaybackConnection conn = null;
            try
            {
                conn = AudioPlaybackConnection.TryCreateFromId(deviceId);
                if (conn == null)
                {
                    lastError = "Can't receive audio from this device right now";
                    nextStart = DateTime.UtcNow.AddSeconds(30);
                    return;
                }
                conn.StateChanged += stateHandler;
                connection = conn;
                startingConnection = conn;
                try
                {
                    await conn.StartAsync();
                }
                finally
                {
                    startingConnection = null;
                }
                if (connection != conn)
                {
                    // Closed (radio off, hidden, shut down...) while we were starting; CloseConnection
                    // left the disposing to us so it happens only after StartAsync has finished.
                    Release(conn);
                    return;
                }
                if (conn.State == AudioPlaybackConnectionState.Opened) engine.OnSinkStateChanged(this, true);
            }
            catch (Exception ex)
            {
                Log.Error(DisplayName + ": couldn't start the audio receiver", ex);
                if (conn != null && connection == conn) connection = null;
                startingConnection = null;
                Release(conn);
                lastError = "Couldn't start the audio receiver";
                nextStart = DateTime.UtcNow.AddSeconds(15);
            }
        }

        void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
        {
            engine.Post(() =>
            {
                if (sender != connection) return;
                AudioPlaybackConnectionState current;
                try
                {
                    current = sender.State;
                }
                catch
                {
                    return;
                }
                engine.OnSinkStateChanged(this, current == AudioPlaybackConnectionState.Opened);
            });
        }

        public void MarkOpen()
        {
            if (isOpen) return;
            isOpen = true;
            paused = false;
            failures = 0;
            lastError = null;
            streamingSince = DateTime.Now;
            settings.LastConnected = DateTime.Now;
            engine.SaveSettings();
            // Did we dial it, or did the phone connect by itself? Windows may report the connection
            // a moment before or after our OpenAsync returns.
            bool justOpened = DateTime.UtcNow - openedAt < TimeSpan.FromSeconds(15);
            bool rebuilt = attempting ? fixing : justOpened && openedByRebuild;
            ShouldSettle = (attempting || justOpened) && !rebuilt;
            Log.Info("{0}: connected, playing audio", DisplayName);
            UpdateState();
            // A rebuild already said "connected" the first time round.
            if (!rebuilt) engine.OnDeviceConnected(this);
        }

        public void MarkLost()
        {
            Log.Info("{0}: connection lost", DisplayName);
            CloseConnection();
            lostAt = DateTime.UtcNow;
            failures = 0;
            nextStart = DateTime.UtcNow.AddSeconds(1.5);
            nextAttempt = DateTime.UtcNow.AddSeconds(2);
            UpdateState();
            engine.OnDeviceDisconnected(this, settings.AutoConnect && !paused);
        }

        void CloseConnection()
        {
            var conn = connection;
            bool wasOpen = isOpen;
            connection = null;
            isOpen = false;
            streamingSince = null;
            // Disposing an object while Windows is still starting or opening it crashes Windows.Media.Devices.dll,
            // so in that case StartConnectionAsync / AttemptAsync dispose it as soon as Windows is done.
            if (conn != startingConnection && conn != openingConnection) Release(conn);
            if (wasOpen) engine.ReleaseActive(this);
        }

        void Release(AudioPlaybackConnection conn)
        {
            if (conn == null) return;
            try { conn.StateChanged -= stateHandler; }
            catch { }
            try { conn.Dispose(); }
            catch (Exception ex) { Log.Error(DisplayName + ": couldn't close the old connection", ex); }
        }

        async void LookUpBluetoothDevice()
        {
            ulong raw;
            if (address == null || !ulong.TryParse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw))
            {
                Log.Info("Found {0}", DisplayName);
                return;
            }
            try
            {
                var device = await BluetoothDevice.FromBluetoothAddressAsync(raw);
                if (device == null || shutDown)
                {
                    Log.Info("Found {0}", DisplayName);
                    return;
                }
                bluetoothDevice = device;
                bluetoothName = device.Name;
                var major = device.ClassOfDevice.MajorClass;
                isTablet = major == BluetoothMajorClass.Computer;
                if (settings.IsNew)
                {
                    // Phones reconnect automatically out of the box; tablets and others only when asked.
                    settings.IsNew = false;
                    settings.AutoConnect = major == BluetoothMajorClass.Phone;
                    engine.SaveSettings();
                    Raise("AutoConnect");
                }
                device.NameChanged += (d, o) => engine.Post(() =>
                {
                    bluetoothName = d.Name;
                    RaiseNames();
                });
                device.ConnectionStatusChanged += (d, o) => engine.Post(() => OnLinkChanged(d.ConnectionStatus));
                RaiseNames();
                Raise("Glyph");
                UpdateState();
                Log.Info("Found {0}", DisplayName);
            }
            catch (Exception ex)
            {
                Log.Info("Found {0}", DisplayName);
                Log.Error(DisplayName + ": couldn't read Bluetooth details", ex);
            }
        }

        // The phone came into range / connected to the PC for any reason: try right away.
        void OnLinkChanged(BluetoothConnectionStatus status)
        {
            if (shutDown || status != BluetoothConnectionStatus.Connected) return;
            if (isOpen || paused || settings.Hidden || !settings.AutoConnect) return;
            Log.Info("{0} is nearby", DisplayName);
            failures = 0;
            nextAttempt = DateTime.UtcNow;
        }

        void RaiseNames()
        {
            Raise("DisplayName");
            Raise("EditableName");
            UpdateStatusText();
            engine.NotifyChanged();
        }

        void UpdateState()
        {
            LinkState next;
            if (!engine.RadioOn) next = LinkState.Off;
            else if (isOpen) next = LinkState.Streaming;
            else if ((attempting && manualAttempt) || fixing) next = LinkState.Connecting;
            else if (paused) next = LinkState.Paused;
            else if (settings.AutoConnect && !settings.Hidden && !engine.AutoPaused && !engine.PcIdle) next = LinkState.Waiting;
            else next = LinkState.Idle;

            if (Set(ref state, next, "State"))
            {
                Raise("StateName");
                Raise("ShowConnect");
                Raise("ShowConnecting");
                Raise("ShowDisconnect");
            }
            UpdateStatusText();
            engine.NotifyChanged();
        }

        void UpdateStatusText()
        {
            string text;
            switch (state)
            {
                case LinkState.Off:
                    text = "Bluetooth is off";
                    break;
                case LinkState.Streaming:
                    text = "Playing audio · " + FormatDuration(streamingSince.HasValue ? DateTime.Now - streamingSince.Value : TimeSpan.Zero);
                    break;
                case LinkState.Connecting:
                    text = "Connecting…";
                    break;
                case LinkState.Paused:
                    text = "Disconnected";
                    break;
                case LinkState.Waiting:
                    if (attempting) text = "Looking for it nearby…";
                    else if (lastError != null) text = lastError + " · retrying";
                    else text = "Auto-connects when nearby";
                    break;
                default:
                    if (engine.PcIdle && settings.AutoConnect) text = "Resting while the PC is idle";
                    else text = lastError ?? "Not connected";
                    break;
            }
            Set(ref statusText, text, "StatusText");
        }

        public static string FormatDuration(TimeSpan span)
        {
            if (span.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds);
            return string.Format("{0}:{1:00}", span.Minutes, span.Seconds);
        }
    }
}
