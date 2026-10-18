# Origins Conquer launcher

Installer, updater and launcher for the [Origins Conquer](https://originsco.online) game client
(classic rules on the official patch 6609 client). Players download only `OriginsLauncher.exe` from the website;
it installs the game, keeps it up to date, logs the player in and starts it.

Open source under the [MIT license](LICENSE). The game client itself (and its art) is not part of
this repository: the launcher downloads it from originsco.online.

## Features

- **Install:** on the first run outside an empty folder it asks where to install (default
  `C:\Origins Conquer`), copies itself there and creates desktop and Start menu shortcuts. The 6609 client is
  about 110k files, 4.0 GB installed.
- **First download:** the whole client comes as 32 MB parts (`pack/<id>.NN.bin`), 6 at a time,
  each resumable with HTTP ranges and checked with SHA-256, instead of ~110k small files.
- **Updates:** `updates/manifest.json` lists every file with size and SHA-256; only changed files are
  downloaded (`files/<path>.gz`, 8 at a time, resumable). Every file is verified before it replaces
  the old one, so a cut connection never leaves a broken game. An up-to-date folder asks for the manifest with
  `If-None-Match` and reuses its saved copy on a 304.
- **Verify files:** re-hashes everything and repairs what differs.
- **Languages:** English, Português and Filipino: the launcher's texts, the game's translated files (manifest
  `languages` packs, installed over the English files) and the login port of that language.
- **Login:** the launcher asks for the account and password, gets a single-use token from the site
  (`POST /api/account/launcher-token`) and writes it to `ini\origins.login`, which the patched client sends
  instead of the password.
- **Start:** writes the chosen language's account server (`host:port` from the manifest's `accountServers`) to
  `ini\origins.server`, then runs `Env_DX9\Conquer.exe blacknull` in `Env_DX9` (manifest `gameExe`, `gameArgs`,
  `workingDir`). After the game's graphics-mode change prompt (the client closes and would run TQ's `play.exe`,
  which is not shipped) the launcher starts it again with a fresh token.
- **Client switch:** when the manifest starts another exe than the installed one (the 5095 client's `Conquer.exe`
  to the 6609 client's `Env_DX9\Conquer.exe`), the player's settings files are replaced once and the files the old
  manifest installed that the new one does not have are deleted. Files the player added stay.
- **Self-update:** a newer `launcherVersion` in the manifest replaces the exe and restarts it.
- **Offline:** if the update server is down and the game is installed, PLAY still works.
- **Log:** `.originsupdate\launcher.log` in the game folder.
- **Uninstall:** Windows Settings → Apps → Installed apps → Origins Conquer → Uninstall (or
  `OriginsLauncher.exe --uninstall`): removes the game files, shortcuts, the installed-apps entry and
  the per-user Flash registration the launcher made; files the player added to the folder stay.

The install dialog lists every change it makes to the system: desktop and Start menu shortcuts, the
installed-apps entry, and (only when Windows has no Flash) a per-user registration of the game's
Flash component, which the game's login screen needs. No administrator rights are used.

Since 2.0.0 there is no options menu: the 5095 client's Magpie full screen ("keep 4:3") and "leaving the game
goes back to login" (a 5095 exe patch) are gone; the 6609 client has native resolutions. Old `settings.json` keys
(`keepAspect`, `closeOnExit`, `fillScreen`) are ignored and dropped on the next save.

## Privacy

The launcher only talks to `originsco.online`: the update manifest and game files, the news (the
request carries the chosen language), the background image and the login. The account and password typed in the
launcher go over HTTPS to the site only to get a single-use login token; they are never written to disk and are
forgotten when the game closes. It collects and sends no other personal data, files or telemetry; requests carry
only the launcher version in the User-Agent.

## Building

Windows with the .NET 10 SDK (the launcher targets .NET Framework 4.8, which ships with Windows 10/11):

```powershell
dotnet build src\OriginsLauncher\OriginsLauncher.csproj -c Release
```

Every push is built by [GitHub Actions](.github/workflows/build.yml); tags `v*` publish a release.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- Only binaries built by this repository's GitHub Actions workflow are submitted for signing.
- Committers and reviewers: [paulobueno164](https://github.com/paulobueno164)
- Approvers (every release signing request is approved by hand): [paulobueno164](https://github.com/paulobueno164)

The policy is also published at <https://originsco.online/assinatura.html>.

## Publishing a game update (server side)

```powershell
.\publish-update.ps1                 # client\6609 -> site-data\updates-6609-staging (not served); -DryRun first
.\publish-update.ps1 -Out C:\conquer\site-data\updates -Cutover -Release v2.0.0   # the live set
```

`tools/UpdateBuilder` publishes the client folder as it is (the client build decides what ships), compresses
changed files, builds the full-client pack (again only with `--repack` or when more than 5% of the client
changed) and writes the manifest last, atomically. The language packs come from `client-lang-6609\pt|fil` (only
the files that differ from English). The account server address per language goes into the manifest: the host
from the client's `ini\origins.server` (set by the client build), the ports from the account server's
`Comet.Account.config` (`Network.Port` = English, `LanguagePorts`). The script refuses the live folder without
`-Cutover`. Cutover steps: `docs\launcher-6609.md` in the server repository.

## Testing

`tools/DevUpdateServer` serves an updates folder at `http://127.0.0.1:5099/updates/` and injects
faults: `--cut-every N`, `--rate B/s`, `--latency ms`, `--corrupt file.gz`, `--down`. The launcher
accepts `--dir=`, `--base-url=` and `--site-url=` to point at it.

`tools/run-6609-tests.ps1` runs the whole check headless (no window, the game is never started):
`tools/LauncherTests` compiles the launcher's own `Updater`, `Manifest`, `Login`, `Config` and `Strings` code and,
against DevUpdateServer, installs the client from scratch (closed at 30% of the pack and resumed, with cut
downloads), compares every file's SHA-256 with the client folder and the language pack, switches languages,
repairs deleted/truncated/corrupted files, updates a 5095 install, checks the conditional manifest, offline play,
a 1.5.0 `settings.json`, and what would be started (`Updater.StartProcess` replaced) with `ini\origins.server`
and `ini\origins.login` per language.

---

**Português:** launcher e atualizador do Origins Conquer (cliente 6609), código aberto (MIT). Baixa o jogo em
pacotes grandes com retomada, confere cada arquivo por SHA-256, atualiza só o que mudou, faz o login por token e
se atualiza sozinho. Fala apenas com originsco.online e não envia dados pessoais além do login. Política de
assinatura: <https://originsco.online/assinatura.html>.
