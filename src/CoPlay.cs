using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BlueAux
{
    // CoPlay: the PC the phone plays to (the host) sends its audio over the local network to other BlueAux PCs
    // (guests), and every PC plays it at the same moment. See docs/superpowers/specs/2026-11-18-coplay-design.md.

    // Time in 100 ns units of the performance counter: the clock WASAPI stamps captured audio with.
    static class HostClock
    {
        public const long Second = 10000000, Ms = 10000;
        static readonly long Freq = Stopwatch.Frequency;

        public static long Now
        {
            get
            {
                long qpc = Stopwatch.GetTimestamp();
                return qpc / Freq * Second + qpc % Freq * Second / Freq;
            }
        }
    }

    // coplay.log: what CoPlay saw, a line every couple of seconds while it runs. No phone or song names.
    static class CoPlayLog
    {
        static readonly object Sync = new object();
        static string path;

        public static void Init(string fileName)
        {
            path = Path.Combine(AppSettings.Folder, fileName);
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                if (File.Exists(path) && new FileInfo(path).Length > 2000000) File.Delete(path);
            }
            catch { }
        }

        public static void Write(string format, params object[] args)
        {
            if (path == null) return;
            string line = DateTime.Now.ToString("HH:mm:ss.fff ") + (args.Length > 0 ? string.Format(format, args) : format) + Environment.NewLine;
            lock (Sync)
            {
                try { File.AppendAllText(path, line); }
                catch { }
            }
        }
    }

    // Every packet starts "BX", the version and its type. Anything else on the port is ignored.
    static class CoPlayWire
    {
        public const int Port = 47101;
        public const byte Audio = 1, Ping = 2, Pong = 3, Discover = 4, HostHere = 5, Bye = 6;
        // The audio on the wire: 48 kHz, 16-bit stereo. 240 frames (5 ms, 960 bytes) per packet fits one
        // Wi-Fi frame without splitting.
        public const int Rate = 48000, FrameBytes = 4, MaxFrames = 240, AudioHeader = 12;
        const byte Version = 1;

        public static byte[] Packet(byte type, int bodyLength)
        {
            var p = new byte[4 + bodyLength];
            p[0] = (byte)'B';
            p[1] = (byte)'X';
            p[2] = Version;
            p[3] = type;
            return p;
        }

        public static int TypeOf(byte[] p)
        {
            return p != null && p.Length >= 4 && p[0] == 'B' && p[1] == 'X' && p[2] == Version ? p[3] : 0;
        }

        public static byte[] WithName(byte type, int before, string name)
        {
            byte[] text = Encoding.UTF8.GetBytes(name ?? "");
            if (text.Length > 64) Array.Resize(ref text, 64);
            var p = Packet(type, before + text.Length);
            Buffer.BlockCopy(text, 0, p, 4 + before, text.Length);
            return p;
        }

        public static string NameIn(byte[] p, int at)
        {
            if (p.Length <= at) return "";
            return Encoding.UTF8.GetString(p, at, Math.Min(64, p.Length - at));
        }

        public static void Put(byte[] p, int at, long value)
        {
            for (int i = 0; i < 8; i++) p[at + i] = (byte)(value >> (8 * i));
        }

        // Windows reports an ICMP "port unreachable" from an earlier send as an error on the next Receive;
        // that only means a guest or host went away, so turn it off (SIO_UDP_CONNRESET).
        public static UdpClient Open(int port)
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.IOControl(-1744830452, new byte[] { 0 }, null);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return udp;
        }
    }

    // The host: answers "who's hosting", treats every PC that pings it as a guest, and sends them the audio.
    sealed class CoPlayHost : IDisposable
    {
        sealed class Guest
        {
            public string Name;
            public long LastSeen;
        }

        static readonly long GuestTimeout = 5 * HostClock.Second;
        readonly UdpClient udp;
        readonly byte[] hereReply;
        readonly Dictionary<IPEndPoint, Guest> guests = new Dictionary<IPEndPoint, Guest>();
        readonly Timer expiry;
        volatile bool stopped;
        long sent;

        // Raised on a background thread when a guest joins or leaves.
        public event Action GuestsChanged;

        // Throws SocketException when the port is taken (another host on this PC).
        public CoPlayHost(string name)
        {
            udp = CoPlayWire.Open(CoPlayWire.Port);
            hereReply = CoPlayWire.WithName(CoPlayWire.HostHere, 0, name);
            new Thread(Receive) { IsBackground = true, Name = "CoPlay host" }.Start();
            expiry = new Timer(_ => Expire(), null, 1000, 1000);
            CoPlayLog.Write("host: listening on UDP port {0}", CoPlayWire.Port);
        }

        public string[] GuestNames
        {
            get
            {
                lock (guests) return guests.Values.Select(g => g.Name).OrderBy(n => n).ToArray();
            }
        }

        public long PacketsSent { get { return Interlocked.Read(ref sent); } }

        void Receive()
        {
            while (!stopped)
            {
                IPEndPoint from = null;
                byte[] p;
                try
                {
                    p = udp.Receive(ref from);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    if (stopped) return;
                    continue;
                }

                switch (CoPlayWire.TypeOf(p))
                {
                    case CoPlayWire.Discover:
                        Send(hereReply, from);
                        break;
                    case CoPlayWire.Ping:
                        if (p.Length < 12) break;
                        var pong = CoPlayWire.Packet(CoPlayWire.Pong, 16);
                        Buffer.BlockCopy(p, 4, pong, 4, 8);
                        CoPlayWire.Put(pong, 12, HostClock.Now);
                        Send(pong, from);
                        Seen(from, CoPlayWire.NameIn(p, 12));
                        break;
                    case CoPlayWire.Bye:
                        bool left;
                        lock (guests) left = guests.Remove(from);
                        if (left)
                        {
                            CoPlayLog.Write("host: {0} left", from);
                            Raise();
                        }
                        break;
                }
            }
        }

        void Seen(IPEndPoint from, string name)
        {
            bool joined = false;
            lock (guests)
            {
                Guest guest;
                if (!guests.TryGetValue(from, out guest))
                {
                    guest = new Guest();
                    guests[from] = guest;
                    joined = true;
                }
                guest.Name = name.Length > 0 ? name : from.Address.ToString();
                guest.LastSeen = HostClock.Now;
            }
            if (joined)
            {
                CoPlayLog.Write("host: {0} joined from {1}", name, from);
                Raise();
            }
        }

        void Expire()
        {
            long now = HostClock.Now;
            List<IPEndPoint> gone;
            lock (guests)
            {
                gone = guests.Where(g => now - g.Value.LastSeen > GuestTimeout).Select(g => g.Key).ToList();
                foreach (var key in gone) guests.Remove(key);
            }
            foreach (var key in gone) CoPlayLog.Write("host: {0} stopped answering, dropped", key);
            if (gone.Count > 0) Raise();
        }

        // Sends audio stamped with the host time of its first frame to every guest, in packets of up to 5 ms.
        public void SendAudio(long time, byte[] pcm, int offset, int frames)
        {
            IPEndPoint[] targets;
            lock (guests) targets = guests.Keys.ToArray();
            if (targets.Length == 0) return;
            for (int done = 0; done < frames; done += CoPlayWire.MaxFrames)
            {
                int n = Math.Min(CoPlayWire.MaxFrames, frames - done);
                var p = CoPlayWire.Packet(CoPlayWire.Audio, 8 + n * CoPlayWire.FrameBytes);
                CoPlayWire.Put(p, 4, time + done * HostClock.Second / CoPlayWire.Rate);
                Buffer.BlockCopy(pcm, offset + done * CoPlayWire.FrameBytes, p, CoPlayWire.AudioHeader, n * CoPlayWire.FrameBytes);
                foreach (var target in targets) Send(p, target);
            }
        }

        void Send(byte[] p, IPEndPoint to)
        {
            try
            {
                udp.Send(p, p.Length, to);
                Interlocked.Increment(ref sent);
            }
            catch { }
        }

        void Raise()
        {
            var handler = GuestsChanged;
            if (handler != null) handler();
        }

        public void Dispose()
        {
            if (stopped) return;
            stopped = true;
            expiry.Dispose();
            var bye = CoPlayWire.Packet(CoPlayWire.Bye, 0);
            IPEndPoint[] targets;
            lock (guests) targets = guests.Keys.ToArray();
            foreach (var target in targets) Send(bye, target);
            udp.Close();
            CoPlayLog.Write("host: stopped");
        }
    }

    // A guest: pings its host twice a second (that's also how the host knows it's there) and works out
    // the difference between the host's clock and its own from the answers.
    sealed class CoPlayGuest : IDisposable
    {
        public readonly IPEndPoint Host;
        public readonly string HostName;
        readonly UdpClient udp;
        readonly byte[] ping;
        readonly Timer pinger;
        // The last 20 answers (10 s); the one with the shortest round trip was delayed least by the network.
        readonly long[] trips = new long[20], offsets = new long[20];
        int answers;
        long offset, trip, lastPong, lastAudio, audioPackets;
        volatile bool stopped;

        // hostTime of the first frame, the packet, where the PCM starts in it, how many frames.
        public event Action<long, byte[], int, int> Audio;
        // The host stopped hosting.
        public event Action HostLeft;

        public CoPlayGuest(IPEndPoint host, string hostName)
        {
            Host = host;
            HostName = hostName;
            udp = CoPlayWire.Open(0);
            ping = CoPlayWire.WithName(CoPlayWire.Ping, 8, Environment.MachineName);
            new Thread(Receive) { IsBackground = true, Name = "CoPlay guest" }.Start();
            pinger = new Timer(_ => Ping(), null, 0, 500);
            CoPlayLog.Write("guest: joining {0} at {1}", hostName, host);
        }

        // Host time minus this PC's time, in 100 ns.
        public long Offset { get { return Interlocked.Read(ref offset); } }
        public long RoundTrip { get { return Interlocked.Read(ref trip); } }
        public bool HasClock { get { return Interlocked.Read(ref lastPong) != 0; } }
        public long AudioPackets { get { return Interlocked.Read(ref audioPackets); } }
        // The host answered in the last 4 s.
        public bool Answering { get { return HostClock.Now - Interlocked.Read(ref lastPong) < 4 * HostClock.Second; } }
        public bool Receiving { get { return HostClock.Now - Interlocked.Read(ref lastAudio) < 2 * HostClock.Second; } }

        public long ToHost(long local) { return local + Offset; }

        void Ping()
        {
            if (stopped) return;
            CoPlayWire.Put(ping, 4, HostClock.Now);
            try { udp.Send(ping, ping.Length, Host); }
            catch { }
        }

        void Receive()
        {
            while (!stopped)
            {
                IPEndPoint from = null;
                byte[] p;
                try
                {
                    p = udp.Receive(ref from);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    if (stopped) return;
                    continue;
                }
                if (!from.Equals(Host)) continue;

                switch (CoPlayWire.TypeOf(p))
                {
                    case CoPlayWire.Pong:
                        if (p.Length < 20) break;
                        long t3 = HostClock.Now, t1 = BitConverter.ToInt64(p, 4), t2 = BitConverter.ToInt64(p, 12);
                        Clock(t1, t2, t3);
                        break;
                    case CoPlayWire.Audio:
                        int bytes = p.Length - CoPlayWire.AudioHeader;
                        if (bytes <= 0 || bytes % CoPlayWire.FrameBytes != 0) break;
                        Interlocked.Exchange(ref lastAudio, HostClock.Now);
                        Interlocked.Increment(ref audioPackets);
                        var handler = Audio;
                        if (handler != null) handler(BitConverter.ToInt64(p, 4), p, CoPlayWire.AudioHeader, bytes / CoPlayWire.FrameBytes);
                        break;
                    case CoPlayWire.Bye:
                        CoPlayLog.Write("guest: the host stopped");
                        var left = HostLeft;
                        if (left != null) left();
                        break;
                }
            }
        }

        // NTP-style: the host stamped t2 halfway (we assume) between our t1 and t3.
        void Clock(long t1, long t2, long t3)
        {
            long rtt = t3 - t1;
            if (rtt < 0 || rtt > HostClock.Second) return;
            lock (trips)
            {
                int i = answers++ % trips.Length;
                trips[i] = rtt;
                offsets[i] = t2 - (t1 + t3) / 2;
                int n = Math.Min(answers, trips.Length), best = 0;
                for (int k = 1; k < n; k++) if (trips[k] < trips[best]) best = k;
                Interlocked.Exchange(ref offset, offsets[best]);
                Interlocked.Exchange(ref trip, trips[best]);
            }
            Interlocked.Exchange(ref lastPong, t3);
        }

        // Asks the network who's hosting and waits for answers: host PC name and address, one per name.
        public static List<KeyValuePair<string, IPEndPoint>> Find(int waitMs)
        {
            var found = new List<KeyValuePair<string, IPEndPoint>>();
            using (var udp = CoPlayWire.Open(0))
            {
                udp.EnableBroadcast = true;
                var ask = CoPlayWire.Packet(CoPlayWire.Discover, 0);
                foreach (var target in BroadcastTargets())
                {
                    try { udp.Send(ask, ask.Length, new IPEndPoint(target, CoPlayWire.Port)); }
                    catch { }
                }
                var until = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (true)
                {
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    udp.Client.ReceiveTimeout = left;
                    IPEndPoint from = null;
                    byte[] p;
                    try { p = udp.Receive(ref from); }
                    catch (SocketException) { break; }
                    if (CoPlayWire.TypeOf(p) != CoPlayWire.HostHere) continue;
                    string name = CoPlayWire.NameIn(p, 4);
                    if (found.Any(f => f.Key == name)) continue;
                    found.Add(new KeyValuePair<string, IPEndPoint>(name, from));
                }
            }
            return found;
        }

        // 255.255.255.255 only leaves through one network card, so also each card's own broadcast address;
        // and this PC itself, for a host on the same PC.
        static IEnumerable<IPAddress> BroadcastTargets()
        {
            var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                        byte[] a = unicast.Address.GetAddressBytes(), m = unicast.IPv4Mask.GetAddressBytes();
                        for (int i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
                        targets.Add(new IPAddress(a));
                    }
                }
            }
            catch { }
            return targets.Distinct();
        }

        public void Dispose()
        {
            if (stopped) return;
            stopped = true;
            pinger.Dispose();
            try
            {
                var bye = CoPlayWire.Packet(CoPlayWire.Bye, 0);
                udp.Send(bye, bye.Length, Host);
            }
            catch { }
            udp.Close();
            CoPlayLog.Write("guest: left");
        }
    }

    // Hidden test switches, so a host and a guest can run on one PC next to the normal app:
    //   BlueAux.exe --coplay-host        a host with no window (logs to coplay-host.log)
    //   BlueAux.exe --coplay-guest [ip]  a guest with no window (coplay-guest.log); joins the first host found
    static class CoPlayDebug
    {
        public static int Run(string[] args)
        {
            if (args[0] == "--coplay-host")
            {
                CoPlayLog.Init("coplay-host.log");
                bool phone = args.Length > 1 && args[1] == "phone";
                CoPlayLog.Write("--- debug host started, sending {0}", phone ? "the phone's audio" : "a test beep every second");
                using (var host = new CoPlayHost(Environment.MachineName + " (test)"))
                {
                    AudioSink send = host.SendAudio;
                    var capture = phone ? new PhoneCapture(send) : null;
                    var tone = phone ? null : new ToneSource(send);
                    while (true)
                    {
                        Thread.Sleep(2000);
                        // With no window there's no phone watcher: take whichever phone stream Windows has active.
                        if (capture != null) capture.InputId = SoundCheck.InputIdOf(SoundCheck.Read(Guid.Empty, null).StreamId);
                        CoPlayLog.Write("host: {0} guest(s) [{1}], {2} packets sent{3}", host.GuestNames.Length, string.Join(", ", host.GuestNames),
                            host.PacketsSent, capture != null ? ", phone input " + (capture.InputId ?? "not found") : "");
                    }
                }
            }
            if (args[0] == "--coplay-guest") return RunGuest(args.Length > 1 ? args[1] : null);
            return 1;
        }

        static int RunGuest(string hostIp)
        {
            CoPlayLog.Init("coplay-guest.log");
            CoPlayLog.Write("--- debug guest started");
            while (true)
            {
                IPEndPoint host = null;
                string name = hostIp;
                if (hostIp != null) host = new IPEndPoint(IPAddress.Parse(hostIp), CoPlayWire.Port);
                else
                {
                    var found = CoPlayGuest.Find(1500);
                    CoPlayLog.Write("guest: found {0} host(s){1}", found.Count, found.Count > 0 ? ": " + string.Join(", ", found.Select(f => f.Key + " at " + f.Value)) : "");
                    if (found.Count == 0) continue;
                    host = found[0].Value;
                    name = found[0].Key;
                }

                var left = new ManualResetEvent(false);
                using (var guest = new CoPlayGuest(host, name))
                {
                    float peak = 0;
                    guest.Audio += (time, pcm, offset, frames) => peak = Math.Max(peak, Wasapi.Peak(pcm, offset, frames));
                    guest.HostLeft += () => left.Set();
                    while (!left.WaitOne(2000))
                    {
                        CoPlayLog.Write("guest: {0}, clock offset {1:0.000} ms, round trip {2:0.000} ms, {3} audio packets, peak {4:0.00}",
                            guest.Answering ? "host answering" : "NO ANSWER from host", guest.Offset / (double)HostClock.Ms,
                            guest.RoundTrip / (double)HostClock.Ms, guest.AudioPackets, peak);
                        peak = 0;
                    }
                }
                if (hostIp != null) return 0;
            }
        }
    }
}
