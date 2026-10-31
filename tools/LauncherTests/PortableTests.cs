using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace OriginsLauncher
{
    internal static partial class TestProgram
    {
        private static void PortableCheck(Config config, string binary)
        {
            Directory.CreateDirectory(config.InstallDir);
            string[] locations = {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };
            var links=locations.Select(p=>Path.Combine(p,"Origins Conquer.lnk")).ToArray();
            var before=links.Select(LinkState).ToArray();
            string registration=InstalledAppState();
            var files=Directory.GetFiles(config.InstallDir,"*",SearchOption.AllDirectories);
            Check(PortableStartup.CanStart(config),"normal startup uses the existing folder and permits play");
            Check(files.SequenceEqual(Directory.GetFiles(config.InstallDir,"*",SearchOption.AllDirectories)),"portable startup neither relocates nor copies files");
            var settings=new Settings{Language="pt"};settings.Save(config);
            Check(File.Exists(Path.Combine(config.InstallDir,".originsupdate","settings.json")),"settings remain inside the portable folder");
            string keep=Path.Combine(config.InstallDir,"player-file.txt");File.WriteAllText(keep,"must survive");
            var process=new ProcessStartInfo(binary,"--uninstall \"--dir="+config.InstallDir.TrimEnd('\\')+"\"") {
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
            };
            using(var child=Process.Start(process)) {
                Check(child.WaitForExit(15000),"actual portable executable handles legacy --uninstall without a window");
                Check(child.HasExited&&child.ExitCode==0,"legacy uninstall command exits successfully without deleting the portable game");
            }
            Check(File.ReadAllText(keep)=="must survive","legacy uninstall preserves player data");
            Check(registration==InstalledAppState(),"Installed Apps registry entry is byte-for-byte unchanged");
            Check(before.SequenceEqual(links.Select(LinkState)),"Desktop and Start menu shortcuts are unchanged");
            var assembly=System.Reflection.Assembly.LoadFile(Path.GetFullPath(binary));
            Check(assembly.GetType("OriginsLauncher.Installer")==null&&assembly.GetType("OriginsLauncher.InstallDialog")==null,"installer and shortcut writer are absent from the delivered assembly");
            Check(assembly.GetType("OriginsLauncher.PortableStartup")!=null,"actual delivered assembly contains portable startup");
            Check(assembly.GetName().Version.ToString(3)=="2.5.7","portable candidate has its own version");
            string strings=System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(binary));
            Check(!strings.Contains("WScript.Shell")&&!strings.Contains("CurrentVersion\\Uninstall\\OriginsConquer"),"no shortcut COM object or Installed Apps path embedded in the artifact");
            var defaultConfig=Config.FromArgs(Array.Empty<string>());
            Check(Path.GetFullPath(defaultConfig.InstallDir)==Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),"default game root is relative to the launcher, independent of the current working directory");
            var sessions=Enumerable.Range(0,20).Select(_=>Config.FromArgs(new[]{"--dir="+config.InstallDir})).ToArray();
            foreach(var session in sessions)session.EnableSessionLogin();
            Check(sessions.Select(s=>s.SessionKey).Distinct().Count()==20,"20 launchers in the portable folder own distinct sessions");
            foreach(var session in sessions){LoginClient.WriteTokenFile(session,session.SessionKey);new Updater(session).WriteServerAddress("127.0.0.1:9958");}
            Check(sessions.All(s=>LoginClient.ReadTokenFile(s)==s.SessionKey),"20 accounts keep their own token and route without clobbering another account");
            new Updater(sessions[0]).CleanupSessionFiles();
            Check(sessions.Skip(1).All(s=>LoginClient.ReadTokenFile(s)==s.SessionKey),"ending one session leaves all other portable sessions intact");
            foreach(var session in sessions)new Updater(session).CleanupSessionFiles();
            Console.WriteLine("PORTABLE_SIDE_EFFECTS: installationRegistry=unchanged shortcuts=unchanged gameOpened=false");
        }
        private static async System.Threading.Tasks.Task PortableCache(Config config)
        {
            var updater=new Updater(config);
            var first=await updater.FetchManifestAsync(System.Threading.CancellationToken.None);
            updater.SaveLocalManifest(first);
            var again=await updater.FetchManifestAsync(System.Threading.CancellationToken.None);
            Check(again.Version==first.Version&&again.Files.Count==first.Files.Count,"HTTP 304 loads the complete saved manifest");
            Check(File.ReadAllText(Path.Combine(config.StateDir,"launcher.log")).Contains("manifest not modified (304)"),"actual conditional HTTP request returned 304");
        }
        private static async System.Threading.Tasks.Task PortablePack(Config config,string lang)
        {
            var updater=new Updater(config);
            var manifest=await updater.FetchManifestAsync(System.Threading.CancellationToken.None);
            int installed=await updater.InstallPackAsync(manifest.Pack,manifest.FilesFor("en"),new SyncProgress(_=>{}),System.Threading.CancellationToken.None);
            Check(installed==manifest.Files.Count,"actual multipart pack installs and hashes every base file");
            var remaining=await updater.FindOutdatedAsync(manifest.FilesFor("en"),true,new SyncProgress(_=>{}),System.Threading.CancellationToken.None);
            Check(remaining.Count==0,"all unpacked SHA256 values match the manifest");
            await updater.UpdateGameAsync(manifest,lang,false,new SyncProgress(_=>{}),System.Threading.CancellationToken.None);
        }
        private static async System.Threading.Tasks.Task PortableSelfSwap(Config config)
        {
            var original=Updater.StartSelfUpdater;
            try {
                Updater.StartSelfUpdater=info=>{
                    var child=Process.Start(info);
                    File.WriteAllText(Path.Combine(config.StateDir,"qa-swap-child.pid"),child.Id.ToString());
                    return child;
                };
                var updater=new Updater(config);
                var manifest=await updater.FetchManifestAsync(System.Threading.CancellationToken.None);
                await updater.StartSelfUpdateAsync(manifest,new SyncProgress(_=>{}),System.Threading.CancellationToken.None);
            } finally {Updater.StartSelfUpdater=original;}
        }
        private static async System.Threading.Tasks.Task PortableSelfUpdateIntegrity(Config config)
        {
            var original=Updater.StartSelfUpdater;int starts=0;
            try {
                Updater.StartSelfUpdater=info=>{starts++;return null;};
                var updater=new Updater(config);
                var manifest=await updater.FetchManifestAsync(System.Threading.CancellationToken.None);
                manifest.LauncherSha256=new string('0',64);bool rejected=false;
                try{await updater.StartSelfUpdateAsync(manifest,new SyncProgress(_=>{}),System.Threading.CancellationToken.None);}
                catch(InvalidDataException){rejected=true;}
                Check(rejected&&starts==0,"wrong launcher hash is refused before any self-update child starts");
                Check(Directory.GetFiles(config.StateDir,"OriginsLauncher.*.new.exe").Length==0&&Directory.GetFiles(config.StateDir,"update-launcher.*.cmd").Length==0,"bad self-update leaves neither executable staging file nor swap script");
            }finally{Updater.StartSelfUpdater=original;}
        }
        private static string LinkState(string path) => File.Exists(path)?Updater.HashFile(path):"absent";
        private static void TokenReplayRegression(Config input)
        {
            var config=Config.FromArgs(new[]{"--dir="+input.InstallDir});config.EnableSessionLogin();
            var keeper=new ReloginKeeper(config,new FakeSite().Mint,Fast());
            var token=NewToken(TimeSpan.FromMinutes(5));
            try {
                keeper.Start(token);
                Check(ClientRead(config)==token.Value,"native packet builder consumes the valid token");
                // Deterministic interleaving: Tick observed the old file, the native
                // client deleted it, then EnsureMineInFile runs before the next Tick.
                typeof(ReloginKeeper).GetMethod("EnsureMineInFile",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(keeper,null);
                Check(LoginClient.ReadTokenFile(config)==null,"consumed single-use token is never resurrected between poll iterations");
            } finally {keeper.Forget();new Updater(config).CleanupSessionFiles();}
        }
        private static string InstalledAppState()
        {
            using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OriginsConquer")) {
                if(key==null)return "absent";
                return string.Join("\n",key.GetValueNames().OrderBy(n=>n).Select(n=>n+"="+key.GetValue(n)+"|"+key.GetValueKind(n)));
            }
        }
    }
}
