using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BlueAux
{
    // CoPlay inside the app: off, hosting, looking for a host, or playing with one. Lives on the UI thread;
    // Tick runs with the engine's timer.
    //
    // Hosting: Windows plays the phone the moment it arrives and that can't be delayed, so the host mutes
    // Windows' stream for the phone (its Volume Mixer entry) and plays its own capture through the same
    // delayed player as the guests. The mute is checked every tick, undone on Stop, and remembered in
    // settings so a crash can't leave the phone muted.
    public sealed class CoPlaySession
    {
        public enum Modes { Off, Hosting, Searching, Joined }

        // The connected phone's Windows stream, from the sound watcher (null with no phone).
        public static volatile string PhoneStream;
        // The stream CoPlay has muted; SoundCheck reports it as unmuted so the watcher isn't fooled.
        public static volatile string MutedStream;

        public const int MaxAdjustMs = 300;
        static readonly TimeSpan SearchEvery = TimeSpan.FromSeconds(3);
        readonly AppSettings settings;
        readonly Dispatcher ui;
        CoPlayHost host;
        CoPlayGuest guest;
        PhoneCapture capture;
        CoPlayPlayer player;
        bool finding;
        DateTime lastFind;

        public CoPlaySession(AppSettings settings, Dispatcher ui)
        {
            this.settings = settings;
            this.ui = ui;
            CoPlayLog.Init("coplay.log");
            Hosts = new List<KeyValuePair<string, IPEndPoint>>();
            // Its own second, not the engine's (that slows to 5 s when idle): the host's mute and the status
            // should follow within a second.
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) =>
            {
                Tick();
                if (Mode != Modes.Off) Raise();
            }, ui);
            timer.Start();
        }

        public Modes Mode { get; private set; }
        // Something went wrong that the user should see ("couldn't host", "the host stopped").
        public string Problem { get; private set; }
        // Hosts found while searching: PC name and address.
        public List<KeyValuePair<string, IPEndPoint>> Hosts { get; private set; }
        public string HostName { get { return guest != null ? guest.HostName : null; } }
        public string[] GuestNames { get { return host != null ? host.GuestNames : new string[0]; } }
        public bool HostAnswering { get { return guest != null && guest.Answering; } }
        public bool Receiving { get { return guest != null && guest.Receiving; } }
        public bool PhoneFound { get { return capture != null && capture.Running; } }

        public event Action Changed;

        public int AdjustMs
        {
            get { return settings.CoPlayAdjustMs; }
            set
            {
                settings.CoPlayAdjustMs = Math.Max(-MaxAdjustMs, Math.Min(MaxAdjustMs, value));
                settings.Save();
                if (player != null) player.Adjust = settings.CoPlayAdjustMs * HostClock.Ms;
            }
        }

        public void StartHost()
        {
            Stop();
            try
            {
                host = new CoPlayHost(Environment.MachineName);
            }
            catch (SocketException ex)
            {
                CoPlayLog.Write("host: can't listen on port {0}: {1}", CoPlayWire.Port, ex.Message);
                Problem = "Couldn't host: another app on this PC is using CoPlay's network port (" + CoPlayWire.Port + ").";
                Raise();
                return;
            }
            Log.Info("CoPlay: hosting");
            host.GuestsChanged += () => ui.BeginInvoke(new Action(Raise));
            player = new CoPlayPlayer(t => t, false) { Adjust = settings.CoPlayAdjustMs * HostClock.Ms };
            var sending = host;
            var playing = player;
            capture = new PhoneCapture((time, pcm, offset, frames) =>
            {
                sending.SendAudio(time, pcm, offset, frames);
                playing.Add(time, pcm, offset, frames);
            });
            Mode = Modes.Hosting;
            Tick();
            Raise();
        }

        public void StartSearch()
        {
            Stop();
            Mode = Modes.Searching;
            Find();
            Raise();
        }

        public void Join(string name, IPEndPoint address)
        {
            Stop();
            Log.Info("CoPlay: joining " + name);
            var joined = new CoPlayGuest(address, name);
            guest = joined;
            player = new CoPlayPlayer(joined.ToHost, false) { Adjust = settings.CoPlayAdjustMs * HostClock.Ms };
            var playing = player;
            joined.Audio += playing.Add;
            joined.HostLeft += () => ui.BeginInvoke(new Action(() =>
            {
                if (guest != joined) return;
                Stop();
                Problem = name + " stopped hosting.";
                Raise();
            }));
            Mode = Modes.Joined;
            Raise();
        }

        public void Stop()
        {
            if (Mode == Modes.Hosting || Mode == Modes.Joined) Log.Info("CoPlay: stopped");
            if (capture != null) capture.Dispose();
            if (host != null) host.Dispose();
            if (guest != null) guest.Dispose();
            if (player != null) player.Dispose();
            capture = null;
            host = null;
            guest = null;
            player = null;
            Unmute();
            Hosts = new List<KeyValuePair<string, IPEndPoint>>();
            Problem = null;
            Mode = Modes.Off;
            Raise();
        }

        void Tick()
        {
            if (Mode == Modes.Searching && DateTime.UtcNow - lastFind > SearchEvery) Find();

            if (Mode == Modes.Hosting)
            {
                string stream = PhoneStream;
                capture.InputId = SoundCheck.InputIdOf(stream);
                if (stream != MutedStream) Unmute();
                // Only while our own copy plays; otherwise the phone would go silent here.
                if (stream != null && capture.Running && SoundCheck.SetStreamMute(stream, true) && MutedStream != stream)
                {
                    MutedStream = stream;
                    settings.CoPlayMutedStream = stream;
                    settings.Save();
                    CoPlayLog.Write("host: muted Windows' own copy of the phone; this PC plays it in step with the guests");
                }
                else if (!capture.Running) Unmute();
            }
            else if (settings.CoPlayMutedStream != null && SoundCheck.SetStreamMute(settings.CoPlayMutedStream, false))
            {
                // Left muted by an earlier run that ended while hosting.
                settings.CoPlayMutedStream = null;
                settings.Save();
                CoPlayLog.Write("unmuted the phone's stream left muted by an earlier run");
            }
        }

        void Unmute()
        {
            string stream = MutedStream;
            if (stream == null) return;
            SoundCheck.SetStreamMute(stream, false);
            MutedStream = null;
            settings.CoPlayMutedStream = null;
            settings.Save();
            CoPlayLog.Write("host: unmuted Windows' own copy of the phone");
        }

        void Find()
        {
            if (finding) return;
            finding = true;
            lastFind = DateTime.UtcNow;
            Task.Run(() => CoPlayGuest.Find(1500)).ContinueWith(task => ui.BeginInvoke(new Action(() =>
            {
                finding = false;
                lastFind = DateTime.UtcNow;
                if (Mode != Modes.Searching || task.IsFaulted) return;
                // This PC's own host, if a test one runs here, isn't one to join from the app.
                Hosts = task.Result.Where(h => h.Key != Environment.MachineName).ToList();
                Raise();
            })));
        }

        void Raise()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
