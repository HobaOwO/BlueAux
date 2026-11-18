using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Security.Cryptography;

namespace BlueAux
{
    public enum SetupStep { Idle, Looking, Pairing, Done, Failed }

    // Seeing what an iPhone plays (PhoneMedia) needs the phone's Bluetooth LE pairing as well as the audio
    // one. Some PCs make both when you pair; many only make the audio one. This makes the LE one, once,
    // when you ask: it looks for the iPhone held next to the PC (iPhones keep sending short connectable
    // broadcasts, "Nearby Info"), connects, asks for the media service's updates (which needs pairing),
    // then pairs; the iPhone shows a Pair prompt. Then it remembers the new LE pairing's address.
    // Everything here runs on the UI thread; Windows callbacks are posted back to it.
    public sealed class PhoneSetup
    {
        const ushort AppleCompany = 0x004C;
        const byte NearbyInfo = 0x10;
        // A phone held next to the PC is usually around -40 to -65 dBm.
        const short NearEnough = -80;
        static readonly TimeSpan LookFor = TimeSpan.FromSeconds(6);

        readonly ReceiverEngine engine;
        readonly PhoneMedia media;
        SetupStep step;
        string message;
        DeviceSession phone;
        int generation;
        BluetoothLEAdvertisementWatcher watcher;

        public PhoneSetup(ReceiverEngine engine, PhoneMedia media)
        {
            this.engine = engine;
            this.media = media;
        }

        public event Action Changed;

        // Where notes for sound-check.log go.
        public Action<string> Diag { get; set; }

        public SetupStep Step { get { return step; } }
        public string Message { get { return message; } }
        public DeviceSession Phone { get { return phone; } }
        public bool IsBusy { get { return step == SetupStep.Looking || step == SetupStep.Pairing; } }

        public void Reset()
        {
            if (IsBusy) return;
            Set(SetupStep.Idle, null);
            phone = null;
        }

        public void Shutdown()
        {
            generation++;
            StopWatcher();
        }

        public async void Start(DeviceSession target)
        {
            if (IsBusy || target == null) return;
            int gen = ++generation;
            phone = target;
            await PairFromPc(target, gen);
            // Many PCs, and iPhones older than the 6s, can't get the LE pairing that way (see PairFromPhone).
            if (gen == generation && step == SetupStep.Failed) await PairFromPhone(target, gen);
        }

        // The PC reaches out to the iPhone held next to it and asks it to pair.
        async Task PairFromPc(DeviceSession target, int gen)
        {
            Set(SetupStep.Looking, "Hold your unlocked iPhone next to this PC…");
            Note(target.DisplayName + ": setting up song display; looking for the iPhone next to the PC");
            BluetoothLEDevice dev = null;
            GattSession session = null;
            try
            {
                var before = await PairedLeAddresses();
                if (gen != generation) return;

                var heard = await FindNearest(before);
                if (gen != generation) return;
                Note(string.Format("{0}: heard {1} unpaired iPhone-like device(s){2}", target.DisplayName,
                    heard.Devices, heard.Devices > 0 ? "; strongest " + heard.Signal + " dBm" : ""));
                if (heard.Address == 0 || heard.Signal < NearEnough)
                {
                    Fail("Not found. Hold your unlocked iPhone close.", "no iPhone close enough");
                    return;
                }
                ulong candidate = heard.Address;
                string name = target.DisplayName;

                Set(SetupStep.Pairing, "Tap Pair on your iPhone");
                dev = await PhoneMedia.Within(BluetoothLEDevice.FromBluetoothAddressAsync(candidate, BluetoothAddressType.Random), 20);
                if (gen != generation) return;
                if (dev == null)
                {
                    Fail("Couldn't reach it. Keep it unlocked and close.", "Windows couldn't open it");
                    return;
                }
                Note(string.Format("{0}: connecting to {1:X12} (Windows can pair it: {2})", name, candidate, dev.DeviceInformation.Pairing.CanPair));
                session = await PhoneMedia.Within(GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId), 20);
                session.MaintainConnection = true;
                if (gen != generation) return;

                // iPhones turn down a pairing request out of the blue: Windows says "Failed" within seconds
                // and no prompt shows. Car stereos and watches go the other way round: they connect and ask
                // for the media service's updates, the iPhone answers that this needs pairing, and the pairing
                // that follows gets the Pair prompt. So ask for the updates first.
                await Describe(dev, name);
                if (gen != generation) return;
                bool pairedAlready = await AskForUpdates(dev, name);
                if (gen != generation) return;

                var status = DevicePairingResultStatus.AlreadyPaired;
                if (!pairedAlready)
                {
                    Note(name + ": asking it to pair");
                    status = await Pair(dev, gen, DevicePairingProtectionLevel.Default);
                    if (gen != generation) return;
                    if (status == DevicePairingResultStatus.Failed)
                    {
                        Note(name + ": pairing Failed; trying once more asking for encryption");
                        status = await Pair(dev, gen, DevicePairingProtectionLevel.Encryption);
                        if (gen != generation) return;
                    }
                }
                if (status != DevicePairingResultStatus.Paired && status != DevicePairingResultStatus.AlreadyPaired)
                {
                    Fail(status == DevicePairingResultStatus.RejectedByHandler || status == DevicePairingResultStatus.AuthenticationTimeout
                        || status == DevicePairingResultStatus.ConnectionRejected
                        ? "Didn't pair. Tap Pair on the iPhone when asked."
                        : "Didn't pair.", "pairing " + status);
                    return;
                }
                Note(target.DisplayName + ": paired over Bluetooth LE (" + status + ")");

                // Pairing gives Windows the phone's lasting address (its broadcasts use changing ones).
                ulong address = await NewPairing(before, gen);
                if (gen != generation) return;
                if (address == 0) address = dev.BluetoothAddress;

                if (!await HasMediaService(dev, gen))
                {
                    if (gen != generation) return;
                    Note(target.DisplayName + ": the paired device doesn't share what it plays; unpairing it again");
                    try { await PhoneMedia.Within(dev.DeviceInformation.Pairing.UnpairAsync(), 15); }
                    catch { }
                    Fail("That wasn't an iPhone. Hold only yours close.", "no media service");
                    return;
                }
                if (gen != generation) return;

                target.LeAddress = address;
                Note(string.Format("{0}: song display set up; its Bluetooth LE address is {1:X12}", target.DisplayName, address));
                Log.Info("{0}: set up, it can now show what it plays", target.DisplayName);
                Set(SetupStep.Done, null);
                media.Retry(target);
            }
            catch (Exception ex)
            {
                if (gen != generation) return;
                Fail("Something went wrong. Try again.", ex.Message);
            }
            finally
            {
                StopWatcher();
                if (session != null)
                {
                    try
                    {
                        session.MaintainConnection = false;
                        session.Dispose();
                    }
                    catch { }
                }
                if (dev != null)
                {
                    try { dev.Dispose(); }
                    catch { }
                }
            }
        }

        // The other way round: the PC shows up in the iPhone's own Bluetooth settings as a media remote
        // (Bluetooth LE HID with only play, pause and volume keys, like a selfie remote), and you pair it
        // from there. Windows only adds the LE pairing next to the audio one by itself when both the PC's
        // adapter and the iPhone support LE Secure Connections (not on many PCs, nor on an iPhone 6), and
        // iPhones turn down a pairing the PC starts out of the blue; one the iPhone starts always works.
        async Task PairFromPhone(DeviceSession target, int gen)
        {
            BluetoothAdapter adapter = null;
            try { adapter = await BluetoothAdapter.GetDefaultAsync(); }
            catch { }
            if (adapter == null || !adapter.IsPeripheralRoleSupported)
            {
                Note(target.DisplayName + ": this PC's Bluetooth can't act as an LE accessory, so it can't be paired from the iPhone either");
                return;
            }
            GattServiceProvider remote = null;
            BluetoothLEDevice dev = null;
            try
            {
                var before = await PairedLeAddresses();
                remote = await CreateMediaRemote();
                if (gen != generation) return;
                if (remote == null)
                {
                    Note(target.DisplayName + ": couldn't offer this PC as a media remote");
                    return;
                }
                remote.AdvertisementStatusChanged += (p, e) => Note("media remote advertising: " + e.Status + (e.Error != BluetoothError.Success ? " (" + e.Error + ")" : ""));
                remote.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
                string pc = Environment.MachineName;
                Set(SetupStep.Pairing, "On your iPhone, open Settings > Bluetooth and tap " + pc + ", then Pair (on both)");
                Note(target.DisplayName + ": offering this PC (" + pc + ") in the iPhone's Bluetooth settings for 2 minutes");
                for (int i = 0; i < 120; i++)
                {
                    await Task.Delay(1000);
                    if (gen != generation) return;
                    ulong address = 0;
                    foreach (ulong a in await PairedLeAddresses())
                        if (!before.Contains(a)) address = a;
                    if (gen != generation) return;
                    if (address == 0) continue;
                    Note(string.Format("{0}: something paired from its side ({1:X12}); checking it's the iPhone", target.DisplayName, address));
                    dev = await PhoneMedia.Within(BluetoothLEDevice.FromBluetoothAddressAsync(address), 20);
                    if (gen != generation) return;
                    if (dev == null || !await HasMediaService(dev, gen))
                    {
                        if (gen != generation) return;
                        Fail("That wasn't an iPhone. Pair this PC from the iPhone's Bluetooth settings.", "no media service on what paired");
                        return;
                    }
                    if (gen != generation) return;
                    target.LeAddress = address;
                    Note(string.Format("{0}: song display set up from the iPhone; its Bluetooth LE address is {1:X12}", target.DisplayName, address));
                    Log.Info("{0}: set up, it can now show what it plays", target.DisplayName);
                    Set(SetupStep.Done, null);
                    media.Retry(target);
                    return;
                }
                Fail("Didn't pair. On the iPhone: Settings > Bluetooth, tap " + pc + ".", "nothing paired from the iPhone in 2 minutes");
            }
            catch (Exception ex)
            {
                if (gen == generation) Fail("Something went wrong. Try again.", ex.Message);
            }
            finally
            {
                if (remote != null)
                {
                    try { remote.StopAdvertising(); }
                    catch { }
                }
                if (dev != null)
                {
                    try { dev.Dispose(); }
                    catch { }
                }
            }
        }

        static Guid Uuid16(int id)
        {
            return new Guid(string.Format("0000{0:X4}-0000-1000-8000-00805F9B34FB", id));
        }

        // A Bluetooth LE HID service with one consumer-control report (play/pause, next, previous, volume,
        // mute). It never sends a key; it exists so the iPhone lists the PC and pairs it. Every part asks
        // for encryption, which is what makes the iPhone pair.
        static async Task<GattServiceProvider> CreateMediaRemote()
        {
            var created = await GattServiceProvider.CreateAsync(Uuid16(0x1812));
            if (created.Error != BluetoothError.Success) return null;
            var svc = created.ServiceProvider.Service;
            byte[] reportMap = { 0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x01, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x06,
                0x09, 0xCD, 0x09, 0xB5, 0x09, 0xB6, 0x09, 0xE9, 0x09, 0xEA, 0x09, 0xE2, 0x81, 0x02, 0x95, 0x02, 0x81, 0x01, 0xC0 };
            var read = GattCharacteristicProperties.Read;
            var write = GattCharacteristicProperties.WriteWithoutResponse;
            if (!await AddCharacteristic(svc, 0x2A4B, read, reportMap)) return null;                         // report map
            if (!await AddCharacteristic(svc, 0x2A4A, read, new byte[] { 0x11, 0x01, 0x00, 0x02 })) return null; // HID 1.11, normally connectable
            if (!await AddCharacteristic(svc, 0x2A4C, write, null)) return null;                               // control point
            if (!await AddCharacteristic(svc, 0x2A4E, read | write, new byte[] { 1 })) return null;            // protocol mode: report
            var report = await svc.CreateCharacteristicAsync(Uuid16(0x2A4D), Parameters(read | GattCharacteristicProperties.Notify, new byte[] { 0 }));
            if (report.Error != BluetoothError.Success) return null;
            var reference = await report.Characteristic.CreateDescriptorAsync(Uuid16(0x2908), new GattLocalDescriptorParameters
            {
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                StaticValue = CryptographicBuffer.CreateFromByteArray(new byte[] { 0x01, 0x01 })   // report 1, input
            });
            return reference.Error == BluetoothError.Success ? created.ServiceProvider : null;
        }

        static async Task<bool> AddCharacteristic(GattLocalService svc, int id, GattCharacteristicProperties properties, byte[] value)
        {
            var result = await svc.CreateCharacteristicAsync(Uuid16(id), Parameters(properties, value));
            return result.Error == BluetoothError.Success;
        }

        static GattLocalCharacteristicParameters Parameters(GattCharacteristicProperties properties, byte[] value)
        {
            var parameters = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = properties,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                WriteProtectionLevel = GattProtectionLevel.EncryptionRequired
            };
            if (value != null) parameters.StaticValue = CryptographicBuffer.CreateFromByteArray(value);
            return parameters;
        }

        sealed class Heard
        {
            public ulong Address;
            public short Signal = short.MinValue;
            public int Devices;
        }

        // Listens for a few seconds and picks the strongest iPhone broadcast that isn't from a device
        // this PC has already paired.
        async Task<Heard> FindNearest(HashSet<ulong> paired)
        {
            var best = new Dictionary<ulong, short>();
            var gate = new object();
            var w = new BluetoothLEAdvertisementWatcher();
            w.ScanningMode = BluetoothLEScanningMode.Passive;
            w.Received += (s, e) =>
            {
                if (e.AdvertisementType != BluetoothLEAdvertisementType.ConnectableUndirected) return;
                if (paired.Contains(e.BluetoothAddress) || !IsNearbyInfo(e.Advertisement)) return;
                lock (gate)
                {
                    short known;
                    if (!best.TryGetValue(e.BluetoothAddress, out known) || e.RawSignalStrengthInDBm > known)
                        best[e.BluetoothAddress] = e.RawSignalStrengthInDBm;
                }
            };
            watcher = w;
            w.Start();
            await Task.Delay(LookFor);
            StopWatcher();
            var heard = new Heard();
            lock (gate)
            {
                heard.Devices = best.Count;
                foreach (var kv in best)
                {
                    if (kv.Value <= heard.Signal) continue;
                    heard.Signal = kv.Value;
                    heard.Address = kv.Key;
                }
            }
            return heard;
        }

        void StopWatcher()
        {
            var w = watcher;
            watcher = null;
            if (w == null) return;
            try { w.Stop(); }
            catch { }
        }

        static bool IsNearbyInfo(BluetoothLEAdvertisement advertisement)
        {
            try
            {
                foreach (var data in advertisement.ManufacturerData)
                {
                    if (data.CompanyId != AppleCompany || data.Data == null || data.Data.Length == 0) continue;
                    byte[] d;
                    CryptographicBuffer.CopyToByteArray(data.Data, out d);
                    for (int i = 0; i + 1 < d.Length; i += 2 + d[i + 1])
                        if (d[i] == NearbyInfo) return true;
                }
            }
            catch { }
            return false;
        }

        static readonly Guid DeviceInfoService = new Guid("0000180A-0000-1000-8000-00805F9B34FB");
        static readonly Guid ModelNumber = new Guid("00002A24-0000-1000-8000-00805F9B34FB");

        // For the log: what the device offers and what it says it is (an iPhone gives e.g. "iPhone15,2"),
        // so a failed setup shows whether it even reached the iPhone.
        async Task Describe(BluetoothLEDevice dev, string name)
        {
            try
            {
                var all = await PhoneMedia.Within(dev.GetGattServicesAsync(BluetoothCacheMode.Uncached), 20);
                var ids = new List<string>();
                string model = null;
                foreach (var svc in all.Services)
                {
                    string id = svc.Uuid.ToString("D").ToUpperInvariant();
                    ids.Add(id.EndsWith("-0000-1000-8000-00805F9B34FB") ? id.Substring(4, 4) : id.Substring(0, 8));
                    try
                    {
                        if (svc.Uuid == DeviceInfoService && model == null)
                        {
                            var chars = await PhoneMedia.Within(svc.GetCharacteristicsForUuidAsync(ModelNumber, BluetoothCacheMode.Uncached), 10);
                            if (chars.Status == GattCommunicationStatus.Success && chars.Characteristics.Count > 0)
                            {
                                var read = await PhoneMedia.Within(chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached), 10);
                                if (read.Status == GattCommunicationStatus.Success && read.Value != null)
                                    model = CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, read.Value);
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { svc.Dispose(); }
                        catch { }
                    }
                }
                Note(string.Format("{0}: it answered {1}{2}: {3}", name, all.Status,
                    model != null ? " as \"" + model + "\"" : "", ids.Count > 0 ? string.Join(" ", ids) : "no services"));
            }
            catch (Exception ex)
            {
                Note(name + ": couldn't list what it offers: " + ex.Message);
            }
        }

        // Asks for the media service's updates, the step that makes an iPhone want pairing, and notes
        // each answer. The iPhone may show its Pair prompt while this waits. True if that left it paired.
        async Task<bool> AskForUpdates(BluetoothLEDevice dev, string name)
        {
            try
            {
                var found = await PhoneMedia.Within(dev.GetGattServicesForUuidAsync(PhoneMedia.MediaService, BluetoothCacheMode.Uncached), 20);
                Note(string.Format("{0}: media service: {1}, {2} found", name, found.Status, found.Services.Count));
                if (found.Status == GattCommunicationStatus.Success && found.Services.Count > 0)
                {
                    try
                    {
                        var chars = await PhoneMedia.Within(found.Services[0].GetCharacteristicsForUuidAsync(PhoneMedia.EntityUpdateId, BluetoothCacheMode.Uncached), 15);
                        Note(string.Format("{0}: its update channel: {1}{2}", name, chars.Status, ErrorText(chars.ProtocolError)));
                        if (chars.Status == GattCommunicationStatus.Success && chars.Characteristics.Count > 0)
                        {
                            var asked = await PhoneMedia.Within(chars.Characteristics[0].WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.Notify), 60);
                            Note(string.Format("{0}: asked for updates: {1}{2}", name, asked.Status, ErrorText(asked.ProtocolError)));
                        }
                    }
                    finally
                    {
                        foreach (var svc in found.Services)
                        {
                            try { svc.Dispose(); }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Note(name + ": asking for updates didn't work: " + ex.Message);
            }
            bool paired = await IsPaired(dev);
            if (paired) Note(name + ": that paired it");
            return paired;
        }

        static string ErrorText(byte? protocolError)
        {
            // 0x05 insufficient authentication, 0x0F insufficient encryption: what an iPhone answers before pairing.
            return protocolError.HasValue ? " (error 0x" + protocolError.Value.ToString("X2") + ")" : "";
        }

        static async Task<bool> IsPaired(BluetoothLEDevice dev)
        {
            try
            {
                var info = await PhoneMedia.Within(DeviceInformation.CreateFromIdAsync(dev.DeviceId), 10);
                return info.Pairing.IsPaired;
            }
            catch
            {
                return false;
            }
        }

        async Task<DevicePairingResultStatus> Pair(BluetoothLEDevice dev, int gen, DevicePairingProtectionLevel level)
        {
            var custom = dev.DeviceInformation.Pairing.Custom;
            TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler = (s, e) =>
            {
                // You confirm on the iPhone (that's where the Pair prompt is); this side just agrees.
                string pin = e.Pin;
                e.Accept();
                if (!string.IsNullOrEmpty(pin))
                    engine.Post(() =>
                    {
                        if (gen == generation && step == SetupStep.Pairing) Set(SetupStep.Pairing, "Tap Pair on your iPhone (code " + pin + ")");
                    });
            };
            custom.PairingRequested += handler;
            try
            {
                var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin;
                var result = await PhoneMedia.Within(custom.PairAsync(kinds, level), 60);
                Note(string.Format("{0}: pairing ({1}): {2}", phone != null ? phone.DisplayName : "phone", level, result.Status));
                return result.Status;
            }
            finally
            {
                custom.PairingRequested -= handler;
            }
        }

        // The LE pairing that wasn't there before, as Windows lists it once pairing has finished.
        async Task<ulong> NewPairing(HashSet<ulong> before, int gen)
        {
            for (int i = 0; i < 10 && gen == generation; i++)
            {
                foreach (ulong address in await PairedLeAddresses())
                    if (!before.Contains(address)) return address;
                await Task.Delay(1000);
            }
            return 0;
        }

        static async Task<bool> HasMediaService(BluetoothLEDevice dev, int gen)
        {
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    var mode = attempt <= 3 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
                    var result = await PhoneMedia.Within(dev.GetGattServicesForUuidAsync(PhoneMedia.MediaService, mode), 20);
                    if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
                    {
                        foreach (var svc in result.Services)
                        {
                            try { svc.Dispose(); }
                            catch { }
                        }
                        return true;
                    }
                }
                catch (TimeoutException)
                {
                    return false;
                }
                catch
                {
                }
                await Task.Delay(2000);
            }
            return false;
        }

        internal static async Task<HashSet<ulong>> PairedLeAddresses()
        {
            var set = new HashSet<ulong>();
            var props = new[] { PhoneMedia.AddressProperty };
            var paired = await PhoneMedia.Within(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), props), 15);
            foreach (var info in paired)
            {
                object address;
                ulong raw;
                if (info.Properties.TryGetValue(PhoneMedia.AddressProperty, out address) && address != null
                    && ulong.TryParse(address.ToString().Replace(":", "").Replace("-", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw) && raw != 0)
                    set.Add(raw);
            }
            return set;
        }

        void Fail(string text, string why)
        {
            Note((phone != null ? phone.DisplayName : "phone") + ": song display setup didn't work (" + why + ")");
            Set(SetupStep.Failed, text);
        }

        void Set(SetupStep next, string text)
        {
            step = next;
            message = text;
            var handler = Changed;
            if (handler != null) handler();
        }

        void Note(string text)
        {
            var handler = Diag;
            if (handler != null) handler(text);
        }
    }
}
