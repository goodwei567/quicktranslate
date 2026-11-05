# Changelog

All notable changes to QuickTranslate are documented here.

## [1.2.0] - 2026-11-05

### Added

- Free-form assistant window opened by its own global shortcut (default `Ctrl+Shift+Z`), with multi-turn context, streaming answers, cancellation, Markdown rendering, and automatic input focus.
- Independent hotkey settings for selection capture and free-form questions, with duplicate/conflict validation and rollback.
- `QuickTranslate.exe --ask` starts directly in the free-form assistant window.
- "改为自由提问" action when the selection cannot be read, so the failed capture can be replaced by a typed question.
- Model selection and model-list refresh directly inside the free-form assistant.

### Changed

- Redesigned the settings page around compact, clearly grouped rows with more prominent labels and actions.
- Added an animated selection pill to the main-window navigation.
- Reworked both popup composers with a unified rounded input surface, equal-height circular send button, soft focus ring, and vertically centered text.
- Improved foreground activation when reopening the main window from the tray or a second process.

### Fixed

- Cleared stale answer text when retrying a free-form conversation turn.
- Prevented the popup input focus style from drawing a bright native underline.
- Preserved the selection hotkey when the optional free-form hotkey cannot be registered.

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
