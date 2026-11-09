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
- **What fixes it:** a second connection, made while something plays. That always works. Made while nothing plays, it sometimes does, but often comes up silent again, even right after you unlock the phone.
- **What doesn't trigger it:** a connection made while music is already playing.
- **A tell:** Windows marks the phone's audio stream "active" even though nothing plays. On a working connection, the stream only turns active once music arrives.

On the meters alone, a broken connection looks exactly like a paused phone: both read 0. iPhones fill that gap (step 2).

The app deals with this in six ways:

1. **It reconnects once, right away.**
   - When the app has connected to a phone itself and nothing has come through after 6 seconds, it reconnects once ("no sound yet, so reconnecting once" in the log).
   - If sound arrives in those 6 seconds, it leaves the connection alone.
   - It never does this more than once a minute.
2. **It asks the iPhone whether it's playing.**
   - iPhones tell paired accessories when they play or pause, over Bluetooth LE. This is Apple's media service (AMS), the one car stereos and smartwatches use. Windows normally adds an LE pairing next to the audio one. The app finds it because both are in the same device container (`System.Devices.Aep.ContainerId`). It never pairs anything itself.
   - It reconnects while the music plays ("it's playing but no sound comes through" in the log) when all of these are true:
     - the phone says it's playing,
     - Windows' stream for it is active,
     - nothing has reached the speakers for 3 seconds,
     - this connection has never played.
   - iPhones pause when their audio disappears for a moment. If the phone paused during that reconnect, the app presses play on it.
   - It leaves things alone in three cases:
     - The stream is idle. Then the phone is playing to AirPods or its own speaker, and reconnecting would pull the music over to the PC.
     - The connection has played before.
     - One reconnect already didn't help.
   - Some apps, such as TikTok, don't report what they play, so the phone says "paused" while they play. This check stays quiet for them; step 3 covers them.
   - The song is shown on the main screen while it plays. Song names aren't written to any log.
   - Android phones don't offer this. For them, steps 1 and 4 do the work.
3. **It listens for the volume buttons.**
   - The same media service reports the phone's volume, whatever app is playing. Someone who presses a volume button and hears nothing is listening to something, so it's playing right then, which is when a reconnect works.
   - It reconnects ("you pressed a volume button but no sound comes through" in the log) when all of these are true:
     - the volume changed more than 5 seconds after this connection started (connecting changes it by itself),
     - 2 seconds later, still nothing has reached the speakers on this connection,
     - Windows' stream for the phone is active, so the phone is sending to this PC and not to AirPods or its own speaker.
   - It shares the limits of step 4: at most once every 45 seconds, and 3 times in 10 minutes.
   - The phone's Bluetooth broadcasts also hint at whether it's unlocked. Reconnecting the moment you unlock it was tried, but the connection usually came up silent again, because nothing was playing yet.
4. **It watches the sound.**
   - Windows plays a phone through a system audio stream (`svchost`, named `"<phone> A2DP SNK"`). That stream copies the phone's hidden input device to the default speakers, and its session ID ends in `%b{<guid>}`, the ID of that input device.
   - So once a second while connected, the app reads two levels: sound arriving from the phone, and sound going to the speakers.
   - If sound arrives but nothing plays, the connection is stuck, and the app rebuilds it while the music is still playing. It also rebuilds it if the stream is missing altogether.
   - It never tries more than 3 times in 10 minutes.
5. **It avoids the trigger.** By default, "Rest when I'm away" keeps a connected phone connected. Only the searching stops.
6. **It makes the manual fix one click.** That's the **Fix sound** button, the "connected" notification and the tray menu.

Everything the watcher sees is written to `sound-check.log`, including what the iPhone reports (playing / paused and the app's name).

**How this was tested:** with an iPhone, by leaving it away for hours or turning its Bluetooth off while the music was paused, then pressing play.
- Without any fix, play stayed silent.
- Reconnecting once (step 1) fixed it most of the time.
- When playing right after it connected, step 2 caught it, and the sound came within about 10 seconds of pressing play.
- With the output set to the iPhone's own speaker, the app left the music where it was.
- With TikTok, after turning Bluetooth off and on while the phone was locked: silent after unlocking, until one press of volume up. Step 3 reconnected, and the sound came about 7 seconds after the press.

## Idle detection

"Away" means no keyboard or mouse input (`GetLastInputInfo`) for the chosen number of minutes, or a locked PC. Phone audio playing through the PC counts as use, so music is never cut off.

## Design

The UI follows Fluent 2, the design system Windows 11 uses. It's rebuilt in plain WPF, so the app stays one file with no libraries:
- **Colour:** Fluent's colour tokens for light and dark mode, plus your Windows accent colour (`Theme.cs`). On Windows 11 the window uses the Mica backdrop.
- **Type:** Fluent's type ramp: Caption 12, Body 14, Body Strong 14 semibold, Subtitle 20 semibold.
- **Space and shape:** multiples of 4 px, 4 px corners on controls and 8 px on cards.
- **Controls:** buttons, switch, number box, card, divider and info bar, all in `Styles.xaml`. The layout in `MainView.xaml` uses nothing else.

## Memory

The window is destroyed when it's closed to the tray, and the working set is trimmed. That leaves about 10–20 MB in the tray while a phone is playing, and less when none is connected.

## Building

`build.cmd` calls `csc.exe` from .NET Framework 4.8 with references to the WinRT metadata in `C:\Windows\System32\WinMetadata`. WinRT `await` helpers are included in the source, because the stock ones need the Windows SDK.
