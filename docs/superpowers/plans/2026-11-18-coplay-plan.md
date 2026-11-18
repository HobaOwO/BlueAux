# CoPlay plan

Design: `docs/superpowers/specs/2026-11-18-coplay-design.md`. Each phase builds with `build.cmd` and is
committed on branch `coplay`.

1. **(a) LAN discovery and connection** — `src/CoPlay.cs`: wire format, `CoPlayHost` (UDP 47101: answers
   Discover, Ping → Pong, keeps the guest list), `CoPlayGuest` (finds hosts, pings, clock offset),
   `--coplay-guest` / `--coplay-host` switches in `App.cs`, `coplay.log`.
   Check: host + headless guest on this PC; guest log shows the host found, offset ≈ 0, round trip < 1 ms.
2. **(b) Audio capture and streaming** — `src/CoPlayAudio.cs`: WASAPI interop, `PhoneCapture` on the
   phone's input device (ID from `SoundWatch`), `ToneSource` (`--coplay-tone`), host sends Audio packets to
   guests. Check: guest log counts packets and level.
3. **(c) Synced playback** — `CoPlayPlayer` (render, target time, jump / repeat / skip), used by guests and
   by the host itself; host mutes Windows' phone stream, unmutes on stop, unmutes a leftover at start;
   `SoundCheck.Read` override. Check: with the tone, host and guest log each beep reaching the speakers at
   the same host time (±2 ms).
4. **(d) UI** — CoPlay card on the main page, `MainViewModel` properties and commands, Sync setting in
   `settings.xml`. Check: Host / Join / Stop from the window.
5. Test notes: `docs/coplay-testing.md` (what was verified here, what needs 2 real PCs, how to try it).
