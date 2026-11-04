# Phone Speaker

Plays your phone's audio through this PC over Bluetooth, and reconnects by itself like a car stereo.

Needs Windows 10 version 2004 or later, or Windows 11, with Bluetooth.

## Use it

1. Pair your phone with the PC once (Windows Settings → Bluetooth & devices). Phone Speaker lists it automatically.
2. Run `build.cmd` (see [Build](#build)), then run `bin\PhoneSpeaker.exe`. Choose **Start with Windows** so it is always ready in the tray.
3. That's it. When the phone is nearby it connects by itself. You can also pick this PC in the phone's Bluetooth menu, or press **Connect**.

- **Disconnect** stops the audio and pauses auto-connect for every device until you press **Connect** or **Resume auto-connect**.
- **No sound? Fix it** (under "Playing from…") reconnects the phone, for when it says connected but nothing plays. Press play on the phone first: a reconnect while music is playing is what gets stuck sound going again.
- **Rest when the PC is idle** (Settings): after a set number of minutes without mouse or keyboard input, or with the PC locked, it stops looking for your phone. With **Disconnect the phone too** on, it also disconnects. It starts again as soon as you use the PC. Phone music playing through the PC counts as use, so it won't cut off a song.
- Closing the window keeps the app in the tray, using about 5 MB of memory, because the window is rebuilt only when you open it. Use **Exit** in the tray menu to quit. If it ever crashes, Windows restarts it in the tray.
- Settings and the activity log are in `%AppData%\PhoneSpeaker`.

## How it stays reliable

- It uses the Bluetooth audio receiver built into Windows 10 2004+ / Windows 11 (`AudioPlaybackConnection`).
- Only one receiver is open at a time: for the phone that's playing, the phone being connected, or otherwise the phone you used last. That phone can connect from its own Bluetooth menu at any time.
  Windows shares one "connected" state across all receivers in an app, so with several open it can't tell which phone is really playing. That caused "connected" with no sound.
- Every Connect really opens the audio link, even if Windows already claims to be connected. It waits until Windows has finished preparing the receiver, and never throws away a receiver Windows is still working on. Doing so crashed Windows' Bluetooth audio component.
- Known iPhone quirk: if the PC closes the audio link while the music is paused, the next connection can come up silent. A reconnect while music is playing (the **Fix it** link) or turning Bluetooth off and on on the phone clears it.
- When a connection drops or an attempt fails, the old connection object is thrown away and a new one is created. Re-opening the old one is what usually leaves you "connected" with no sound.
- If auto-connect is on, it calls out to the phone: quickly at first after a drop, then every 20–30 seconds, trying the phone you used last first. It also tries immediately when the phone's Bluetooth link comes up, after the PC wakes from sleep, and when Bluetooth is turned back on.
- The phone's sound plays on Windows' **default output device**. If you switch between speakers and a headset, change the default in Settings → Sound (the app's **Sound output** button opens it).

## Build

Run `build.cmd`. It uses the C# compiler that ships with Windows (.NET Framework 4.8), so there is nothing to install.
