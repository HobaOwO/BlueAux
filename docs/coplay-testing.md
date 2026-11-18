# CoPlay: what was tested, what wasn't, how to try it

Design: `docs/superpowers/specs/2026-11-18-coplay-design.md`. Plan: `docs/superpowers/plans/2026-11-18-coplay-plan.md`.

## Verified on one PC (2026-11-18)

All with the test switches, next to the normal BlueAux that kept running.

| What | How | Result |
|---|---|---|
| Finding a host, joining, leaving | `--coplay-host` + `--coplay-guest` | Found in ~1.5 s; host lists the guest; drops it 5 s after it's killed |
| Clock sync | guest log | offset ≈ 0.01 ms, round trip ≈ 0.1 ms (same PC) |
| Audio streaming | test beep | 200 packets/s arrive, beep level intact |
| **Sync, host vs guest** | each player logs when every beep leaves its speakers | both at +300.0…+300.4 ms: **within 0.3 ms of each other** |
| Sync with a wrong, drifting guest clock | `--coplay-guest skew` (7 s off, 0.02 % fast, ~20× worse than real PCs) | offset found; beeps stay within 0.4 ms of the host's |
| Sync adjust | +20 ms in the window | beeps moved from +300 to +318 ms, smoothly (no jump) |
| Phone capture opens | `--coplay-host phone` with the phone connected but paused | WASAPI capture opened on the phone's A2DP input without error; no audio, as nothing played |
| Window | `--ui-test`, driven by UI Automation | Join → host list → Join → "Playing along with …" → Sync −/+ → Stop; Host while the port was taken → "Couldn't host: …"; Host → "Hosting. On the other PCs…" |

"Leaves the speakers" is the player's own reckoning (how much Windows has buffered plus the stream latency
Windows reports). It proves the timing logic; it can't see the speakers' own delay.

## Settings switches (test build 4)

- **CoPlay** (General, off by default): shows the CoPlay card. Turning it off stops hosting or joining.
- **Update automatically** (General, on by default): checks GitHub's latest release (never pre-releases) a minute
  after start and every 6 h; downloads a newer `BlueAux.exe`, and swaps it in when nothing has played for 2 min,
  CoPlay is off and the window isn't in use, then restarts. Tested here with a copy pretending to be 1.4: it
  fetched v1.5, swapped itself (old one kept as `BlueAux.exe.old`, deleted next start), and the restarted copy
  waited for the old one to close. In a protected folder (Program Files) it can't swap and logs that instead.
  Only versions that have the updater update themselves: 1.5 users install 1.6 by hand once.

## Not tested yet

- **Two real PCs on Wi-Fi.** Discovery across a router, real clock offsets, packet loss and jitter, and how it
  sounds in a room. This is the real test.
- **Real phone audio through CoPlay**: capture of a playing phone, and the host muting Windows' own copy of it
  (and unmuting on Stop). The mute call is the one the sound watcher already uses, but not with CoPlay.
- **Windows Firewall prompt** on first Host (it didn't appear here: this PC already allows the app, or the
  test ran on loopback/link-local). The UI shows hints when nobody joins or the host doesn't answer.
- Crash recovery of the mute (unmute at the next start).
- Speakers that lag on their own (Bluetooth speakers, TVs): that's what "Sync this PC" is for; untested.
- Changing the default speakers while playing (the player reopens on the new ones; untested).

## Known limits

- Lost packets play as a few ms of silence; nothing is re-sent.
- Anyone on the same network can join a host (no join code).
- ~1.5 Mbit/s per guest. Fine for a few PCs on home Wi-Fi.
- The host plays the phone 300 ms late (that's the room for Wi-Fi). Pausing on the phone takes 300 ms to be heard.
- Windows' own copy of the phone stays muted while hosting; if BlueAux is killed while hosting, it unmutes at
  the next start (or unmute it in the Volume Mixer).

## Trying it with 2 PCs

1. Build `bin\BlueAux.exe` from this branch and put it on both PCs. Both on the **same Wi-Fi or network**.
2. **PC 1 (host):** connect the phone as usual. In the CoPlay card at the bottom of the main page, press
   **Host**. If Windows Firewall asks, tick **Private networks** and allow.
3. **PC 2 (guest):** press **Join**. PC 1's name appears within a few seconds; press its **Join**.
   The card says "Playing along with PC 1"; PC 1's card lists PC 2.
4. Play music on the phone. Both PCs play it, ~0.3 s after the phone.
5. If one PC sounds early or late, nudge **Sync this PC** on that PC (+ = later) until they sound like one.
6. If PC 2 can't find PC 1, or says "No answer": on PC 1, Windows Security → Firewall & network protection →
   Allow an app through firewall → BlueAux → Private ticked. Also check the network is set to Private, and
   that the Wi-Fi doesn't isolate devices (guest networks often do).

Send back `%AppData%\BlueAux\coplay.log` from both PCs: it shows the clock offset, round trip, sync error,
jumps and corrections.

Without a phone, the test beep shows sync on two PCs: on PC 1 run `BlueAux.exe --coplay-host loud`, on PC 2
`BlueAux.exe --coplay-guest loud`. The beeps should sound as one.
