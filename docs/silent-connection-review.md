# Silent connections: review and the way out

What this is: a review of everything found so far about iPhones that connect to BlueAux but play silently, what the cause looks like, and the fixes that would end it. No new tests were run for this.

## The problem in one sentence

The first connection after an iPhone comes back (Bluetooth toggled, out of range, PC was resting) is often "connected" with no sound, and nothing the phone sends tells the PC that it's broken, so BlueAux has been guessing with timers and reconnects.

## What the logs say about the cause

Every connection BlueAux made was logged with Windows' state of the phone's audio stream at the moment the link opened, and what happened after. Two patterns cover all of them:

| Stream state when the link opened | What happened next |
|---|---|
| **inactive** (Windows waits for the phone's data) | Sound came every time the phone played. The "reconnect once after 6 s" step fired on many of these anyway, for nothing. |
| **active with level 0** before anything played | The silent kind. Play stayed silent until a reconnect. |

Where that "active" stream comes from: in the recordings, the phone's stream stayed **active with level 0 while the phone was not connected at all** (OwO at 10:27:05–10:27:45 on 10 Nov, and `while disconnected: stream active` lines throughout `sound-check.log`). The phone's input device stayed present (`devstate 1`) through the phone's own disconnect. Only when BlueAux disposed its receiver did Windows unplug the device (`devstate 8`, session inactive), and the connection that followed came up inactive and worked.

So the working explanation: when the phone drops the link itself (Bluetooth off, walking away), Windows' audio receiver keeps the old audio path for that phone alive. The next link attaches a new Bluetooth stream, but the audio engine is still pumping the dead one, so nothing reaches the speakers. A reconnect from the PC side tears the old path down, and that's why "a second connection always fixes it". It also explains why a connection made while music already plays is never silent, and why one commenter found this stuck stream blocking the PC's hibernation.

This is not BlueAux's doing. The same bug is reported against every app that uses Windows' receiver, including Microsoft's own, and the one maintainer who looked at it closed it as *wontfix*: [AudioPlaybackConnector2 issue 1](https://github.com/N0ahTM/AudioPlaybackConnector2/issues/1). The older project warns about it too: "the first manual connection after restarting the app may occasionally connect without audio" ([AudioPlaybackConnector-Win11](https://github.com/summerwind6/AudioPlaybackConnector-Win11)), and [AudioPlaybackConnector issue 29](https://github.com/ysc3839/AudioPlaybackConnector/issues/29) is the iPhone case of it.

## What the phone can and can't tell the PC

Everything researched, with the result:

- **Apple media service (AMS) over Bluetooth LE.** The only real play/pause signal. BlueAux already uses it. It needs an LE pairing, which Windows only creates by itself on adapters with cross-transport key derivation. On the others, the in-app Set up exists but still fails on some PCs (Ahmed). Third-party tools do the same thing and hit the same wall: [iphone-companion](https://github.com/312arjun/iphone-companion) reads AMS on Windows and needs the full Windows pairing first, and notes Phone Link holds that link when it runs.
- **Nearby Info broadcasts** (no pairing needed). The documented activity codes ([furiousMAC](https://github.com/furiousMAC/continuity/blob/master/messages/nearby_info.md), [Handoff All Your Privacy](https://arxiv.org/pdf/1904.10600)) promise "audio playing" and "video playing" states, but the controlled recording on OwO never showed them: it sat on code 3 (idle) through music and TikTok, and only toggled between 3, B and F with the screen. Not usable for media state on current iOS.
- **AVRCP (the remote-control channel of the audio link).** The iPhone sends play state and song metadata here, and Windows has the driver for it (`OwO Avrcp Transport`, interface `bc03ba80-…`), but Windows discards the data and offers no app interface for it. Only kernel-level tracing would see it. Dead end for an app.
- **Windows' own signals.** The stream state above is the one reliable, pairing-free tell. The "Alternative A2DP Driver" ([bluetoothgoodies](https://www.bluetoothgoodies.com/a2dp/)) is for PC-to-headphones (source) only, so it can't replace the receiver side.

## Fixes that would end it

Ranked, with what each needs.

### 1. Clear the stale path before connecting (recommended)

Works on every PC and every phone, iPhone or Android, with no pairing and no song display.

- **Before connecting**, read the phone's stream while it's disconnected (BlueAux already does: the `while disconnected: stream active` lines). If it's **active**, Windows is holding a dead path, and the connection is going to be silent. So connect, let it open, drop it, and connect again, as one "connecting" step. The user sees one slightly longer connect and never a silent one.
- **At the moment the link opens**, read the stream once. **Inactive:** it's healthy, don't reconnect at all (today's blind 6-second reconnect goes away, with its one-minute gap and its "leaving it" cases). **Active with level 0 and nothing played:** reconnect right away, not after 6 s.
- Keep the AMS and volume-button rules as a second line of defence for anything that slips through.

This turns the guessing into a check.

**What testing added (12–13 Nov):** closing the app's receiver doesn't always make Windows let go of the old stream. With an iPhone 17 paused when it dropped the link, Windows held the stream for more than 8 seconds after the receiver was closed, and the reconnect landed on it again (still silent). What does tear it down, from user mode and without admin rights, is asking the Bluetooth audio driver itself to drop the link: `KSPROPSETID_BtAudio` / `KSPROPERTY_ONESHOT_DISCONNECT` sent with `IKsControl` to the driver's KS filter behind the phone's hidden input device (the approach [ToothTray](https://github.com/m2jean/ToothTray) uses for headphones; Microsoft documents the property under [KSPROPERTY_ONESHOT_DISCONNECT](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-disconnect)). In a probe it unplugged the phone's input device and closed the stream within a second, and the reconnect after it opened clean with sound. BlueAux's reconnect now does this first, then waits for the stream to be gone, then connects.

### 2. Mute and unmute instead of reconnecting

One user of the other app reports that muting and unmuting the phone's entry in the Volume Mixer brings the sound without reconnecting ([same issue, comments](https://github.com/N0ahTM/AudioPlaybackConnector2/issues/1)); the maintainer couldn't reproduce it. If it works, it's a fix with no gap in the music and no risk of the iPhone pausing. The test tool for it is built (`rawtest.exe` in the scratchpad) but hasn't been run through its steps yet. Worth one try on a day you want to, after fix 1 is in; not needed for fix 1.

### 3. Report it to Microsoft

With the finding above (the phone's A2DP SNK audio path survives a phone-side disconnect and the next link never feeds it), a Feedback Hub report has something concrete to act on. Long shot, long term; costs nothing.

### 4. For PCs without an LE pairing (Ahmed)

Fix 1 removes the main reason those PCs needed the song display. The Set up flow can stay as an extra, and its failure on his PC can be looked at separately, without blocking anything.

## What to drop

- Nearby Info as a play signal: measured not to work on current iOS.
- Reading AVRCP from an app: no interface.
- Replacing Windows' receiver with a third-party driver: none exists for the receiving side.
