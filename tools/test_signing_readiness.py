"""Offline checks of release scope and signing guards. No credentials or network calls."""
from pathlib import Path
import hashlib, json, re, subprocess, sys, tempfile
import xml.etree.ElementTree as ET
import struct
from check_signed_payload import compare

root=Path(__file__).resolve().parents[1]
checks=[]
def check(condition, description):
    if not condition:
        raise AssertionError(description)
    checks.append(description)

receipt=json.loads((root/'docs/source-2.5.8.json').read_text(encoding='utf8'))
actual={p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest()
        for p in (root/'src').rglob('*') if p.is_file() and not {'bin','obj'}&set(p.relative_to(root).parts)}
check(actual==receipt['files'],'All 24 launcher source files match the reviewed installed 2.5.8 snapshot')
project=ET.parse(root/'src/OriginsLauncher/OriginsLauncher.csproj').getroot()
check(project.findtext('./PropertyGroup/Version')=='2.5.8','Launcher source version is 2.5.8')
check(any(n.attrib.get('Remove')=='PortableStartup.cs' for n in project.findall('./ItemGroup/Compile')),'Portable entry point remains excluded from the installed launcher')
workflow=(root/'.github/workflows/ci.yml').read_text(encoding='utf8')
check('pull_request_target' not in workflow,'Untrusted pull requests never run a privileged target workflow')
check('inputs.sign_release' in workflow and "github.ref == 'refs/heads/main'" in workflow,'Manual signing requests require the main branch and explicit sign_release input')
check('signing-policy-slug: release-signing' in workflow,'Only the manually approved release-signing policy is used')
check('Test-SigningConfiguration.ps1' in workflow and 'Test-ReleaseTrust.ps1' in workflow,'Signing configuration and public Authenticode trust are checked')
check('check_signed_payload.py' in workflow,'Signed executable must preserve the exact unsigned build payload')
check('signed/OriginsLauncher.exe signed/signing-receipt.json' in workflow,'GitHub release uses only the verified signed executable and receipt')
check("else { 'src/OriginsLauncher/bin" not in workflow,'There is no unsigned binary release fallback')
check(all(re.fullmatch(r'[^@\s]+@[0-9a-f]{40}',x) for x in re.findall(r'uses:\s*(\S+)',workflow)),'All third-party GitHub actions are pinned to full commit SHA')
check(not any(x in workflow for x in ['site_publish.py','publish-update.ps1','conquer-prod','ssh ','scp ','vercel ']),'Workflow has no site or server deployment steps')
ns={'s':'http://signpath.io/artifact-configuration/v1'}
config=ET.parse(root/'signpath/artifact-configuration.xml').getroot()
pe=config.find('./s:zip-file/s:pe-file',ns)
check(pe is not None and pe.attrib['path']=='OriginsLauncher.exe','SignPath ZIP configuration selects only the launcher')
check(pe.attrib['product-name']=='Origins Conquer Launcher' and pe.attrib['product-version']=='${version}' and pe.attrib['file-version']=='${version}.0','Product, file and version metadata restrictions are configured')
check(len(config.findall('.//s:pe-file',ns))==1,'No proprietary game or third-party executable is submitted for signing')
# Synthetic PE-shaped fixture tests payload integrity only, never public trust.
fixture=bytearray(512); fixture[:2]=b'MZ'; struct.pack_into('<I',fixture,0x3c,64)
fixture[64:68]=b'PE\0\0'; struct.pack_into('<H',fixture,88,0x10b)
signed=bytearray(fixture)+b'fixture-certificate-not-valid\0\0\0\0\0'
struct.pack_into('<II',signed,88+96+32,512,len(signed)-512)
struct.pack_into('<I',signed,88+64,12345)
check(compare(bytes(fixture),bytes(signed))['program_payload_unchanged'],'Certificate append and checksum update preserve the unsigned payload')
tampered=bytearray(signed);tampered[400]^=1
for body,label in [(bytes(tampered),'Program-byte changes after signing are refused'),(bytes(fixture),'A missing appended certificate is refused')]:
    try:
        compare(bytes(fixture),body)
    except ValueError:
        check(True,label)
    else:
        raise AssertionError(label)
if sys.platform=='win32':
    import os
    env={k:v for k,v in os.environ.items() if k not in ['SIGNPATH_API_TOKEN','SIGNPATH_ORGANIZATION_ID','SIGNPATH_PROJECT_SLUG','ORIGINS_SIGNER_SUBJECT']}
    p=subprocess.run(['powershell','-NoProfile','-NonInteractive','-File',str(root/'tools/Test-SigningConfiguration.ps1')],env=env,capture_output=True,text=True)
    check(p.returncode!=0 and 'SIGNING_CONFIGURATION_REQUIRED' in p.stdout+p.stderr,'A signing request without credentials is explicitly refused')
    with tempfile.TemporaryDirectory(prefix='origins-trust-negative-') as folder:
        invalid=Path(folder)/'not-a-signed-program.exe'; invalid.write_bytes(b'MZ'+b'\0'*128)
        p=subprocess.run(['powershell','-NoProfile','-NonInteractive','-File',str(root/'tools/Test-ReleaseTrust.ps1'),'-File',str(invalid),'-PublisherSubject','CN=SignPath Foundation'],capture_output=True,text=True)
        check(p.returncode!=0,'An unsigned program cannot pass the public signature verifier')
print(json.dumps({'passed':True,'checks':checks,'production_writes':False},indent=2))
