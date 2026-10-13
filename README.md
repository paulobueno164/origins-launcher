# Origins Conquer launcher

Installer, updater and launcher for the [Origins Conquer](https://originsco.online) game client
(classic patch 5095). Players download only `OriginsLauncher.exe` from the website; it installs the
game, keeps it up to date and starts it.

Open source under the [MIT license](LICENSE). The game client itself (and its art) is not part of
this repository: the launcher downloads it from originsco.online.

## Features

- **Install:** on the first run outside an empty folder it asks where to install (default
  `C:\Origins Conquer`), copies itself there and creates desktop and Start menu shortcuts.
- **First download:** the whole client comes as a few 32 MB parts (`pack/<id>.NN.bin`), 6 at a time,
  each resumable with HTTP ranges and checked with SHA-256, instead of ~10k small files.
- **Updates:** `updates/manifest.json` lists every file with size and SHA-256; only changed files are
  downloaded (`files/<path>.gz`, 8 at a time, resumable). Every file is verified before it replaces
  the old one, so a cut connection never leaves a broken game.
- **Verify files:** re-hashes everything and repairs what differs.
- **Languages:** English, Português and Filipino (texts and the game's login port per language).
- **Self-update:** a newer `launcherVersion` in the manifest replaces the exe and restarts it.
- **Offline:** if the update server is down and the game is installed, PLAY still works.
- **Log:** `.originsupdate\launcher.log` in the game folder.
- **Uninstall:** Windows Settings → Apps → Installed apps → Origins Conquer → Uninstall (or
  `OriginsLauncher.exe --uninstall`): removes the game files, shortcuts, the installed-apps entry and
  the per-user Flash registration the launcher made; files the player added to the folder stay.

The install dialog lists every change it makes to the system: desktop and Start menu shortcuts, the
installed-apps entry, and (only when Windows has no Flash) a per-user registration of the game's
Flash component, which the game's login screen needs. No administrator rights are used.

## Privacy

The launcher only talks to `originsco.online`: the update manifest and game files, the news (the
request carries the chosen language) and the background image. It collects and sends no personal
data, files or telemetry; requests carry only the launcher version in the User-Agent. Account and
password are typed in the game itself.

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
.\publish-update.ps1            # -DryRun to see what would change
```

`tools/UpdateBuilder` compresses changed files, builds the full-client pack (again only with
`--repack` or when more than 5% of the client changed) and writes the manifest last, atomically.

## Testing

`tools/DevUpdateServer` serves an updates folder at `http://127.0.0.1:5099/updates/` and injects
faults: `--cut-every N`, `--rate B/s`, `--latency ms`, `--corrupt file.gz`, `--down`. The launcher
accepts `--dir=`, `--base-url=` and `--site-url=` to point at it.

---

**Português:** launcher e atualizador do Origins Conquer, código aberto (MIT). Baixa o jogo em
pacotes grandes com retomada, confere cada arquivo por SHA-256, atualiza só o que mudou e se
atualiza sozinho. Fala apenas com originsco.online e não envia dados pessoais. Política de
assinatura: <https://originsco.online/assinatura.html>.
