# SimpleDream

A clean, simple receiver for Digital Radio Mondiale (DRM) on radios with TCI. Tune to a
station, or pick one from the broadcast list, and listen. The mode, bandwidth, codec and
station name all come from the signal, so there's nothing to set.

Built with [Avalonia](https://avaloniaui.net/) on the
[DecDRM](https://github.com/CasualArclamp/DecDRM) decoder.

SimpleDream is receive only. Its TCI client refuses everything except IQ start and stop,
setting the receive frequency and muting the radio's audio.

## Features

- **Auto-detect:** robustness mode, bandwidth, modulation and codec.
- **Broadcasts:** EiBi and DRMDX schedules merged, shown in your local time. Click one to tune.
- **Why there's no audio:** a too-weak signal shows the level the station needs.
- **Data:** station details, text messages, Journaline news and slideshow pictures.
- **Recording** of the decoded audio to WAV.
- **Light and dark themes**, following the system.

## Build

You need the .NET 10 SDK and Rust 1.88 or later, plus a C/C++ compiler and CMake.

```sh
git clone --recursive https://github.com/kingsqueak/SimpleDream.git
cd SimpleDream
dotnet run --project src/SimpleDream
```

The build runs cargo for the bridge in `native/`.

## How it fits together

- `src/SimpleDream`: the Avalonia app and its TCI client.
- `native`: a Rust library that moves the channel to 0 Hz, decimates to 48 kHz and drives DecDRM.
- `external/DecDRM`: the decoder, unmodified, as a submodule.

Station Engine sends IQ at the radio's hardware rate with its spectrum mirrored, whatever
is requested, so `native/src/ddc.rs` takes any multiple of 48 kHz and can conjugate first.

## Licence

GPL-3.0-or-later. DecDRM is GPL-2.0-or-later and links FDK-AAC, whose licence is widely
regarded as GPL-incompatible, so check this before distributing binaries.
