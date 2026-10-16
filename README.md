# GTA V Radio

The GTA V radio, running outside the game: every station (including DLC), DJ chatter timed to the music, adverts, optional news, and stations that keep going while you're tuned elsewhere.

**This repository contains no game audio or other Rockstar files.** The radio is read from your own copy of GTA V, on your own PC, and stays on your devices.

## Parts

- `pc/` is **GTARadio.exe**, a Windows program. On first run it reads the radio from your GTA V install and plays it in a radio wheel in your browser. It can also make the iPhone copy.
- `phone/` is the **iPhone player**, published with GitHub Pages. It runs the stations on the phone itself, from a radio file you copy over once. It works offline and doesn't need the PC to be on.

## iPhone setup

1. On the PC, run GTARadio.exe and click **Make iPhone copy**. It creates `GTARadio-phone.pack` next to the exe (about 2 GB).
2. Copy that file to your iPhone, for example through iCloud Drive or with a cable into the Files app.
3. On the iPhone, open the player page in Safari, tap Share, then **Add to Home Screen**, and open it from the Home Screen.
4. Tap **Choose GTARadio-phone.pack** and pick the file. It's copied into the app's private storage, so you can delete the original afterwards.

## Building the PC program

`pc/GTARadio` needs [CodeWalker.Core](https://github.com/dexyfex/CodeWalker) (MIT) next to it as `pc/CodeWalker.Core`. The **Build GTARadio.exe** workflow does this and publishes a single-file exe as a build artifact.

The iPhone player uses [opus-decoder](https://github.com/eshaz/wasm-audio-decoders) (MIT). The PC program uses [NLayer](https://github.com/naudio/NLayer) (MIT) and [Concentus](https://github.com/lostromb/concentus) (BSD).
