from pathlib import Path
import datetime,gzip,hashlib,http.server,json,os,shutil,subprocess,threading,time,uuid

source=Path(__file__).resolve().parents[1]
root=source
run=root/'artifacts/headless'/uuid.uuid4().hex[:8];run.mkdir(parents=True)
runner=source/'tools/LauncherTests/bin/Release/net48/LauncherTests.exe'
binary=source/'src/OriginsLauncher/bin/Release/net48/OriginsLauncher.exe'
sha=lambda b:hashlib.sha256(b).hexdigest()
bodies={};requests=[];state=dict(down=False,corrupt=False,cut=False)
# Inert synthetic data: never distribute or execute a proprietary game binary in CI.
game=b'MZ'+bytes(range(256))*32
def row(path,body,url=None,keep=False):
    packed=gzip.compress(body,mtime=0)
    bodies['/files/'+(url or path.replace('\\','/'))+'.gz']=packed
    return dict(path=path,size=len(body),sha256=sha(body),gzSize=len(packed),keep=keep,url=url or path)
base=[row('Env_DX9\\Conquer.exe',game),row('ini\\language.txt',b'en'),row('ini\\GameSetUp.ini',b'[ScreenMode]\r\nScreenModeRecord=4\r\n',keep=True),row('data\\portable-fixture.bin',os.urandom(5*1024*1024))]
manifest=dict(version=999998,generated='LOCAL QA ONLY',launcherVersion='99.99.99',launcherUrl='launcher/OriginsLauncher.exe',launcherSha256=sha(binary.read_bytes()),gameExe='Env_DX9\\Conquer.exe',gameArgs='blacknull',workingDir='Env_DX9',sessionLogin=False,accountServers={k:'127.0.0.1:'+str(9958+i) for i,k in enumerate(['en','pt','fil'])},files=base,languages={k:[row('ini\\language.txt',k.encode(),'lang/'+k)] for k in ['pt','fil']})
pack=b''.join(bodies['/files/'+r['path'].replace('\\','/')+'.gz'] for r in base)
index=json.dumps(dict(id='portable-qa',files=base)).encode();bodies['/pack/index.json']=index
parts=[]
for i,offset in enumerate(range(0,len(pack),512*1024)):
    data=pack[offset:offset+512*1024];url=f'pack/portable-qa.{i:02}.bin';bodies['/'+url]=data;parts.append(dict(url=url,size=len(data),sha256=sha(data)))
manifest['pack']=dict(id='portable-qa',index='pack/index.json',indexSha256=sha(index),size=len(pack),parts=parts)
bodies['/manifest.json']=json.dumps(manifest).encode();bodies['/launcher/OriginsLauncher.exe']=binary.read_bytes()
etag='"'+sha(bodies['/manifest.json'])+'"'
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        path=self.path;requests.append(dict(path=path,range=self.headers.get('Range'),ifNoneMatch=self.headers.get('If-None-Match')))
        if state['down']:self.send_error(503);return
        if path not in bodies:self.send_error(404);return
        body=bodies[path]
        if path=='/manifest.json'and self.headers.get('If-None-Match')==etag:self.send_response(304);self.send_header('ETag',etag);self.end_headers();return
        if state['corrupt']and path.endswith('/data/portable-fixture.bin.gz'):
            state['corrupt']=False;b=bytearray(body);b[len(b)//2]^=1;body=bytes(b)
        offset=int(self.headers.get('Range','bytes=0-').split('=')[1].split('-')[0])
        self.send_response(206 if offset else 200)
        if offset:self.send_header('Content-Range',f'bytes {offset}-{len(body)-1}/{len(body)}')
        self.send_header('Content-Length',str(len(body)-offset))
        if path=='/manifest.json':self.send_header('ETag',etag)
        self.end_headers()
        try:
            if state['cut']and path.endswith('/data/portable-fixture.bin.gz'):
                state['cut']=False;self.wfile.write(body[offset:offset+40000]);self.wfile.flush();self.close_connection=True;return
            self.wfile.write(body[offset:])
        except (ConnectionResetError,BrokenPipeError):pass
    def log_message(self,*args):pass
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
threading.Thread(target=server.serve_forever,daemon=True).start();url=f'http://127.0.0.1:{server.server_port}/'
checks=[]
def save():
    (run/'results.json').write_text(json.dumps(dict(passed=all(r['passed']for r in checks),checks=checks,productionWrites=False,gameOpened=False,screenUsed=False,loginServer='loopback fixture',syntheticGame=True,syntheticGameSha256=sha(game)),indent=2),encoding='utf8')
def call(command,folder,extra=(),expected_exit=0,timeout=150):
    args=[str(runner),command,'--dir',str(folder),'--base-url',url,*extra]
    start=time.monotonic()
    try:
        p=subprocess.run(args,capture_output=True,text=True,encoding='utf8',errors='replace',timeout=timeout)
        output=p.stdout+p.stderr;passed=p.returncode==expected_exit
    except subprocess.TimeoutExpired as e:
        output=str(e);passed=False
    log=run/f'{len(checks):02}-{command}.log';log.write_text(output,encoding='utf8')
    checks.append(dict(command=command,passed=passed,seconds=round(time.monotonic()-start,2),log=log.name,assertions=output.count('  ok  ')))
    save();print(command,'PASS'if passed else'FAIL',str(log),flush=True)
    return passed
try:
    for lang in ['en','pt','fil']:
        folder=run/('folder-'+lang)
        call('standard',folder,['--binary',str(binary)])
        call('update',folder,['--lang',lang,'--full','--expect','4'])
        call('update',folder,['--lang',lang,'--full','--expect','0'])
        target=folder/'Env_DX9/Conquer.exe';b=bytearray(target.read_bytes());b[1200]^=1;target.write_bytes(b)
        call('update',folder,['--lang',lang,'--full','--expect','1'])
        assert target.read_bytes()==game
        call('self-update-integrity',run/('integrity-'+lang),['--lang',lang])
    folder=run/'folder-pt'
    call('settings',run/'settings')
    call('start',folder)
    call('portable-cache',folder)
    call('migrate',folder)
    state['down']=True;call('offline',folder);state['down']=False
    call('portable-pack',run/'pack', ['--lang','fil'])
    target=folder/'data/portable-fixture.bin';target.unlink();state['corrupt']=True
    call('update',folder,['--lang','pt','--full','--expect','1'])
    target.unlink();state['cut']=True
    before=len(requests);call('update',folder,['--lang','pt','--full','--expect','1'])
    assert any(r['range']and r['range']!='bytes=0-'for r in requests[before:]),'RESUME_RANGE_NOT_OBSERVED'
    relocated=run/'pasta movida — São João'
    shutil.copytree(folder,relocated)
    (relocated/'launcher.exe').write_bytes(binary.read_bytes())
    call('update',relocated,['--lang','pt','--full','--expect','0'])
    call('standard',relocated,['--binary',str(relocated/'launcher.exe')])
    for command in ['token-replay-regression','relogin','session-login','route','screen']:
        call(command,run/command)
    for suffix in ['normal','São João % local & portable']:
        swap=run/('self-swap-'+suffix);shutil.copytree(runner.parent,swap)
        driver=swap/runner.name
        p=subprocess.run([str(driver),'self-swap','--dir',str(swap),'--base-url',url,'--version'],capture_output=True,text=True,encoding='utf8',errors='replace',timeout=20)
        deadline=time.monotonic()+12
        while time.monotonic()<deadline:
            if driver.read_bytes()==binary.read_bytes() and 'Launcher version: 2.5.8'in(swap/'.originsupdate/launcher.log').read_text(encoding='utf-8-sig',errors='replace'):break
            time.sleep(.2)
        passed=driver.read_bytes()==binary.read_bytes()and'Launcher version: 2.5.8'in(swap/'.originsupdate/launcher.log').read_text(encoding='utf-8-sig',errors='replace')
        log=run/f'{len(checks):02}-real-self-swap.log';log.write_text(p.stdout+p.stderr,encoding='utf8')
        checks.append(dict(command='real-self-swap',pathVariant=suffix,passed=passed,log=log.name));save();print('real-self-swap',suffix,passed,flush=True)
finally:
    server.shutdown();server.server_close();save();(run/'http-requests.json').write_text(json.dumps(requests,indent=2))
    print('RESULTS',run/'results.json',flush=True)
if not all(r['passed']for r in checks):raise SystemExit(1)
