# CoPlay: one phone, several PCs, same moment

## Goal

One phone plays through one PC (the **host**) as BlueAux works today. Other PCs running BlueAux on the same
network (**guests**) play the same audio at the same moment, so 3 laptops in a room sound like one system.

## Fixed decisions

- An iPhone sends Bluetooth audio (A2DP) to one receiver only, so the audio travels host → guests over the
  local network (same router / Wi-Fi), not Bluetooth.
- Host and guests are the same `BlueAux.exe`.

## Design

### Where the host gets the audio

Windows plays a phone through a system stream that copies the phone's hidden input device
(`{0.0.1.00000000}.{guid}`, "Line/Microphone (<phone> A2DP SNK)") to the speakers. `SoundWatch` already finds
that stream every second; its ID ends in the input device's GUID (`SoundCheck.InputIdOf`). The host opens its
**own shared-mode WASAPI capture** on that input device. Shared mode lets several readers share it, so Windows'
own copy keeps working.

The capture asks for 48 kHz, 16-bit stereo and lets Windows convert (`AUTOCONVERTPCM`), so every PC uses one
fixed wire format and no codec: 192 kB/s ≈ 1.5 Mbit/s per guest.

WASAPI loopback of the speakers was the fallback, but it can't work here: the host plays the audio itself
(next section), so loopback would capture its own output and feed back. Not built.

### How the host stays in sync

Windows plays the phone the instant it arrives, and nothing can delay that path. So while hosting, the host
**mutes Windows' phone stream** (its Volume Mixer entry, the same call `SoundWatch` already uses for its
mute/unmute nudge) and plays the captured audio itself through the same delayed player as the guests.
- The mute is re-checked every second (the phone can reconnect, or the nudge can unmute it).
- It's undone when hosting stops. In case the app dies while hosting, the muted stream's ID is saved in
  `settings.xml` and unmuted at the next start.
- While CoPlay has the stream muted, `SoundCheck.Read` reports it as unmuted with the phone's input level as
  its output, so the sound watcher doesn't mistake CoPlay's mute for a broken connection.

### Network

UDP only, port **47101** on the host. Every packet starts `B X <version> <type>`; anything else is ignored.

| Type | From → to | Body |
|---|---|---|
| Discover | guest → broadcast | — |
| HostHere | host → guest | host PC name |
| Ping | guest → host, every 0.5 s | guest send time, guest PC name |
| Pong | host → guest | guest send time, host time |
| Audio | host → each guest | host time of the first frame, up to 240 frames of PCM (960 bytes, below one Wi-Fi packet) |
| Bye | either way | — |

- **Discovery:** the guest broadcasts Discover to 255.255.255.255 and to each network card's own broadcast
  address; hosts answer with their PC name. The guest picks a host from a list. No join code: anyone on the
  same network can join (fine for a home or a room of friends; a code can be added later).
- **Joining** is just pinging: a host treats every PC that pings it as a guest and drops it after 5 silent
  seconds. Pings double as clock sync.
- **Firewall:** Windows asks the first time the host listens. The guest's packets are replies to its own
  pings, which Windows lets through. If the guest gets no answers for 4 s, both sides show a hint to allow
  BlueAux through Windows Firewall (private networks).

### Clocks

Times are in 100 ns units of the PC's performance counter (QPC), which is also what WASAPI stamps captured
audio with, so the host's timestamps come straight from the capture (`qpcPosition`).

Each guest estimates `offset = hostTime - localTime` NTP-style from its pings: `offset = t2 - (t1 + t3) / 2`,
using the ping with the shortest round trip among the last 20 (the least delayed by Wi-Fi).

### Playing at the same moment (host and guests alike)

Every PC plays audio stamped host time *T* when its own speakers reach host time *T + delay + adjust*:
- **delay** is fixed at 300 ms: room for Wi-Fi hiccups and re-sent packets.
- **adjust** is per PC (−300…+300 ms, the "Sync" setting) for speakers that lag on their own (Bluetooth
  speakers, some USB DACs). Default 0.

The player is a shared-mode WASAPI render on the default speakers (48 kHz 16-bit, Windows converts). Each
time Windows asks for more audio, it works out which host time should go out next:
`target = toHost(now + queued-in-Windows + stream latency) - delay - adjust`, and compares it with where its
read position is:
- more than 50 ms off (start, a gap, a jump): jump straight to the target;
- 2–50 ms off (clocks drifting apart): repeat or skip one frame in every 250 until it's back. That's a 0.4 %
  speed change, inaudible, and it outruns any real clock drift (~0.01 %);
- otherwise, play straight on.

Missing packets play as silence. No re-sending (not needed on a normal Wi-Fi; noted as a limit).

### UI

A **CoPlay** card at the bottom of the main page, in the existing card style:
- Off: **Host** and **Join** buttons.
- Host: "Playing to N PCs", the list of PC names, **Stop**, firewall hint while nobody has joined.
- Join: list of hosts found (refreshes every 3 s), each with **Join**; then "Playing with <host>" and **Stop**.
- Host or joined: the **Sync** row, −/+ in 10 ms steps, the same control as "Rest after N min".

CoPlay is off at every start (it doesn't start by itself).

### Testing on one PC

Hidden switches:
- `--coplay-guest [host-ip]`: a headless guest without the window or the single-instance lock, logging to
  `%AppData%\BlueAux\coplay-guest.log`. Joins the first host found (or the given address).
- `--coplay-host`: starts hosting when the app starts.
- `--coplay-tone`: the host sends a short beep at each whole second of its own clock instead of the phone.
  Every player logs when each beep reaches its speakers (in host time), so host and guest can be compared.

Logs: `coplay.log` (in-app host or guest) and `coplay-guest.log`, once every 2 s: level, offset, round trip,
queued audio, sync error, jumps and corrections.

## Not in this version

Join code / pairing, re-sending lost packets, a codec, per-guest volume, the song display on guests, hosting
with no phone (any PC audio), IPv6.
