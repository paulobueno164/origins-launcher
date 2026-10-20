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
- **Logging in again (2.3.0):** after a disconnect the client goes back to its own login screen. The launcher stays
  with the game until it closes and keeps a fresh token in `ini\origins.login` whenever the game is at that screen
  (no server connection for 2 seconds, seen in the game process's own TCP table): a new one after the last was
  read, and an unread one renewed before it ends, a few times per wait. With several game windows from the same
  folder only the window in front has its token in the file. Tokens are asked with `"Relogin": true`; a site that
  answers `expiresIn` gives them a longer life (an older site: 90 seconds).
- **Start:** writes the chosen language's account server (`host:port` from the manifest's `accountServers`) to
  `ini\origins.server`, then runs `Env_DX9\Conquer.exe blacknull` in `Env_DX9` (manifest `gameExe`, `gameArgs`,
  `workingDir`). After the game's graphics-mode change prompt (the client closes and would run TQ's `play.exe`,
  which is not shipped) the launcher starts it again with a fresh token.
- **Route (2.4.0):** a Route button picks how the game reaches the servers: the UDP tunnel (default) or direct.
  With the tunnel the game connects to `127.0.0.1` and the launcher carries its TCP connections over one UDP
  session to the gateway next to the game servers (`updates/tunnel.json`: host, port, game port; `"enabled":
  false` or no file = direct for everyone). Every frame is sent twice, 20 ms apart, so a lost packet costs ~20 ms
  instead of a TCP retransmission (hundreds of ms on a long route). `ini\origins.server` gets the tunnel's local
  login port; when the tunnel dies (10 s without an answer) the game's connections close and, at the game's login
  screen, the file gets the account server's own address until the tunnel is back (reopened in the background). A
  gateway that doesn't answer the handshake (~4.5 s) means direct for that session.
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
launcher go over HTTPS to the site only to get single-use login tokens (for the game's first login and its logins
after a disconnect); they are never written to disk and are forgotten when the game closes. To know when the game
is at its login screen the launcher looks at which connections the game process has open and which window is in
front; it reads no traffic. Since 2.4.0, with the UDP tunnel on, the game's own connections to the game servers
pass through the launcher, which forwards them to the gateway on the game server without reading them. It collects
and sends no other personal data, files or telemetry; requests carry only the launcher version in the User-Agent.

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
and `ini\origins.login` per language. `LauncherTests relogin --dir D` (no update server needed) checks 2.3.0's
login tokens: the site's answers, the file written and read the way the client opens it, the TCP probe, and the
keeper against a stand-in game and site (disconnect and login again, a login going on, idle renewals, two game
windows, site failures). `LauncherTests route --dir D [--gateway H:P --login P --game P]` checks 2.4.0's route:
every way to "direct" (no or disabled or broken `tunnel.json`, a slow or down site with and without a saved copy,
a gateway that never answers, the player's choice) and, with a gateway (the dev stack's), a real tunnel through a
UDP relay the test cuts and restores: the game's connection through it, the address the keeper writes at the
login screen, a restart that reopens the session, the background reconnect. The tunnel's own protocol tests are
in the server repo (`tools/Comet.TunnelTest selftest`).

---

**Português:** launcher e atualizador do Origins Conquer (cliente 6609), código aberto (MIT). Baixa o jogo em
pacotes grandes com retomada, confere cada arquivo por SHA-256, atualiza só o que mudou, faz o login por token
(também depois de uma queda de conexão, pela tela de login do jogo), leva a conexão do jogo por um túnel UDP que
perde menos tempo com pacotes perdidos (ou direto, à escolha) e se atualiza sozinho. Fala apenas com originsco.online e não envia dados pessoais além do login. Política de
assinatura: <https://originsco.online/assinatura.html>.
