using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OriginsLauncher
{
    internal static partial class TestProgram
    {
        private static async Task SessionLogin(Config input)
        {
            Directory.CreateDirectory(Path.Combine(input.InstallDir,"ini"));
            var a=Config.FromArgs(new[]{"--dir="+input.InstallDir});
            var b=Config.FromArgs(new[]{"--dir="+input.InstallDir});
            a.EnableSessionLogin();b.EnableSessionLogin();
            string key=a.SessionKey;a.EnableSessionLogin();
            Check(key==a.SessionKey && key.Length==32 && a.SessionKey!=b.SessionKey,"one stable private session per launcher, different between launchers");
            string shared=Path.Combine(a.InstallDir,LoginClient.TokenFile);
            File.WriteAllText(shared,new string('f',32));
            string oldRoute=Path.Combine(a.InstallDir,Updater.ServerFile);
            File.WriteAllText(oldRoute,"203.0.113.9:9958");
            var siteA=new FakeSite();var siteB=new FakeSite();
            var ka=new ReloginKeeper(a,siteA.Mint,Fast());var kb=new ReloginKeeper(b,siteB.Mint,Fast());
            var ta=NewToken(TimeSpan.FromMinutes(5));var tb=NewToken(TimeSpan.FromMinutes(5));
            ka.Start(ta);kb.Start(tb);
            Check(LoginClient.ReadTokenFile(a)==ta.Value && LoginClient.ReadTokenFile(b)==tb.Value,"two simultaneous starts keep separate tokens");
            var ua=new Updater(a);var ub=new Updater(b);
            ua.WriteServerAddress("127.0.0.1:61001");ub.WriteServerAddress("127.0.0.1:61002");
            Check(File.ReadAllText(Path.Combine(a.InstallDir,a.SessionFile("server")))=="127.0.0.1:61001" &&
                File.ReadAllText(Path.Combine(b.InstallDir,b.SessionFile("server")))=="127.0.0.1:61002","two sessions keep separate tunnel routes");
            var ga=new FakeGame{Front=false,Others=1};var gb=new FakeGame{Front=false,Others=1};
            using(new Watching(ka,ga))using(new Watching(kb,gb))
            {
                await Task.Delay(400);
                Check(LoginClient.ReadTokenFile(a)==ta.Value && LoginClient.ReadTokenFile(b)==tb.Value,"automatic login works in background: neither keeper removes its private token for another window");
                ga.IsConnected=true;
                Check(ClientRead(a)==ta.Value && LoginClient.ReadTokenFile(b)==tb.Value,"one game consuming its token cannot consume the other account");
                await Task.Delay(650);
                ga.IsConnected=false;
                Check(WaitFor(()=>LoginClient.ReadTokenFile(a)!=null,2000),"disconnect issues a fresh token without a second password dialog");
                Check(siteA.Calls==1 && siteB.Calls==0 && LoginClient.ReadTokenFile(b)==tb.Value,"only the disconnected account renews");
                ga.IsConnected=true;ClientRead(a);await Task.Delay(650);
                Check(LoginClient.ReadTokenFile(a)==null && siteA.Calls==1,"successful reconnection leaves no extra token or repeated session request");
            }
            ka.Forget();kb.Forget();
            Check(File.ReadAllText(shared)==new string('f',32) && File.ReadAllText(oldRoute)=="203.0.113.9:9958","private sessions never touch the legacy token or route");

            var manifest=new Manifest{GameExe=@"Env_DX9\session-child.exe",WorkingDir="Env_DX9",GameArgs="/d /c set ORIGINS_SESSION",SessionLogin=true,
                AccountServers=new Dictionary<string,string>{{"en","203.0.113.7:9958"},{"pt","203.0.113.7:9959"}}};
            string child=Path.Combine(a.InstallDir,manifest.GameExe);Directory.CreateDirectory(Path.GetDirectoryName(child));
            File.Copy(Path.Combine(Environment.SystemDirectory,"cmd.exe"),child,true);
            var saved=Updater.StartProcess;string output=null;string envBefore=Environment.GetEnvironmentVariable("ORIGINS_SESSION");
            string dpiBefore=Environment.GetEnvironmentVariable(Updater.CompatLayer);
            try
            {
                Updater.StartProcess=info=>
                {
                    Check(!info.UseShellExecute && info.EnvironmentVariables["ORIGINS_SESSION"]==a.SessionKey,"process starts with an explicit private-session environment");
                    Check(info.EnvironmentVariables[Updater.CompatLayer]==Updater.HighDpiAware,"DPI awareness preserved for the new process");
                    info.CreateNoWindow=true;info.RedirectStandardOutput=true;
                    var p=Process.Start(info);output=p.StandardOutput.ReadToEnd();p.WaitForExit();return p;
                };
                using(var p=ua.StartGame(manifest,"pt"))Check(p.ExitCode==0 && output.Trim()=="ORIGINS_SESSION="+a.SessionKey,"a real child process receives exactly this session key, without synthetic input");
                Check(File.ReadAllText(Path.Combine(a.InstallDir,a.SessionFile("server")))=="203.0.113.7:9959","language chooses the account port inside the private route");
                Check(Environment.GetEnvironmentVariable("ORIGINS_SESSION")==envBefore && Environment.GetEnvironmentVariable(Updater.CompatLayer)==dpiBefore,"launch does not mutate the parent's session or DPI environment");
                bool refused=false;
                try{new Updater(Config.FromArgs(new[]{"--dir="+a.InstallDir})).StartGame(manifest,"en");}
                catch(InvalidOperationException){refused=true;}
                Check(refused,"session-capable client cannot start before private token paths were selected");
                refused=false;
                try{ua.StartGame(new Manifest{SessionLogin=true},"en");}catch(InvalidDataException){refused=true;}
                Check(refused,"missing account-server configuration fails before starting a private-session client");
            }
            finally{Updater.StartProcess=saved;}
            LoginClient.WriteTokenFile(a,new string('a',32));LoginClient.WriteTokenFile(b,new string('b',32));
            ua.CleanupSessionFiles();
            Check(!File.Exists(LoginClient.TokenPath(a)) && !File.Exists(Path.Combine(a.InstallDir,a.SessionFile("server"))) &&
                LoginClient.ReadTokenFile(b)==new string('b',32) && File.Exists(Path.Combine(b.InstallDir,b.SessionFile("server"))),"cleanup removes only the ended session's token and route");
            File.WriteAllText(Path.Combine(b.InstallDir,b.SessionFile("retry")),"");
            ub.CleanupSessionFiles();
            Check(!File.Exists(Path.Combine(b.InstallDir,b.SessionFile("retry"))),"ended session removes its native retry marker");
            await LingeringConnectionRecovery(input);
            await SlowPrivateHandshake(input);
            var roundtrip=Json.Parse<Manifest>(Json.Serialize(manifest));
            Check(roundtrip.SessionLogin && !Manifest.Fallback().SessionLogin,"manifest capability survives cache roundtrip and old manifests stay manual");
        }
        private static async Task LingeringConnectionRecovery(Config input)
        {
            var cfg=Config.FromArgs(new[]{"--dir="+input.InstallDir});cfg.EnableSessionLogin();
            var updater=new Updater(cfg);updater.WriteServerAddress("127.0.0.1:9958");
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            using var connection=new TcpClient();connection.Connect(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted=listener.AcceptTcpClient();
            var probe=new GameProbe(Process.GetCurrentProcess(),cfg);var site=new FakeSite();var keeper=new ReloginKeeper(cfg,site.Mint,Fast());
            string retry=Path.Combine(cfg.InstallDir,cfg.SessionFile("retry"));
            keeper.Start(NewToken(TimeSpan.FromMinutes(5)));ClientRead(cfg);
            try
            {
                Check(probe.Connected()==true,"reproduced stale account/tunnel TCP still looks connected after game DC");
                using(new Watching(keeper,probe))
                {
                    await Task.Delay(650);Check(site.Calls==0&&LoginClient.ReadTokenFile(cfg)==null,"reproduced old TCP-only decision with no replacement token");
                    for(int cycle=1;cycle<=3;cycle++)
                    {
                        File.WriteAllText(retry,"");
                        Check(probe.Connected()==false,"native retry overrides lingering real TCP, cycle "+cycle);
                        Check(WaitFor(()=>LoginClient.ReadTokenFile(cfg)!=null,2000),"same process/launcher receives fresh token, cycle "+cycle);
                        await Task.Delay(650);
                        Check(site.Calls==cycle&&LoginClient.ReadTokenFile(cfg)!=null,"replacement stays available despite the old socket, cycle "+cycle);
                        ClientRead(cfg);File.Delete(retry); // actual MsgAccount's two deletions
                        await Task.Delay(650);
                        Check(probe.Connected()==true&&LoginClient.ReadTokenFile(cfg)==null&&site.Calls==cycle,"successful relogin clears request without extra credentials, cycle "+cycle);
                    }
                }
                Check(connection.Connected&&accepted.Connected,"all reconnect cycles used the same still-open TCP connection and launcher PID");
            }
            finally {keeper.Forget();updater.CleanupSessionFiles();listener.Stop();}
        }
        private static async Task NativeHandoff(Config input,string client,string script)
        {
            var cfg=Config.FromArgs(new[]{"--dir="+input.InstallDir});cfg.EnableSessionLogin();
            Directory.CreateDirectory(Path.Combine(cfg.InstallDir,"ini"));
            var updater=new Updater(cfg);updater.WriteServerAddress("127.0.0.1:9958");
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            using var connection=new TcpClient();connection.Connect(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted=listener.AcceptTcpClient();
            var probe=new GameProbe(Process.GetCurrentProcess(),cfg);var site=new FakeSite();var keeper=new ReloginKeeper(cfg,site.Mint,Fast());
            keeper.Start(NewToken(TimeSpan.FromMinutes(5)));
            try
            {
                using(new Watching(keeper,probe))
                {
                    var info=new ProcessStartInfo("python",$"\"{script}\" \"{client}\" --bridge \"{cfg.InstallDir.TrimEnd('\\')}\" {cfg.SessionKey}")
                    {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                    using var child=Process.Start(info);
                    var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
                    if(!child.WaitForExit(60000)){child.Kill();throw new TimeoutException("Native handoff test timed out");}
                    string text=await output;string err=await error;Console.Write(text);Console.Write(err);
                    Check(child.ExitCode==0 && text.Contains("NATIVE_KEEPER_PACKET_INTEGRATION_OK"),"actual native packet cave and actual keeper recover three DCs without login timer");
                    await Task.Delay(650);
                    Check(site.Calls==3,"only three fresh tokens minted for three DCs in the same launcher");
                    Check(probe.Connected()==true && connection.Connected && accepted.Connected,"real old TCP connection survives all DCs without hiding native token requests");
                    Check(LoginClient.ReadTokenFile(cfg)==null,"actual native code consumed the final token and removed its retry marker");
                }
            }
            finally {keeper.Forget();updater.CleanupSessionFiles();listener.Stop();}
        }
        private static async Task SlowPrivateHandshake(Config input)
        {
            var cfg=Config.FromArgs(new[]{"--dir="+input.InstallDir});cfg.EnableSessionLogin();
            var site=new FakeSite();var keeper=new ReloginKeeper(cfg,site.Mint,Fast());
            var game=new FakeGame{IsConnected=true};var token=NewToken(TimeSpan.FromMinutes(5));
            keeper.Start(token);
            try
            {
                using(new Watching(keeper,game))
                {
                    await Task.Delay(950); // beyond the accelerated InGameAfter threshold
                    Check(LoginClient.ReadTokenFile(cfg)==token.Value,"slow private handshake keeps its unread token even after TCP looks logged in");
                    Check(ClientRead(cfg)==token.Value,"late native MsgAccount still receives its original single-use token");
                    await Task.Delay(100);
                    game.IsConnected=false;
                    Check(WaitFor(()=>LoginClient.ReadTokenFile(cfg)!=null,2000),"DC after slow handshake replenishes the consumed token in the same launcher");
                    Check(site.Calls==1,"slow handshake and DC mint exactly one replacement, with no repeated password");
                }
            }
            finally {keeper.Forget();new Updater(cfg).CleanupSessionFiles();}
        }
    }
}
