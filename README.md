# 🛡️ CrashLens

**Lightweight Windows Crash Monitoring & Diagnostics**

**CrashLens** is a lightweight Windows utility designed to monitor system errors, application crashes, unexpected shutdowns, and Blue Screen of Death (BSOD) events.

Built with C# and WPF, CrashLens runs quietly in the system tray, helping you understand what went wrong with your PC.

**Developed by Nikita Batalov**

---

## ✨ Features

- **Real-Time Monitoring** — Tracks critical Windows events and application crashes.
- **BSOD Detection** — Identifies BugCheck events and extracts available stop codes.
- **Unexpected Shutdown Detection** — Detects unexpected restarts and shutdowns.
- **Error Explanations** — Provides descriptions for known Windows error codes.
- **Google Error Lookup** — Quickly search for additional information about any error.
- **System Tray Integration** — Runs quietly in the background.
- **Windows Startup** — Supports automatic launch when Windows starts.
- **Event History** — Browse previously detected errors with filters and search.
- **CSV Export** — Export recorded events for troubleshooting.
- **Privacy First** — Stores diagnostic history locally without uploading telemetry.

## 📸 Interface

CrashLens features a modern dark interface with blue accents, designed for easy navigation and quick access to diagnostic information.

## 💻 System Requirements

| Component | Requirement |
|---|---|
| Operating System | Windows 10 / 11 |
| Architecture | x64 |
| Framework | .NET 8 |
| Technology | C# / WPF |
| Internet | Only required for online error lookup |

The self-contained release does not require a separate .NET installation.

## 📥 Installation

1. Open the **Releases** section of this repository.
2. Download the latest `CrashLens.exe`.
3. Launch the application.
4. Enable automatic startup if desired.
5. CrashLens will monitor supported Windows events in the background.

Some system event logs may require elevated permissions.

## 🔍 How It Works

CrashLens uses the Windows Event Log infrastructure to detect and collect diagnostic events.

It monitors supported event categories, including:

- Application crashes and failures
- Windows BugCheck reports
- Unexpected system shutdowns
- Critical system events
- Selected hardware and service errors

When Windows restarts after a crash, CrashLens can retrieve available historical events from the Windows Event Log.

**Note:** CrashLens cannot guarantee identification of every system crash. The information available depends on what Windows successfully records.

## 🔐 Privacy & Performance

CrashLens is designed for minimal background resource usage.

- No cloud account required
- No automatic telemetry uploads
- Local diagnostic history
- Event-driven monitoring
- Online searches initiated by the user

## 🛠️ Build From Source

1. Clone or download this repository.
2. Open `CrashLens.csproj` in Visual Studio 2022 or later.
3. Install the .NET 8 SDK and Windows desktop development workload.
4. Select `Release` and `x64`.
5. Build or publish the application.

For a standalone executable, publish using:

- Deployment mode: `Self-contained`
- Target runtime: `win-x64`
- Produce single file: `True`
- Trimming: `False`

## 📌 Version

**CrashLens v1.0.2**

Initial public release with fixes for missing `System.IO` references and improvements to source compatibility.

## 👨‍💻 Developer

**Nikita Batalov**

CrashLens — *See the crash. Understand the cause.*

---

© 2026 Nikita Batalov. All rights reserved.
