using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OriginsLauncher
{
    internal static partial class TestProgram
    {
        private static void StandardCheck(Config config,string binary)
        {
            string registry=InstalledAppState();
            string[] links={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Origins Conquer.lnk"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Origins Conquer.lnk")};
            var before=links.Select(LinkState).ToArray();
            var assembly=Assembly.LoadFile(Path.GetFullPath(binary));
            var installer=assembly.GetType("OriginsLauncher.Installer");
            Check(installer!=null&&assembly.GetType("OriginsLauncher.InstallDialog")!=null,"original installer and folder dialog are present in the actual assembly");
            Check(assembly.GetType("OriginsLauncher.PortableStartup")==null,"portable startup is excluded from the normal launcher");
            Check(assembly.GetName().Version.ToString(3)=="2.5.8","recovery version is newer than portable 2.5.7");
            Check(assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a=>a.Key=="OriginsDistribution"&&a.Value=="Installed"),"delivered executable identifies its installed distribution");
            var main=assembly.GetType("OriginsLauncher.Program").GetMethod("Main",BindingFlags.Static|BindingFlags.NonPublic);
            byte[] il=main.GetMethodBody().GetILAsByteArray();
            foreach(string method in new[]{"NeedsOwnFolder","IsFirstRun","CreateShortcuts","RegisterUninstall","Uninstall"}) {
                byte[] call=new byte[]{0x28}.Concat(BitConverter.GetBytes(installer.GetMethod(method).MetadataToken)).ToArray();
                Check(Enumerable.Range(0,il.Length-call.Length+1).Any(i=>il.Skip(i).Take(call.Length).SequenceEqual(call)),"actual entry point calls original Installer."+method);
            }
            var start=new ProcessStartInfo(binary,"--version") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(start)) {
                string output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();
                Check(process.WaitForExit(15000)&&process.ExitCode==0&&output.Contains("2.5.8"),"actual launcher reports its version without opening UI or installing");
            }
            Check(registry==InstalledAppState()&&before.SequenceEqual(links.Select(LinkState)),"headless diagnostics did not touch the user's installation or shortcuts");
            var sessions=Enumerable.Range(0,20).Select(_=>Config.FromArgs(new[]{"--dir="+config.InstallDir})).ToArray();
            foreach(var s in sessions){s.EnableSessionLogin();LoginClient.WriteTokenFile(s,s.SessionKey);}
            Check(sessions.Select(s=>s.SessionKey).Distinct().Count()==20&&sessions.All(s=>LoginClient.ReadTokenFile(s)==s.SessionKey),"20 launcher sessions preserve independent login tokens");
            foreach(var s in sessions)new Updater(s).CleanupSessionFiles();
        }
    }
}
