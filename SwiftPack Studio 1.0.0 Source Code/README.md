# SwiftPack Studio

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**SwiftPack Studio** is a free, open-source Windows desktop application that automates
Microsoft Intune Win32 app packaging. It takes an application from source download to a
published Intune app, with validation along the way.

Built with C#, WPF and .NET 8. Published by [Smekal Solutions](https://www.smekalsolutions.com).

## Features

SwiftPack Studio is organised as a set of tabs that follow the packaging workflow:

| Tab | What it does |
|---|---|
| **Auto Package** | End-to-end pipeline: source download, silent install/uninstall, PSAppDeployToolkit packaging, `.intunewin` creation and Intune publishing, chained together. Includes automated source download with Windows Package Manager (winget) catalog integration and scheduling. |
| **MSI** | Edit MSI databases and create MST transforms (properties, features, files, registry, custom actions, sequences, dialogs, and more). |
| **Validate Source** | Inspect and validate application source: capture installer changes (registry, files, services, scheduled tasks, ODBC and more) with a before/after snapshot workflow. |
| **Package** | Create PSAppDeployToolkit packages from MSI or EXE sources. |
| **Execute Package** | Run package install/uninstall, including in SYSTEM context via PsExec. |
| **Validate Package** | Verify an installed or removed package against its expected state, and export results to Excel. |
| **Intunewin** | Create `.intunewin` files with the Microsoft Win32 Content Prep Tool. |
| **Publish Package** | Publish Win32 apps to your own Intune tenant through the Microsoft Graph API (detection rules, requirements, assignments, supersedence). |
| **Document** | Generate package documentation. |

Everything runs on your computer and, where applicable, in your own Microsoft tenant.
SwiftPack Studio has no backend server and sends no telemetry (see `Legal/PrivacyPolicy.txt`).

## Download

Ready-to-use Windows builds are published on the
[Releases](https://github.com/SmekalSolutions/SwiftPackStudio/releases) page. Official
releases are provided by Smekal Solutions and may be digitally signed.

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 / 11 | Windows Installer COM interop and Windows APIs are used throughout |
| Administrator rights | The app manifest requests elevation so installers and child processes inherit it |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | To build from source (the .NET 8 Desktop Runtime is enough to run) |
| Visual Studio 2022 (17.8 or later) | Optional, with the ".NET desktop development" workload |

### External tools you supply yourself

These are **not** bundled with this repository and are not redistributed by SwiftPack Studio:

- [PSAppDeployToolkit](https://psappdeploytoolkit.com) (LGPL-3.0)
- [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool) (`IntuneWinAppUtil.exe`)
- [Sysinternals PsExec](https://learn.microsoft.com/sysinternals/downloads/psexec) (for SYSTEM-context operations)

Each is governed by its own license; see `Legal/NOTICES.txt`.

## Build from source

```
git clone https://github.com/SmekalSolutions/SwiftPackStudio.git
cd SwiftPackStudio
dotnet build SwiftPack.csproj -c Release
```

Or open `SwiftPack.csproj` in Visual Studio and press `F5`. Because the app requests
administrator elevation, start Visual Studio as administrator to debug.

At runtime the app looks for its helper tools and JSON configuration under
`C:\Program Files\Smekal\SwiftPack Studio\Templates`. The official installer sets this up;
if you run a source build, create that folder and place the tools you supply there.

## Project layout

```
SwiftPack.csproj          Project file (.NET 8 WPF)
App.xaml(.cs)             Application entry point
ThemeManager.cs           Light / dark theme switching
Models/                   Data models
Services/                 Document export
Resources/                Colors, styles, light and dark themes, PSADT command catalog
Views/                    Main window and feature pages (one page per tab)
Legal/                    License texts and third-party notices
```

## License

SwiftPack Studio is licensed under the [MIT License](LICENSE). Third-party components keep their own
licenses; see [`Legal/NOTICES.txt`](Legal/NOTICES.txt).

**Trademarks.** The MIT License covers the source code only. The names "SwiftPack Studio" and
"Smekal Solutions" and their logos are trademarks of Smekal Solutions. If you distribute a modified
version, please use a different name and branding and do not suggest it is an official release. See
[`Legal/EULA.txt`](Legal/EULA.txt) (Terms of Use and Trademark Notice), section 4.

## Contributing

Issues and pull requests are welcome. Unless you state otherwise, contributions you submit are
licensed under the MIT License, the same as the project. Please do not include credentials, tenant
data or customer information in issues or pull requests.

## Professional services

For training, support, implementation or customization, email
[contact@smekalsolutions.com](mailto:contact@smekalsolutions.com) or visit
[smekalsolutions.com](https://www.smekalsolutions.com).
