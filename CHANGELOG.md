# Changelog

All notable changes to SwiftPack Studio are documented here.

## [1.0.0] - 2026-11-06 (rebuild)

SwiftPack Studio is now open source. The 1.0.0 release assets were rebuilt from the published
source; the version number is unchanged.

### Changed

- SwiftPack Studio source code is now published on GitHub under the MIT License.
- Replaced the End User License Agreement with a Terms of Use and Trademark Notice
  (`Legal/EULA.txt`). The source code is governed by the MIT License (`LICENSE`).
- Updated the Privacy Policy (version 1.1).
- Updated third-party notices (`Legal/NOTICES.txt`), adding Microsoft.Data.Sqlite, SQLitePCLRaw
  and SQLite, and the Apache License 2.0 text.
- Rewrote the README.

### Removed

- Removed the optional Claude API silent-switch lookup from Auto Package. SwiftPack Studio no longer
  contacts `api.anthropic.com`.
- Removed unused internal code (an unused alternate window and a placeholder page).

### Fixed

- Validate Package no longer uses hardcoded user profile paths for Desktop and Start Menu shortcut
  checks; it now uses the current user's folders.

## [1.0.0] - 2026-09-20

### Added

- SwiftPack Studio 1.0.0
- Auto Package workflow
- Create Package
- Validate Source
- Execute Package
- Validate Package
- Create IntuneWin
- Publish to Microsoft Intune
- Documentation generation
- Application source acquisition
- PSADT-based packaging
- MSI creation
- MSI/MST editing

### Distribution

- Official executable distributed through Smekal Solutions.
- Installer naming convention: `SwiftPackStudio-Setup-<version>.exe`
- Official releases are digitally signed by Smekal Solutions.
- Public releases are distributed through GitHub Releases.
