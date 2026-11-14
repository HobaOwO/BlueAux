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
- **The tell:** Windows marks the phone's audio stream "active" even though nothing plays. On a working connection, the stream opens inactive and only turns active once music arrives.
- **Where it comes from:** when the phone drops the link itself (Bluetooth off, out of range), Windows keeps the phone's old audio stream running, active and carrying nothing, the whole time the phone is away (`while disconnected: stream active` in the log). The next link attaches to a new Bluetooth stream, but the audio engine is still pumping the dead one. A reconnect from the PC tears the old stream down, which is why a second connection fixes it. This happens with every app built on Windows' receiver, Microsoft's own included.

On the meters alone, a broken connection looks exactly like a paused phone: both read 0. The stream state fills that gap for every phone; iPhones add their own word (step 2).

The app deals with this in six ways:

1. **It reads the tell as the link opens, and reconnects once if it's there.**
   - A second after a connection opens (the first reading can be left over), it looks at Windows' stream for the phone.
   - Stream inactive: the connection is healthy, and it's left alone ("so this connection should work" in the log). One exception: the first connection the app makes after the PC woke up, or to a phone that was away for more than 10 minutes, is silent in a way the stream doesn't show (it opens inactive and never turns active, and play is silent on the phone too). That connection keeps the old rule: one reconnect after 6 silent seconds, unless sound comes first.
   - Stream active with nothing coming through: it reconnects once ("reconnecting once so it works when you press play"). If the stream was already seen active while the phone was away, that happens at once; otherwise music that was already playing gets 3 seconds to show up first.
   - Closing the app's receiver doesn't always make Windows let go of the old stream (it held one for more than 8 seconds while an iPhone 17 was paused). So before reconnecting, the app asks Windows' Bluetooth audio driver itself to drop the phone's audio link, the way the Disconnect button in the Sound control panel does (`KSPROPSETID_BtAudio` / `KSPROPERTY_ONESHOT_DISCONNECT`, sent with `IKsControl` to the driver's filter behind the phone's hidden input device; no admin rights needed). On a live link, Windows unplugs the phone's input device and closes the stream within a second. The app then waits for the stream to be gone (up to 3 seconds) before connecting again ("asked Windows' Bluetooth audio driver to drop its old link" and "Windows let go of its old stream after N s" in the activity log).
   - When the phone itself dropped the link while paused, the stream is a leftover with no link behind it, and nothing closes it: the reconnected link opens onto it again, still active and empty. Then the app mutes the phone's entry in the Volume Mixer for a second and unmutes it ("muting it for a second" / "unmuted it again"). In testing this brought the sound within seconds every time, where the reconnect alone had stayed silent.
   - It never reconnects a connection that a reconnect just made, and never more than once a minute, so it can't loop.
   - With no stream to read at all, a connection the app dialed itself gets the old rule: one reconnect after 6 silent seconds.
2. **It asks the iPhone whether it's playing.**
   - iPhones tell paired accessories when they play or pause, over Bluetooth LE. This is Apple's media service (AMS), the one car stereos and smartwatches use. AMS needs an LE pairing. Windows adds one next to the audio pairing only when the PC's Bluetooth can derive it from that pairing (cross-transport key derivation), and many adapters can't. The app finds it because both pairings are in the same device container (`System.Devices.Aep.ContainerId`).
   - When there's no LE pairing and the phone is from Apple (Bluetooth vendor ID 76), the app offers **Set up**:
     - It listens for 6 seconds for iPhone broadcasts (Apple's "Nearby Info") that aren't from an already-paired device. It picks the strongest one, and only if it's closer than about -80 dBm.
     - It connects and asks for the media service's updates first, the way car stereos do. That part needs pairing, so the iPhone wants it too, and the pairing that follows gets the iPhone's Pair prompt. (Asking an iPhone to pair out of the blue fails within seconds, with no prompt.)
     - It remembers the phone's lasting LE address, because the broadcasts use changing ones.
     - If the paired device has no media service, it wasn't the iPhone, so the app unpairs it again.
     - Each step is noted in `sound-check.log` ("heard N unpaired iPhone-like device(s)", "paired over Bluetooth LE", "song display set up", or "setup didn't work" with the reason).
   - The app never pairs anything unless you press **Set up**.
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
   - The song is shown on the main screen while it plays, with its cover if *Album art* is on (see Design). Song names aren't written to any log.
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
   - Windows keeps one such stream for every phone that has connected, plus leftovers from old pairings (`"2- <phone> A2DP SNK"` after re-pairing). The app reads the connected phone's own stream:
     - the one in the phone's device container (`PKEY_Device_ContainerId` of the input device, the same container as the phone's Bluetooth entry);
     - if containers aren't known, the one named after the phone;
     - never one whose input device Windows no longer has, or one that belongs to another phone. Many iPhones are just called "iPhone", so the container decides when it's known.
   - Reading the first stream it found worked with one phone, but with two it read a leftover. Then a working phone looked silent, and a silent one looked like it was playing elsewhere, so nothing got fixed.
   - So once a second while connected, the app reads two levels: sound arriving from the phone, and sound going to the speakers.
   - A broken connection reads exactly 0. iPhones lower their volume digitally before sending, so music at the lowest volume step reads about 0.01, and quiet parts less. So anything above 0.0001 counts as sound, and music at any volume counts as using the PC (it won't rest mid-song).
   - If sound arrives but nothing plays, the connection is stuck, and the app rebuilds it while the music is still playing.
   - If the phone says it's playing and Windows has no stream for it at all, the app rebuilds it once. A phone that connected while paused can have no stream until it plays, so a missing stream alone never triggers a reconnect.
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

## Sleep and lock

- **Sleep:** when Windows announces it's going to sleep, the app closes the phone's connection itself ("PC going to sleep: disconnecting" in the activity log) instead of letting the link die mid-stream, so the phone and Windows are in a clean state for the connection after waking. After waking, it reconnects as before.
- **Lock:** when the PC is locked, the app lets the phone go ("PC locked: disconnecting"), so nothing plays on a PC nobody is at, and the phone pauses. On unlock it looks for the phone again right away.
- Both are on by default (`DisconnectWhenAsleep`, `DisconnectWhenLocked` in `settings.xml`).

## Idle detection

"Away" means no keyboard or mouse input (`GetLastInputInfo`) for the chosen number of minutes, or a locked PC. Phone audio playing through the PC counts as use, so music is never cut off.

## Design

The UI follows Fluent 2, the design system Windows 11 uses. It's rebuilt in plain WPF, so the app stays one file with no libraries:
- **Themes:** four, picked in Settings > Appearance and switched live (`Theme.cs`). *Windows* follows the light/dark setting and uses the Mica backdrop on Windows 11. *OLED* is true black (#000000) everywhere, cards outlined rather than lighter, text dimmed a touch below white, no Mica. *Graphite* is solid dark grey with depth by lightness and no strokes. *Paper* is warm off-white with white cards on a soft shadow. All four use your Windows accent colour, and on Windows 11 the title bar takes the theme's colours too (`DWMWA_CAPTION_COLOR`), so OLED is black up to the top edge. Styles only ever use the theme's colour tokens, through `DynamicResource`, which is what makes the live switch possible.
- **Type:** Segoe UI Variable at its optical sizes: Small for captions, Text for body, Display for titles, with explicit line heights (Caption 12/16, Body 14/20, Body Strong semibold, Subtitle 20/28, the now-playing title 22/28). Windows 10 falls back to Segoe UI by itself.
- **Space and shape:** multiples of 4 px, 4 px corners on controls, 8 px on cards, 12 px on the now-playing card, which also gets a hairline that catches the light at the top and a soft accent halo behind the circle while a phone is connected.
- **Motion:** Fluent's 83 / 167 / 250 ms ramp. Buttons fade their hover fill in and shrink a touch when pressed, the switch knob stretches while held, pages slide in, cards that appear rise into place, the circle cross-fades to the accent colour with one ring burst on connect, and a new song rises into the card. The *Animations* switch in Settings turns all of it off, and so does Windows' own "Show animations" setting (`Motion` in `Theme.cs`; the durations are read when `Styles.xaml` loads, so a change reloads the styles and the view). Loops only run while the window is visible.
- **Album art:** an iPhone sends the song's title and artist, never its cover. With *Album art* on (Settings > Appearance), the app looks the song up in Apple's iTunes catalogue (Deezer as a fallback) and shows the cover on the now-playing card, with a record that slides out from behind it and spins while the music plays, and the cover's own colours as a soft tint over the card (`AlbumArt.cs`). Only the title and artist are sent. Each song is looked up once: covers are kept in memory and in the `art` folder next to the settings, filed under a hash so no song names are written to disk.
- **Icon:** an aux plug with two sound waves leaving its tip, white and blue on a flat near-black tile, drawn in code (`IconFactory.cs`); `app.ico` is generated from it by `build.cmd` (or `BlueAux.exe --make-icon srcpp.ico`).
- **Tray icon:** the coloured tile, or *Plain tray icon*: just the plug and waves in one colour, white on a dark taskbar and black on a light one, like Windows' own icons. The status dot stays either way.
- **Window:** the title bar is the app's own (`WindowChrome` in `MainWindow.cs`): no icon and no name, just the page's buttons at the left and the three caption buttons at the right, drawn in the theme's colours. The maximize button answers Windows' hit test as a real one (`HTMAXBUTTON`), so Windows 11's snap layouts flyout still appears over it. The window grows and shrinks to the page it shows (Settings is taller than the main page), sliding rather than jumping and staying on screen, up to 640 px, past which Settings scrolls. Mouse-wheel scrolling glides instead of jumping (`SmoothScroll`); both follow the Animations switch.
- **Settings:** grouped into General (start with Windows, notifications, keep running when closed, always on top), When you're away, Appearance and Connect automatically, each row with a Fluent glyph, plus a collapsed More section for the Windows shortcuts and the activity log.
- **Controls:** buttons, switch (with an optional row glyph), number box, theme swatch, caption buttons, card, divider and info bar, all in `Styles.xaml`. The layout in `MainView.xaml` uses nothing else.

## Memory

The window is destroyed when it's closed to the tray, and the working set is trimmed. That leaves about 10–20 MB in the tray while a phone is playing, and less when none is connected.

## Building

`build.cmd` calls `csc.exe` from .NET Framework 4.8 with references to the WinRT metadata in `C:\Windows\System32\WinMetadata`. WinRT `await` helpers are included in the source, because the stock ones need the Windows SDK.
