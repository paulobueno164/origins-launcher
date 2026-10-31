# Origins Conquer Launcher 2.5.8

Open-source Windows installer, updater and launcher for [Origins Conquer](https://www.originsco.online/).
The launcher is [MIT licensed](LICENSE). The proprietary game client and its artwork are not included in this repository or its CI artifacts.

**Signing status: application pending. No SignPath Foundation certificate has been granted to this project.**
The current 2.5.8 download is unsigned. An unsigned CI artifact is not an approved signed release.

## Source and distribution

`src/OriginsLauncher` is the complete source snapshot of the normal, installed launcher 2.5.8 distributed on October 30, 2026. All 24 source/assets/project files were compared byte-for-byte against the production source on October 31. Their SHA-256 hashes and the existing published executable's hash are recorded in [source-2.5.8.json](docs/source-2.5.8.json).

That receipt identifies the existing distribution. A rebuild on another runner can have different compiler/debug metadata and a different file hash; it is not a claim of reproducible identical binaries. A future public signature must come from a reviewed GitHub-hosted build, not an uploaded production executable.

Publishing this repository **does not deploy the game, login, API, website or update manifest**. GitHub releases and production deployments are separate operations. Existing server-side publisher scripts are legacy development tools and are never called by CI.

## Features

- Normal installed distribution: folder selection, per-user Desktop/Start menu shortcuts, Installed Apps entry, and uninstall support. `PortableStartup.cs` remains as historical test source and is explicitly excluded from the launcher assembly.
- Resumable multipart first download, incremental gzip updates, SHA-256 integrity checks, cached manifest/HTTP 304 support, repair, and offline use of an already installed client.
- English, Portuguese and Filipino launcher texts and manifest-selected game language/account-server routes.
- HTTPS login with short-lived one-use tokens. Session-aware clients use a separate token and route file for each launcher, including twenty independent simultaneous sessions.
- Token renewal after disconnects supports manual login without a second launcher password prompt; one account must not overwrite another account's token.
- Direct or configured UDP tunnel route, graphics settings, and client startup based on the manifest.
- Normal self-update retained, including paths containing accents, percent signs and ampersands; received bytes are hashed before replacement.
- `--version` reports the version and writes a diagnostic log without opening a window, creating shortcuts or registering an installation.

The installer discloses its system changes. The game may require a legacy Flash component; when absent, the launcher can register the game-provided component for the current user. No administrator access is requested.

## Privacy and network activity

The launcher contacts the configured Origins website/API, update servers and CDN destinations used by the manifest, and the selected game/tunnel servers. Updates, news, background art, authentication and game routing require these connections.

Credentials entered by the player are sent over HTTPS to the configured authentication API to obtain one-use tokens. Passwords are kept in process memory for that game session, not persisted to disk. Short-lived tokens and route files are written inside the game folder and cleaned up by the session code.

The reconnect monitor inspects the game process's TCP connections and foreground window to manage its own session; it does not capture other applications' network traffic. The optional UDP tunnel transports the game's traffic. The launcher has no analytics uploader or unrelated file/telemetry collection. Its working data and log are in `.originsupdate` in the chosen game folder.

## Build

Windows 10/11 with .NET SDK 10 and the .NET Framework 4.8 targeting pack:

```powershell
dotnet build src/OriginsLauncher/OriginsLauncher.csproj -c Release -p:IncludeSourceRevisionInInformationalVersion=false
dotnet build tools/LauncherTests/LauncherTests.csproj -c Release
```

The application targets .NET Framework 4.8 and remains a single launcher EXE. CI uses .NET SDK 10 for the existing net10.0 maintenance tools; the launcher and headless harness can also build with SDK 8. The informational-version property keeps PE product version `2.5.8` rather than adding the Git revision to SignPath's version-restricted artifact.

## Test without opening the game

```powershell
python tools/test_signing_readiness.py
python tools/test_headless.py
```

Build `LauncherTests` first. The headless suite exercises the actual update/login/session code against temporary loopback HTTP fixtures. The game payload is synthetic and is never executed. It checks EN/PT/FIL installs, no-op updates, corruption repair, interrupted-download resume, multipart packs, cache/304, offline behavior, tokens/replay/disconnects, twenty isolated sessions, and real launcher self-replacement followed only by `--version`.

Tests assert that diagnostics do not alter the user's installation or shortcuts. Results and HTTP responses are saved under the gitignored `artifacts/headless` directory. This is technical validation; it does not claim visual game inspection or a clean-machine SmartScreen result.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

**The attribution above describes the requested sponsorship, not an existing signature or approval.**

- Committer and reviewer: [paulobueno164](https://github.com/paulobueno164).
- Release approver: [paulobueno164](https://github.com/paulobueno164). Every production signature must be manually approved in SignPath's `release-signing` policy.
- Only this project's own `OriginsLauncher.exe`, built from the reviewed repository source on GitHub-hosted runners, may be submitted. No proprietary game, third-party DLL or historical executable is re-signed.
- All maintainers must use MFA for GitHub and SignPath. Never commit signing tokens, private keys or certificates containing private keys.
- [CI](.github/workflows/ci.yml) runs builds/tests on pushes and pull requests. Signing requires a version tag or an explicit manual release request on `main`, complete signing configuration, and Foundation approval.
- A signed release must preserve the unsigned program payload and pass Windows Authenticode, exact publisher identity, code-signing EKU and timestamp verification. Failure stops publication; there is no unsigned-release fallback.
- A signature proves publisher/build integrity. It does not guarantee that every Windows reputation or antivirus decision will be warning-free.

See [SignPath setup](docs/signpath.md) for the artifact configuration, secrets/variables, approval and release procedure. No production service credentials are needed or stored in this repository.

## Version history

The [2.5.8 notes](docs/release-2.5.8.md) describe the code synchronized after the last public GitHub release (2.4.0). The normal installed launcher is retained; the temporary portable build is not the release target.
