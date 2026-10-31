# Launcher 2.5.8 source synchronization

This source update brings the public repository forward from 2.4.0 to the normal installed launcher already distributed on October 30, 2026. It does not publish a new production binary or modify a game server.

- Separate per-launcher login tokens and route files prevent simultaneous accounts sharing a folder from overwriting one another. The tests cover twenty independent sessions and consumed-token/disconnect behavior.
- Atomic file operations and self-update fixes preserve downloaded bytes and support installation paths containing accents, percent signs and ampersands.
- The normal installer, folder-selection dialog, Desktop/Start menu shortcuts, Installed Apps registration and uninstall support are restored after the temporary 2.5.7 portable experiment. Portable startup is excluded from the actual executable.
- `--version` provides a headless diagnostic path without installing or opening the game.
- Existing login, languages, update/repair/resume, pack, screen and route functionality is preserved by copying the verified 2.5.8 source without runtime edits.

The source receipt is `source-2.5.8.json`. Signing preparation adds independently testable CI guards, PE payload comparison, exact publisher/timestamp verification, and a single-executable SignPath configuration. These changes affect release tooling and documentation only.

The Foundation application is pending. Until a genuine trusted signature is verified, this version must be described as unsigned. No claim of a warning-free SmartScreen result or visual game validation is made.
