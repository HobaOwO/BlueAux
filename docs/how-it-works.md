# How it works

BlueAux uses the Bluetooth audio receiver built into Windows 10 2004+ and Windows 11 (`Windows.Media.Audio.AudioPlaybackConnection`). Windows does the actual audio work. The app decides when to connect, and spots and repairs connections that go wrong.

## Connecting like a car stereo

- If auto-connect is on, the app calls out to the phone itself. It retries quickly after a drop, then every 20–30 seconds, starting with the phone you used last.
- It also tries right away when:
  - the phone's Bluetooth link comes up,
  - the PC wakes from sleep,
  - Bluetooth is turned back on,
  - you come back to an idle PC.
- The phone can also connect from its own Bluetooth menu at any time.

## Windows quirks it works around

- **Only one receiver open at a time.** Every receiver in a process shares one "connected" state. With several open, Windows can't tell which phone is playing, and you get "connected" with no sound. So only one receiver is open: for the phone that's playing, the phone being connected, or otherwise the phone used last.
- **Every Connect really opens the link.** It opens it even when Windows already claims to be connected, because that claim can be left over from another receiver.
- **Receivers are never reused.** When a connection drops or an attempt fails, the old receiver object is thrown away and a new one is made. Reopening an old one tends to leave you connected with no sound.
- **Never dispose a receiver Windows is still working on.** Disposing a receiver while `StartAsync` or `OpenAsync` is still running crashes `Windows.Media.Devices.dll`. The app waits until Windows has finished. As a safety net, it registers with Windows Error Reporting to be restarted after a crash.

## Connected but silent

The first connection the PC makes to an iPhone that has just come back often comes up silent. "Come back" means back in range, Bluetooth turned on again, or the PC done resting.
- **What it looks like:** the link is up and the phone says it's playing, but no sound reaches Windows at all.
- **What fixes it:** a second connection made right after the first one works, whether the music is playing or paused.
- **What doesn't trigger it:** a connection made while music is already playing.

From the PC, a broken connection looks exactly like a paused phone: both read 0 on every meter. So the app doesn't try to tell them apart.

The app deals with this in four ways:

1. **It reconnects once, right away.**
   - When the app has connected to a phone itself and nothing has come through after 6 seconds, it reconnects once ("no sound yet, so reconnecting once" in the log).
   - If sound arrives in those 6 seconds, it leaves the connection alone.
   - It never does this more than once a minute.
2. **It watches the sound.**
   - Windows plays a phone through a system audio stream (`svchost`, named `"<phone> A2DP SNK"`). That stream copies the phone's hidden input device to the default speakers, and its session ID ends in `%b{<guid>}`, the ID of that input device.
   - So once a second while connected, the app reads two levels: sound arriving from the phone, and sound going to the speakers.
   - If sound arrives but nothing plays, the connection is stuck, and the app rebuilds it while the music is still playing. It also rebuilds it if the stream is missing altogether.
   - It never tries more than 3 times in 10 minutes.
3. **It avoids the trigger.** By default, "Rest when I'm away" keeps a connected phone connected. Only the searching stops.
4. **It makes the manual fix one click.** That's the **No sound?** link, the "connected" notification and the tray menu.

Everything the watcher sees is written to `sound-check.log`.

**How this was tested:** with an iPhone, by leaving it away for hours or turning its Bluetooth off for 30 seconds while the music was paused. Without the reconnect, play stayed silent. With it, the sound worked on every try.

## Idle detection

"Away" means no keyboard or mouse input (`GetLastInputInfo`) for the chosen number of minutes, or a locked PC. Phone audio playing through the PC counts as use, so music is never cut off.

## Memory

The window is destroyed when it's closed to the tray, and the working set is trimmed. That leaves about 10–20 MB in the tray while a phone is playing, and less when none is connected.

## Building

`build.cmd` calls `csc.exe` from .NET Framework 4.8 with references to the WinRT metadata in `C:\Windows\System32\WinMetadata`. WinRT `await` helpers are included in the source, because the stock ones need the Windows SDK.
