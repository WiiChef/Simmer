# <img src="simmer.png" width="40" align="top"> Simmer

Power, fan and battery control for Snapdragon laptops on Windows on ARM. A small tray app that keeps the laptop cool,
quiet and long-running.

## Features

- **Chip power cap**: limit the whole chip to 5, 8, 13, 17 or 22 W (or leave it free), and split that budget
  between CPU and GPU.
- **CPU tuning**: hold a CPU watt target, or set fixed clock caps; energy preference, turbo boost and
  efficient-cores-only.
- **Fan curve**: your own curve (silent until it's needed), plus the ASUS Whisper / Standard / Performance modes,
  kept in sync with Fn+F.
- **Battery charge limit**: stop charging at 50-100 %, applied at boot too, before anyone logs in. When the battery is above the limit with the charger in, Simmer drains it down to the limit (needs Windows test-signing mode and the driver in `tools\BmdDrain`; the driver ends the drain itself if Simmer stops).
- **Live readout**: laptop, chip, CPU and GPU power, battery time, temperature and fan speed, with a history chart.
- **Profiles**: three saved profiles; one can start with Windows, and profiles can switch automatically on AC or battery.
- **Quiet on battery**: switches to quieter settings when unplugged.
- **Safety**: every change must be kept within 60 s or it's undone, and after a crash Simmer starts back at
  Windows defaults.

## Supported devices

Built for and tested on the **ASUS Zenbook A16 (UX3607OA, Snapdragon X2 Elite Extreme)**.

- The power cap, CPU tuning and live readout use Qualcomm's own drivers, so they should also work on other laptops
  with the Snapdragon X2 Elite / Elite Extreme (untested).
- The charge limit uses an ASUS battery setting; it probably works on other ASUS Snapdragon laptops.
- The fan curve talks to this model's fan controller directly; don't turn it on for other models.

Use at your own risk. `Simmer.exe --off` puts everything back to Windows and ASUS defaults.

## Build and run

Run `build.cmd` (needs Visual Studio Build Tools; no .NET SDK needed), then start `Simmer.exe`. On first start it asks
for administrator rights once and installs itself to `Program Files\Simmer`.
