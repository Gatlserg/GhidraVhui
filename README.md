# Vhui USB Server - C# / .NET

A USB server for Windows x64, written in C# / .NET 10, with a graphical interface
and support for the VirtualHere client. This repository contains the complete
source code for the core library, GUI, CLI, tests, build scripts, and documentation.

**Single executable, version 0.3.1:** `artifacts/Vhui.Server.exe` after publishing.
The executable includes the .NET runtime and both English and Russian GUI guides.
No separate .NET installation is required to run it.

The GUI provides a device tree, server start/stop controls, sharing settings,
logs, and a system tray menu. It supports English and Russian, and saves custom
device names that also appear in the VirtualHere client.
See the [English GUI guide](docs/GUI.en.md) or [Russian GUI guide](docs/GUI.ru.md).

## Requirements

- Windows x64.
- Administrator privileges to capture USB devices and change their driver binding.
- .NET SDK 10 to build from source; the published GUI includes its own runtime.
- A VirtualHere client on the receiving computer. Compatibility has been tested
  with client version 6.0.3 and a Trust QHD webcam (145F:02E7).

Compatibility with every USB device or every feature of the original server
has not been verified. Detailed protocol, INI configuration, TLS, reverse
connection, mDNS, and Windows service coverage is described in the
[compatibility status](docs/COMPATIBILITY_STATUS.ru.md) (Russian).

## Build

Run these commands **from the repository root**. Use an installed .NET SDK 10,
or install a local copy with `Setup-DotNet.ps1`:

```powershell
git clone https://github.com/Gatlserg/GhidraVhui.git
cd GhidraVhui
# Only needed if .NET SDK 10 is not already installed:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-DotNet.ps1
```

```powershell
# Build the solution and run the automated checks:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
# Build, run checks, and publish for Windows x64 with the .NET runtime included:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Publish
```

Publishing produces:

| Application | Output | Files to copy |
| --- | --- | --- |
| GUI server | `artifacts/Vhui.Server.exe` | Just this executable |
| CLI tool | `artifacts/recovery/win-x64/Vhui.Recovery.exe` | The entire CLI `win-x64` folder |

The original GUI publish output is also available at
`artifacts/server/single-file/win-x64/Vhui.Server.exe`.
Build outputs, the local SDK, working settings, private keys, and driver recovery
journals are excluded from Git.

## Run

Launch `artifacts/Vhui.Server.exe` after publishing. Follow the
[English GUI guide](docs/GUI.en.md) to configure sharing, switch languages,
and rename devices.

The `serve-usb` CLI mode has been tested with the original client 6.0.3 using
WinUSB capture, URB forwarding, MJPEG frames from a virtual USB device, release,
and restoration of the physical camera. See the
[USB server guide](docs/USB_SERVER.ru.md) (Russian) for commands and limitations.
The separate `serve-discovery` mode provides device information without capture.

The [CLI user guide](docs/USER_GUIDE.ru.md) (Russian) covers `inspect`,
`extract-payload`, `usb-inspect`, `probe-server`, and `serve-discovery`, including
examples, supported input files, errors, and output formats. A copy is published
as `README.ru.md` alongside the CLI executable.

For image analysis, supply the original executable separately:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start.ps1 inspect ..\analysis\recovered\vhusbdwinw64.exe
# Save the verified LZMA prefix to a new file:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start.ps1 extract-payload ..\analysis\recovered\vhusbdwinw64.exe .\artifacts\payload.bin
```

`Start.ps1` resolves input and output paths relative to the current directory,
so it can also be invoked from a parent directory.

## Repository layout

Open **`Vhui.sln`** in Visual Studio, or open the repository root in VS Code.
The repository root corresponds to the `dotnet` folder in the original workspace.
VS Code build tasks and F5 debugging use this root. The `inspect source image`
debug configuration requires the original executable in `../analysis/recovered/`.

| Path | Purpose |
| --- | --- |
| `src/Vhui.Core/` | USB capture and forwarding, protocol handling, configuration, TLS, discovery, and LZMA/LZ4 decoding |
| `src/Vhui.Recovery/` | CLI for image analysis, USB inspection, and server operations |
| `src/Vhui.Server/` | Windows Forms USB server GUI |
| `tests/Vhui.Recovery.Tests/` | Automated checks and independent reference fixtures |
| `scripts/` | SDK selection, fixture generation, and compatibility test scripts |
| `config/server.example.ini` | Example server configuration |
| `docs/` | User guides and compatibility documentation |
| `Build.ps1` | Build, checks, and publishing |
| `Start.ps1` | CLI launcher |
| `.tools/` | Local SDK and installer; excluded from Git |
| `artifacts/` | Published applications, test reports, and extracted data; excluded from Git |

The build creates `bin` and `obj` directories automatically.

Decompiled code and original executables are outside this repository and are not
required for a normal build or the automated checks. References to `analysis/`
in the documentation refer to a separate analysis directory next to the project.
If `../analysis/recovered/vhusbdwinw64.exe` exists, `Build.ps1` also checks decoding
of its packed stream. Comparison scripts that use the original client and server
require their executables in that external directory.

To run the webcam interoperability check, use
`scripts/Test-UsbClient.ps1 -VideoProbe` in an elevated PowerShell session with
the required hardware and original client available. The report is written to
`artifacts/usb-client-test/result.json`.
