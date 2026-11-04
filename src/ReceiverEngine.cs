using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Media.Audio;

namespace PhoneSpeaker
{
    public enum RadioStatus { Unknown, On, Off, Missing }

    // Finds every paired device that can send audio to this PC, keeps a DeviceSession for each,
    // and decides when to reach out to which phone. Only one automatic attempt runs at a time,
    // and none while something is already playing, so we never fight over the Bluetooth radio.
    public sealed class ReceiverEngine
    {
        const string AddressProperty = "System.DeviceInterface.Bluetooth.DeviceAddress";

        readonly Dispatcher dispatcher;
        readonly AppSettings settings;
        readonly DispatcherTimer timer;
        readonly ObservableCollection<DeviceSession> sessions = new ObservableCollection<DeviceSession>();
        readonly ActivityMonitor activity = new ActivityMonitor();
        DeviceWatcher watcher;
        DeviceSession active;
        bool autoPaused;
        bool pcIdle;
        DateTime nextAudioCheck;
        Radio bluetoothRadio;
        RadioStatus radioStatus = RadioStatus.Unknown;
        DateTime nextRadioCheck;
        DateTime nextWatcherRestart = DateTime.MaxValue;
        bool enumerationDone;
        bool shuttingDown;

        public ReceiverEngine(Dispatcher dispatcher, AppSettings settings)
        {
            this.dispatcher = dispatcher;
            this.settings = settings;
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += delegate { Tick(); };
        }

        public event Action Changed;
        public event Action Ticked;
        public event Action VisibilityChanged;
        public event Action<DeviceSession> DeviceConnected;
        public event Action<DeviceSession, bool> DeviceDisconnected;
        public event Action<bool> IdleChanged;

        public ObservableCollection<DeviceSession> Sessions { get { return sessions; } }
        public AppSettings Settings { get { return settings; } }
        public bool EnumerationDone { get { return enumerationDone; } }
        public RadioStatus RadioStatus { get { return radioStatus; } }
        public bool RadioOn { get { return radioStatus == RadioStatus.On || radioStatus == RadioStatus.Unknown; } }

        // The device that is really streaming right now, if any.
        public DeviceSession Active { get { return active; } }

        // Set when you press Disconnect: nothing reconnects by itself until you press Connect again,
        // otherwise another nearby phone would grab the audio the moment you let go.
        public bool AutoPaused { get { return autoPaused; } }

        public void SetAutoPaused(bool paused)
        {
            if (autoPaused == paused) return;
            autoPaused = paused;
            Log.Info(paused ? "Auto-connect paused until you press Connect" : "Auto-connect resumed");
            foreach (var session in sessions) session.Refresh();
            NotifyChanged();
        }

        // Nobody has used the PC for a while (and nothing is playing): stay disconnected and stop
        // looking for phones until someone is back, like a chat app showing you as "idle".
        public bool PcIdle { get { return pcIdle; } }

        void UpdateIdle(DateTime now)
        {
            if (active != null && now >= nextAudioCheck)
            {
                nextAudioCheck = now.AddSeconds(5);
                if (PhoneAudioMeter.IsPlaying()) activity.NoteAudio();
            }

            int minutes = Math.Max(1, settings.IdleMinutes);
            bool idle = settings.RestWhenIdle && activity.IdleFor() >= TimeSpan.FromMinutes(minutes);
            if (idle == pcIdle) return;
            pcIdle = idle;
            // While resting we only need to notice when someone is back.
            timer.Interval = TimeSpan.FromSeconds(idle ? 5 : 1);
            if (idle)
            {
                Log.Info("PC idle for {0} minutes: resting until you're back", minutes);
                if (active != null && settings.DisconnectWhenIdle) active.Rest();
                foreach (var session in sessions) session.Refresh();
            }
            else
            {
                Log.Info("You're back: looking for your phone again");
                foreach (var session in sessions) session.WakeUp();
            }
            NotifyChanged();
            var handler = IdleChanged;
            if (handler != null) handler(idle);
        }

        public void Start()
        {
            Log.Info("Phone Speaker started");
            StartWatcher();
            CheckRadio();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            timer.Start();
        }

        public void Shutdown()
        {
            shuttingDown = true;
            timer.Stop();
            activity.Dispose();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            StopWatcher();
            foreach (var session in sessions) session.Shutdown();
        }

        public void Post(Action action)
        {
            if (!shuttingDown) dispatcher.BeginInvoke(action);
        }

        public void SaveSettings()
        {
            settings.Save();
        }

        public void NotifyChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        public void NotifyVisibilityChanged()
        {
            var handler = VisibilityChanged;
            if (handler != null) handler();
            NotifyChanged();
        }

        public void OnDeviceConnected(DeviceSession session)
        {
            var handler = DeviceConnected;
            if (handler != null) handler(session);
        }

        public void OnDeviceDisconnected(DeviceSession session, bool willReconnect)
        {
            var handler = DeviceDisconnected;
            if (handler != null) handler(session, willReconnect);
        }

        // Windows keeps a single Opened/Closed state shared by every connection object in the process,
        // which made it impossible to tell which phone was really playing when several were started.
        // So only one session ever holds a connection (TakeSlot / the listener), and the reporter is
        // always the device the change is about.
        public void OnSinkStateChanged(DeviceSession reporter, bool opened)
        {
            if (opened)
            {
                if (active == null) SetActive(reporter);
                return;
            }
            if (reporter != active) return;
            active = null;
            reporter.MarkLost();
        }

        // About to dial a device: everyone else lets go of their idle connection first.
        public void TakeSlot(DeviceSession session)
        {
            foreach (var other in sessions)
                if (other != session) other.ReleaseIdleConnection();
        }

        // Our own OpenAsync succeeded for this session.
        public void ClaimStream(DeviceSession session)
        {
            if (active == session) return;
            if (active != null)
            {
                var previous = active;
                active = null;
                previous.MarkLost();
            }
            SetActive(session);
        }

        public void ReleaseActive(DeviceSession session)
        {
            if (active == session) active = null;
        }

        void SetActive(DeviceSession session)
        {
            active = session;
            int index = sessions.IndexOf(session);
            if (index > 0) sessions.Move(index, 0);
            session.MarkOpen();
        }

        // The one device whose connection stays started so it can connect by itself: whatever is playing
        // or being dialled, otherwise the phone used most recently (nothing while auto-connect is paused).
        DeviceSession PickListener()
        {
            if (active != null) return active;
            var dialling = sessions.FirstOrDefault(s => s.IsAttempting);
            if (dialling != null) return dialling;
            if (autoPaused || pcIdle || !RadioOn) return null;
            return sessions.Where(s => s.CanListen).OrderByDescending(s => s.LastConnected).FirstOrDefault();
        }

        public void ResetAll()
        {
            Log.Info("Restarting all connections");
            foreach (var session in sessions) session.Reset(TimeSpan.FromSeconds(1));
        }

        public async void TurnOnBluetooth()
        {
            bool done = false;
            try
            {
                if (bluetoothRadio != null && await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
                    done = await bluetoothRadio.SetStateAsync(RadioState.On) == RadioAccessStatus.Allowed;
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't turn on Bluetooth", ex);
            }
            if (!done) Shell.Open("ms-settings:bluetooth");
        }

        void Tick()
        {
            var now = DateTime.UtcNow;
            if (bluetoothRadio == null && now >= nextRadioCheck) CheckRadio();
            if (now >= nextWatcherRestart)
            {
                nextWatcherRestart = DateTime.MaxValue;
                StopWatcher();
                StartWatcher();
            }

            UpdateIdle(now);
            var listener = PickListener();
            foreach (var session in sessions.ToList()) session.Tick(now, session == listener);

            if (RadioOn && !autoPaused && !pcIdle && !sessions.Any(s => s.State == LinkState.Streaming || s.IsAttempting))
            {
                // The phone you used last goes first whenever it's due, like a car stereo.
                var due = sessions.Where(s => s.WantsAutoAttempt(now)).OrderByDescending(s => s.LastConnected).FirstOrDefault();
                if (due != null) due.Connect(false);
            }

            var handler = Ticked;
            if (handler != null) handler();
        }

        void StartWatcher()
        {
            try
            {
                var current = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector(), new[] { AddressProperty });
                watcher = current;
                current.Added += (w, info) => Post(() => { if (w == watcher) OnDeviceAdded(info); });
                current.Removed += (w, update) => Post(() => { if (w == watcher) OnDeviceRemoved(update.Id); });
                // The watcher only reports changes once something listens to Updated, even if we don't need them.
                current.Updated += (w, update) => { };
                current.EnumerationCompleted += (w, o) => Post(() =>
                {
                    if (w != watcher) return;
                    enumerationDone = true;
                    NotifyChanged();
                });
                current.Stopped += (w, o) => Post(() =>
                {
                    if (w != watcher || shuttingDown) return;
                    Log.Info("Device search stopped unexpectedly; restarting it");
                    nextWatcherRestart = DateTime.UtcNow.AddSeconds(5);
                });
                current.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't search for Bluetooth devices", ex);
                enumerationDone = true;
                nextWatcherRestart = DateTime.UtcNow.AddSeconds(10);
            }
        }

        void StopWatcher()
        {
            var current = watcher;
            watcher = null;
            if (current == null) return;
            try
            {
                if (current.Status == DeviceWatcherStatus.Started || current.Status == DeviceWatcherStatus.EnumerationCompleted)
                    current.Stop();
            }
            catch
            {
            }
        }

        void OnDeviceAdded(DeviceInformation info)
        {
            if (sessions.Any(s => s.DeviceId == info.Id)) return;
            string address = null;
            object value;
            if (info.Properties.TryGetValue(AddressProperty, out value) && value != null) address = value.ToString();

            var session = new DeviceSession(this, info.Id, address, info.Name, settings.ForDevice(address ?? info.Id));
            // Most recently used devices first.
            int index = 0;
            while (index < sessions.Count && sessions[index].LastConnected >= session.LastConnected) index++;
            sessions.Insert(index, session);
            Log.Info("Found {0}", session.DisplayName);
            session.Initialize();
            NotifyChanged();
        }

        void OnDeviceRemoved(string id)
        {
            var session = sessions.FirstOrDefault(s => s.DeviceId == id);
            if (session == null) return;
            Log.Info("{0} was removed from this PC", session.DisplayName);
            session.Shutdown();
            sessions.Remove(session);
            NotifyChanged();
        }

        async void CheckRadio()
        {
            nextRadioCheck = DateTime.UtcNow.AddSeconds(15);
            try
            {
                var radios = await Radio.GetRadiosAsync();
                var radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (radio == null)
                {
                    SetRadioStatus(RadioStatus.Missing);
                    return;
                }
                bluetoothRadio = radio;
                radio.StateChanged += (r, o) => Post(UpdateRadioStatus);
                UpdateRadioStatus();
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't read the Bluetooth radio state", ex);
            }
        }

        void UpdateRadioStatus()
        {
            if (bluetoothRadio == null) return;
            SetRadioStatus(bluetoothRadio.State == RadioState.On ? RadioStatus.On : RadioStatus.Off);
        }

        void SetRadioStatus(RadioStatus status)
        {
            bool wasOn = RadioOn;
            if (status == radioStatus) return;
            radioStatus = status;
            if (RadioOn != wasOn)
            {
                Log.Info(RadioOn ? "Bluetooth is on" : status == RadioStatus.Missing ? "No Bluetooth adapter found" : "Bluetooth was turned off");
                foreach (var session in sessions) session.OnRadioChanged(RadioOn);
            }
            NotifyChanged();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            Post(() =>
            {
                Log.Info("PC woke up; refreshing connections");
                foreach (var session in sessions) session.Reset(TimeSpan.FromSeconds(4));
            });
        }
    }
}
