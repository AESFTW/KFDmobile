# Synthetic loopback integration fixture. Dependencies: paramiko==5.0.0, impacket==0.13.1.
# Run from repository root; stop by creating work/storage-smoke-stop.
import sys, pathlib, socket, threading, os, errno, json, time, struct
sys.path.insert(0,str(pathlib.Path('work/storage-test-deps').resolve()))
import paramiko
from impacket import smbserver, ntlm
# Impacket assumes POSIX rename semantics. Adapt only the synthetic Windows
# server: release its OS handle, atomically replace, then reopen the same SMB ID.
if os.name == 'nt':
 import inspect, textwrap, re
 source=textwrap.dedent(inspect.getsource(smbserver.SMB2Commands.smb2SetInfo)).replace('@staticmethod\n','')
 source=re.sub(r'(?m)^( +)os.rename\(pathName, newPathName\)',lambda m: m[1]+("os.close(connData['OpenedFiles'][fileID]['FileHandle'])\n"+m[1]+"os.replace(pathName, newPathName)\n"+m[1]+"connData['OpenedFiles'][fileID]['FileHandle'] = os.open(newPathName, os.O_RDWR | os.O_BINARY)"),source)
 scope=dict(vars(smbserver));exec(source,scope);smbserver.SMB2Commands.smb2SetInfo=staticmethod(scope['smb2SetInfo'])
root=pathlib.Path('work/storage-smoke-files').resolve();root.mkdir(parents=True,exist_ok=True)
username='kfd-test';password='synthetic-test-password'
class Auth(paramiko.ServerInterface):
 def check_auth_password(self,u,p):return paramiko.AUTH_SUCCESSFUL if (u,p)==(username,password) else paramiko.AUTH_FAILED
 def get_allowed_auths(self,u):return 'password'
 def check_channel_request(self,k,c):return paramiko.OPEN_SUCCEEDED if k=='session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
class Files(paramiko.SFTPServerInterface):
 def local(self,p):
  q=(root/p.lstrip('/')).resolve()
  if q!=root and root not in q.parents:raise OSError(errno.EACCES,'outside test root')
  return q
 def list_folder(self,p):
  try:
   result=[]
   for f in self.local(p).iterdir():
    a=paramiko.SFTPAttributes.from_stat(f.stat());a.filename=f.name;result.append(a)
   return result
  except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
 def stat(self,p):
  try:return paramiko.SFTPAttributes.from_stat(self.local(p).stat())
  except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
 lstat=stat
 def open(self,p,flags,attr):
  try:
   fd=os.open(self.local(p),flags,0o600);mode='r+b' if flags & os.O_RDWR else 'wb' if flags & os.O_WRONLY else 'rb';f=os.fdopen(fd,mode);h=paramiko.SFTPHandle(flags);h.readfile=f;h.writefile=f;return h
  except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
 def remove(self,p):
  try:os.unlink(self.local(p));return paramiko.SFTP_OK
  except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
 def rename(self,a,b):
  try:
   if self.local(b).exists():return paramiko.SFTP_FAILURE
   os.rename(self.local(a),self.local(b));return paramiko.SFTP_OK
  except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
 def posix_rename(self,a,b):
  try:os.replace(self.local(a),self.local(b));return paramiko.SFTP_OK
  except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
class AtomicSFTPServer(paramiko.SFTPServer):
 def _send_server_version(self):
  kind,data=self._read_packet()
  if kind!=1:raise paramiko.SFTPError('Expected SFTP INIT')
  message=paramiko.Message();message.add_int(3);message.add_string('posix-rename@openssh.com');message.add_string('1')
  self._send_packet(2,message)
  return struct.unpack('>I',data[:4])[0]
key=paramiko.RSAKey.generate(2048)
listener=socket.socket();listener.bind(('127.0.0.1',0));listener.listen(8)
def serve():
 while True:
  sock,_=listener.accept()
  def handle(sock):
   t=paramiko.Transport(sock);t.add_server_key(key);t.set_subsystem_handler('sftp',AtomicSFTPServer,Files)
   try:
    t.start_server(server=Auth())
    while t.is_active():time.sleep(.1)
   finally:t.close()
  threading.Thread(target=handle,args=(sock,),daemon=True).start()
threading.Thread(target=serve,daemon=True).start()
server=smbserver.SimpleSMBServer(listenAddress='127.0.0.1',listenPort=0)
server.setSMB2Support(True);server.addShare('STORAGE',str(root),'Synthetic storage test')
server.addCredential(username,0,ntlm.compute_lmhash(password),ntlm.compute_nthash(password))
smbport=server.getServer().server_address[1]
threading.Thread(target=server.start,daemon=True).start()
pathlib.Path('work/smb-smoke-port.txt').write_text(str(smbport))
pathlib.Path('work/storage-smoke-info.json').write_text(json.dumps({'Host':'127.0.0.1','Port':listener.getsockname()[1],'Username':username,'Password':password,'HostFingerprint':key.fingerprint,'Share':'STORAGE','Folder':'/'}))
print('Synthetic loopback SFTP servers ready.',flush=True)
while not pathlib.Path('work/storage-smoke-stop').exists():time.sleep(.2)
listener.close()
server.stop()
