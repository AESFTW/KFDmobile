import zipfile,struct,sys,hashlib
from pathlib import Path
apk=Path(sys.argv[1]); z=zipfile.ZipFile(apk)
assert z.testzip() is None
abis=set(); inspected=0
for entry in z.infolist():
    if not entry.filename.startswith('lib/') or not entry.filename.endswith('.so'): continue
    abi=entry.filename.split('/')[1]; abis.add(abi)
    if abi not in ['arm64-v8a','x86_64']: continue
    data=z.read(entry); assert data[:4]==b'\x7fELF'
    off=struct.unpack_from('<Q',data,32)[0]; stride,count=struct.unpack_from('<HH',data,54)
    alignments=[struct.unpack_from('<Q',data,off+i*stride+48)[0] for i in range(count) if struct.unpack_from('<I',data,off+i*stride)[0]==1]
    assert alignments and min(alignments)>=16384,(entry.filename,alignments)
    inspected+=1
assert {'armeabi-v7a','arm64-v8a','x86_64'}<=abis,abis
assert 'classes.dex' in z.namelist() and 'AndroidManifest.xml' in z.namelist()
print('PASS ZIP integrity')
print('PASS ABIs: '+', '.join(sorted(abis)))
print(f'PASS 16 KB ELF segment alignment in {inspected} ARM64/x64 native libraries')
print('SHA256 '+hashlib.sha256(apk.read_bytes()).hexdigest())
print(f'Size: {apk.stat().st_size:,} bytes')
