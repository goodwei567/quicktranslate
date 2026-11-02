# Changelog

All notable changes to QuickTranslate are documented here.

## [1.1.0] - 2026-11-02

### Added

- Task presets directly in the selection popup, synchronized with the settings page.
- Streaming model output, request cancellation, and Markdown rendering.
- Local translation cache, searchable history, favorites, and history export-friendly entries.
- Technical-document translation mode and expanded prompt presets.
- Admin mode for capturing text from windows running with higher integrity.
- Automatic UAC handoff when an elevated target window is detected, with a manual retry action.
- Optional highest-privilege startup task for logon and unlock, restricted to protected installation paths.
- Startup registration repair and atomic local settings/cache/history writes.

### Changed

- Reworked the settings page, assistant popup, and navigation styling.
- Added a compact task selector, model/reasoning controls, and configurable global hotkey.
- Improved clipboard capture cancellation and single-request concurrency handling.

### Fixed

- Explained and handled Windows UIPI failures when translating elevated VS Code or terminal windows.
- Prevented stale task-selection and navigation-highlight state.
- Improved selection capture errors without using stale clipboard contents.

### Security

- Admin mode never bypasses UAC and does not install a resident privilege service.
- Elevated network traffic is limited to loopback Magpie endpoints.
- Highest-privilege startup tasks are not created from user-writable program directories.

## [1.0.0] - 2026-11-01

### Added

- Global selection translation through a local Magpie OpenAI-compatible API.
- Always-on-top assistant window with configurable task prompts and follow-up questions.
- Shared target-language selector and dynamically loaded model list.
- Configurable global hotkey and prompt presets.
- System tray integration, close-to-tray behavior, and optional startup at sign-in.
- Single-instance behavior and x64 self-contained publishing.
