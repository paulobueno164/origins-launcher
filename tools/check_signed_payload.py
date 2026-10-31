"""Verify that signing changed only PE checksum, security directory and appended certificate."""
from pathlib import Path
import hashlib, json, struct, sys

def describe(data):
    if data[:2]!=b'MZ': raise ValueError('NOT_A_PE_FILE')
    pe=struct.unpack_from('<I',data,0x3c)[0]
    if data[pe:pe+4]!=b'PE\0\0': raise ValueError('NOT_A_PE_IMAGE')
    opt=pe+24; magic=struct.unpack_from('<H',data,opt)[0]
    if magic not in (0x10b,0x20b): raise ValueError('UNKNOWN_PE_FORMAT')
    checksum=opt+64; security=opt+(96 if magic==0x10b else 112)+4*8
    certificate=struct.unpack_from('<II',data,security)
    normalized=bytearray(data); normalized[checksum:checksum+4]=b'\0'*4;normalized[security:security+8]=b'\0'*8
    return bytes(normalized), certificate

def compare(unsigned,signed):
    before,bc=describe(unsigned); after,ac=describe(signed)
    if bc!=(0,0): raise ValueError('UNSIGNED_BUILD_ALREADY_HAS_CERTIFICATE')
    offset,size=ac
    if not (size>8 and offset>=len(unsigned) and offset%8==0 and offset+size==len(signed)):
        raise ValueError('INVALID_APPENDED_CERTIFICATE_RANGE')
    if after[:len(unsigned)]!=before: raise ValueError('SIGNED_PROGRAM_PAYLOAD_CHANGED')
    if any(after[len(unsigned):offset]): raise ValueError('NONZERO_DATA_BEFORE_CERTIFICATE')
    return {'passed':True,'unsigned_sha256':hashlib.sha256(unsigned).hexdigest(),'signed_sha256':hashlib.sha256(signed).hexdigest(),'program_payload_unchanged':True}

if __name__=='__main__':
    if len(sys.argv)!=3: raise SystemExit('Usage: check_signed_payload.py unsigned.exe signed.exe')
    print(json.dumps(compare(Path(sys.argv[1]).read_bytes(),Path(sys.argv[2]).read_bytes()),indent=2))
