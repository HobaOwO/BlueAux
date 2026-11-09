using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace BlueAux
{
    public enum PhonePlayback { Unknown, Paused, Playing }

    // iPhones and iPads tell paired accessories what they're playing, over Bluetooth LE (Apple's "media
    // service", the one car stereos and smartwatches use): the moment you press play or pause, plus the
    // song and the app. The sound meters can't tell a paused phone from a broken connection (both are
    // silent), so this is what lets BlueAux notice "playing, but no sound arrives" and fix it right then.
    //
    // It uses the phone's Bluetooth LE pairing, which Windows normally adds next to the audio one (same
    // name). Windows puts both in the same "container", which is how we match them. No LE pairing (or an
    // Android phone): nothing happens here and the rest of BlueAux works as before. We never pair anything.
    // Everything here runs on the UI thread; Windows callbacks are posted back to it.
    public sealed class PhoneMedia
    {
        static readonly Guid MediaService = new Guid("89D3502B-0F36-433A-8EF4-C502AD55F8DC");
        static readonly Guid RemoteCommandId = new Guid("9B3C81D8-57B1-4A8A-B8DF-0E56F7CA51C2");
        static readonly Guid EntityUpdateId = new Guid("2F7CABCE-808D-411F-9A0C-BB92BA96C102");
        const string ContainerProperty = "System.Devices.Aep.ContainerId";
        const string AddressProperty = "System.Devices.Aep.DeviceAddress";
        public const byte PlayCommand = 0;
        // Right after BlueAux was closed abruptly, Windows can refuse access for about half a minute.
        static readonly int[] RetrySeconds = { 10, 10, 15, 30, 60, 120, 300 };
        static readonly TimeSpan Linger = TimeSpan.FromSeconds(20);
        static readonly TimeSpan TrackSettle = TimeSpan.FromSeconds(1.5);

        readonly ReceiverEngine engine;
        DeviceSession session;
        int generation;
        bool opening;
        bool ready;
        BluetoothLEDevice device;
        GattDeviceService service;
        GattCharacteristic remote;
        // Windows stops delivering updates once nothing references this object, so we hold on to it.
        GattCharacteristic updates;
        byte[] supported;
        int failures;
        DateTime nextTry;
        DateTime goneSince = DateTime.MaxValue;
        string noPairingFor;
        DateTime noPairingUntil;
        string loggedFor;
        string pairingFor;
        ulong pairingAddress;
        PhonePlayback playback;
        DateTime playbackChangedAt = DateTime.MinValue;
        string app;
        // As the phone reports them, and as shown: a song change arrives as several updates (and iOS
        // sometimes sends a placeholder for half a second), so we show them once they stop changing.
        string title, artist, shownTitle, shownArtist;
        DateTime trackChangedAt;
        // The phone's media volume as it reports it; the first report after subscribing isn't a change.
        string volume;
        DateTime volumeChangedAt = DateTime.MinValue;

        public PhoneMedia(ReceiverEngine engine)
        {
            this.engine = engine;
        }

        public event Action Changed;

        // Where notes for sound-check.log go.
        public Action<string> Diag { get; set; }

        public DateTime PlaybackChangedAt { get { return playbackChangedAt; } }

        // When someone last pressed a volume button on this phone (MinValue if not since we started
        // reading it). It works whatever app is playing, so it's our sign that you're listening.
        public DateTime VolumeChangedAt(DeviceSession phone)
        {
            return ready && phone != null && phone == session ? volumeChangedAt : DateTime.MinValue;
        }

        // The phone's Bluetooth LE address, once we've found its LE pairing (0 if none / not yet).
        public ulong LeAddressOf(DeviceSession phone)
        {
            return phone != null && phone.DeviceId == pairingFor ? pairingAddress : 0;
        }

        public PhonePlayback PlaybackOf(DeviceSession phone)
        {
            return ready && phone != null && phone == session ? playback : PhonePlayback.Unknown;
        }

        // The song this phone is on, or null when we don't know it.
        public string TitleOf(DeviceSession phone)
        {
            return ready && phone != null && phone == session && !string.IsNullOrEmpty(shownTitle) ? shownTitle : null;
        }

        public string ArtistOf(DeviceSession phone)
        {
            return TitleOf(phone) != null && !string.IsNullOrEmpty(shownArtist) ? shownArtist : null;
        }

        // Presses a button on the phone (PlayCommand, ...). False when it can't.
        public bool Send(DeviceSession phone, byte command)
        {
            if (!ready || phone != session || remote == null) return false;
            if (supported != null && Array.IndexOf(supported, command) < 0) return false;
            SendAsync(remote, command, phone.DisplayName);
            return true;
        }

        // Follows the phone that's playing through the PC. Keeps reading through a quick reconnect and
        // lets go once the phone has been gone for a little while.
        public void Tick(DeviceSession active, DateTime now)
        {
            if (active != null && active != session)
            {
                if (session != null) Close();
                session = active;
                failures = 0;
                nextTry = now;
            }
            if (session == null) return;
            if (active == null)
            {
                if (goneSince == DateTime.MaxValue) goneSince = now;
                else if (now - goneSince > Linger)
                {
                    Close();
                    session = null;
                    return;
                }
            }
            else goneSince = DateTime.MaxValue;

            if (!ready && !opening && active != null && now >= nextTry) Open(session, now);
            CommitTrack(now);
        }

        public void Shutdown()
        {
            Close();
            session = null;
        }

        async void Open(DeviceSession target, DateTime now)
        {
            int gen = ++generation;
            opening = true;
            string step = "finding its Bluetooth LE pairing";
            BluetoothLEDevice dev = null;
            GattDeviceService svc = null;
            try
            {
                if (noPairingFor == target.DeviceId && now < noPairingUntil)
                {
                    nextTry = noPairingUntil;
                    return;
                }
                string classicId = target.BluetoothDeviceId;
                if (classicId == null)
                {
                    // Still looking up the phone's Bluetooth details.
                    nextTry = now.AddSeconds(3);
                    return;
                }
                ulong address = await FindLePairing(classicId);
                if (gen != generation) return;
                if (address == 0)
                {
                    noPairingFor = target.DeviceId;
                    noPairingUntil = DateTime.UtcNow.AddMinutes(10);
                    nextTry = noPairingUntil;
                    Note(target.DisplayName + ": no Bluetooth LE pairing, so it can't tell us when it plays (normal for Android phones)");
                    return;
                }
                pairingFor = target.DeviceId;
                pairingAddress = address;

                step = "connecting over Bluetooth LE";
                dev = await Within(BluetoothLEDevice.FromBluetoothAddressAsync(address), 20);
                if (gen != generation) return;
                if (dev == null) throw new Exception("Windows doesn't know it");

                step = "finding its media service";
                svc = await FindService(dev, gen);
                if (gen != generation) return;
                if (svc == null) throw new Exception("the phone didn't offer it");
                try { svc.Session.MaintainConnection = true; }
                catch { }

                step = "subscribing";
                var commands = await Characteristic(svc, RemoteCommandId);
                var news = await Characteristic(svc, EntityUpdateId);
                if (gen != generation) return;
                remote = commands;
                updates = news;
                news.ValueChanged += (c, e) =>
                {
                    var value = Bytes(e.CharacteristicValue);
                    engine.Post(() => { if (gen == generation) OnUpdate(value); });
                };
                commands.ValueChanged += (c, e) =>
                {
                    var value = Bytes(e.CharacteristicValue);
                    engine.Post(() => { if (gen == generation) supported = value; });
                };
                await Subscribe(news);
                await Subscribe(commands);
                // Player: app, playback state, volume. Track: artist, album, title, duration.
                await Write(news, new byte[] { 0, 0, 1, 2 });
                await Write(news, new byte[] { 2, 0, 1, 2, 3 });
                if (gen != generation) return;

                var link = dev;
                link.ConnectionStatusChanged += (d, o) =>
                {
                    BluetoothConnectionStatus status;
                    try { status = d.ConnectionStatus; }
                    catch { return; }
                    engine.Post(() => { if (gen == generation) OnLinkChanged(status); });
                };
                device = dev;
                service = svc;
                dev = null;
                svc = null;
                ready = true;
                failures = 0;
                if (loggedFor != target.DeviceId)
                {
                    loggedFor = target.DeviceId;
                    Log.Info("{0}: can see when it plays or pauses", target.DisplayName);
                }
                Note(target.DisplayName + ": reading what's playing over Bluetooth LE");
                Raise();
                if (link.ConnectionStatus != BluetoothConnectionStatus.Connected) OnLinkChanged(link.ConnectionStatus);
            }
            catch (Exception ex)
            {
                if (gen != generation) return;
                int wait = RetrySeconds[Math.Min(failures, RetrySeconds.Length - 1)];
                failures++;
                nextTry = DateTime.UtcNow.AddSeconds(wait);
                Note(string.Format("{0}: couldn't read what's playing ({1}: {2}); trying again in {3} s", target.DisplayName, step, ex.Message, wait));
            }
            finally
            {
                Dispose(svc, dev);
                if (gen == generation) opening = false;
            }
        }

        // The LE link dropped (out of range, Bluetooth toggled). The phone forgets our subscription,
        // so start over.
        void OnLinkChanged(BluetoothConnectionStatus status)
        {
            if (!ready || status == BluetoothConnectionStatus.Connected) return;
            Note((session != null ? session.DisplayName : "phone") + ": Bluetooth LE link dropped; connecting it again");
            Close();
            nextTry = DateTime.UtcNow.AddSeconds(3);
        }

        void OnUpdate(byte[] data)
        {
            if (data.Length < 3) return;
            // [entity, attribute, flags, UTF-8 value]
            string value = Encoding.UTF8.GetString(data, 3, data.Length - 3);
            var now = DateTime.UtcNow;
            if (data[0] == 0 && data[1] == 0)
            {
                if (app != value)
                {
                    app = value;
                    Note((session != null ? session.DisplayName : "phone") + ": playing from " + (value.Length > 0 ? value : "no app"));
                }
            }
            else if (data[0] == 0 && data[1] == 1)
            {
                // "state,rate,elapsed": 0 paused, 1 playing, 2 rewinding, 3 fast-forwarding.
                int comma = value.IndexOf(',');
                string state = comma >= 0 ? value.Substring(0, comma) : value;
                var next = state == "1" ? PhonePlayback.Playing : PhonePlayback.Paused;
                if (next != playback)
                {
                    playback = next;
                    playbackChangedAt = now;
                    Raise();
                }
            }
            else if (data[0] == 0 && data[1] == 2)
            {
                // A number from 0 to 1, as text.
                if (volume != null && value != volume)
                {
                    volumeChangedAt = now;
                    Note((session != null ? session.DisplayName : "phone") + ": volume changed to " + value);
                }
                volume = value;
            }
            else if (data[0] == 2 && data[1] == 0) SetTrack(title, value, now);
            else if (data[0] == 2 && data[1] == 2) SetTrack(value, artist, now);
        }

        void SetTrack(string newTitle, string newArtist, DateTime now)
        {
            if (newTitle == title && newArtist == artist) return;
            title = newTitle;
            artist = newArtist;
            trackChangedAt = now;
        }

        void CommitTrack(DateTime now)
        {
            if (title == shownTitle && artist == shownArtist) return;
            if (now - trackChangedAt < TrackSettle) return;
            shownTitle = title;
            shownArtist = artist;
            Raise();
        }

        void Close()
        {
            generation++;
            opening = false;
            bool hadInfo = ready || playback != PhonePlayback.Unknown || shownTitle != null;
            ready = false;
            remote = null;
            updates = null;
            supported = null;
            var svc = service;
            var dev = device;
            service = null;
            device = null;
            Dispose(svc, dev);
            playback = PhonePlayback.Unknown;
            app = title = artist = shownTitle = shownArtist = null;
            volume = null;
            volumeChangedAt = DateTime.MinValue;
            if (hadInfo) Raise();
        }

        static void Dispose(GattDeviceService svc, BluetoothLEDevice dev)
        {
            if (svc != null)
            {
                try
                {
                    svc.Session.MaintainConnection = false;
                    svc.Session.Dispose();
                }
                catch { }
                try { svc.Dispose(); }
                catch { }
            }
            if (dev != null)
            {
                try { dev.Dispose(); }
                catch { }
            }
        }

        // The audio pairing and the LE pairing are separate entries in Windows, in the same container.
        static async Task<ulong> FindLePairing(string classicId)
        {
            var props = new[] { ContainerProperty, AddressProperty };
            var classic = await Within(DeviceInformation.CreateFromIdAsync(classicId, props), 15);
            object container;
            if (classic == null || !classic.Properties.TryGetValue(ContainerProperty, out container) || container == null) return 0;
            var paired = await Within(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), props), 15);
            foreach (var info in paired)
            {
                object other, address;
                if (!info.Properties.TryGetValue(ContainerProperty, out other) || !Equals(other, container)) continue;
                if (!info.Properties.TryGetValue(AddressProperty, out address) || address == null) continue;
                ulong raw;
                string hex = address.ToString().Replace(":", "").Replace("-", "");
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw) && raw != 0) return raw;
            }
            return 0;
        }

        // iOS only shows the media service on an encrypted link, and Windows only encrypts once something
        // protected is asked for, so the first look right after connecting often finds nothing (or fails).
        // Look again, then ask for Windows' remembered copy, which also makes it encrypt.
        async Task<GattDeviceService> FindService(BluetoothLEDevice dev, int gen)
        {
            for (int attempt = 1; attempt <= 5 && gen == generation; attempt++)
            {
                var mode = attempt <= 3 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
                try
                {
                    var result = await Within(dev.GetGattServicesForUuidAsync(MediaService, mode), 30);
                    if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
                    {
                        for (int i = 1; i < result.Services.Count; i++) Dispose(result.Services[i], null);
                        return result.Services[0];
                    }
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch
                {
                }
                await Task.Delay(2000);
            }
            return null;
        }

        static async Task<GattCharacteristic> Characteristic(GattDeviceService svc, Guid id)
        {
            var result = await Within(svc.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached), 15);
            if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
                throw new Exception("part of the service is missing (" + result.Status + ")");
            return result.Characteristics[0];
        }

        static async Task Subscribe(GattCharacteristic characteristic)
        {
            var result = await Within(characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify), 15);
            if (result.Status != GattCommunicationStatus.Success) throw new Exception("the phone refused updates (" + result.Status + ")");
        }

        static async Task Write(GattCharacteristic characteristic, byte[] value)
        {
            var result = await Within(characteristic.WriteValueWithResultAsync(
                CryptographicBuffer.CreateFromByteArray(value), GattWriteOption.WriteWithResponse), 15);
            if (result.Status != GattCommunicationStatus.Success) throw new Exception("the phone refused the request (" + result.Status + ")");
        }

        async void SendAsync(GattCharacteristic characteristic, byte command, string name)
        {
            try
            {
                await Write(characteristic, new[] { command });
            }
            catch (Exception ex)
            {
                Note(name + ": couldn't press the button on the phone: " + ex.Message);
            }
        }

        // Windows calls normally answer, but a phone that walks away mid-call can leave one hanging.
        static async Task<T> Within<T>(IAsyncOperation<T> operation, int seconds)
        {
            var task = operation.AsTask();
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task)
            {
                try { operation.Cancel(); }
                catch { }
                throw new TimeoutException("no answer in " + seconds + " s");
            }
            return await task;
        }

        static byte[] Bytes(IBuffer buffer)
        {
            if (buffer == null || buffer.Length == 0) return new byte[0];
            byte[] value;
            CryptographicBuffer.CopyToByteArray(buffer, out value);
            return value;
        }

        void Note(string text)
        {
            var handler = Diag;
            if (handler != null) handler(text);
        }

        void Raise()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
