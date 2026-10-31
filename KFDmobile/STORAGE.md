# Container storage

Open the gear menu or Container > Storage settings. Choose Local, SMB, SFTP, or
HTTPS/WebDAV. One connection profile is active at a time. Switching profiles saves
and closes the current container first; existing files are not moved or deleted.

## Connection settings

- SMB: hostname/IP, share, folder, username/password and optional domain. Uses
  SMB2 on TCP 445, not SMB1. The container remains encrypted; SMB transport is not
  encrypted by this implementation. Use a trusted LAN or VPN.
- SFTP: hostname/IP, port, absolute folder, username, password or imported SSH
  private key and optional passphrase. Supply the server SHA256 host fingerprint
  obtained from your administrator. A mismatch is rejected. Replacing existing
  files requires the OpenSSH `posix-rename@openssh.com` extension.
- HTTPS/WebDAV: base URL, relative folder, username and password/app password.
  Requires a trusted HTTPS certificate and PROPFIND/GET/PUT/DELETE support.
  Redirects are rejected. Existing files require a strong ETag and conditional
  PUT support; otherwise use Save as with a new filename.

Browse server folders, then use Test connection and save access. The test creates
random test bytes, reads them back, replaces the file, verifies it and deletes it.
It does not upload a container or any key material.

Connection profiles, including passwords and SSH private keys, are encrypted with
an Android Keystore AES-GCM key. Android app backup is disabled. Container passwords
are not stored in the profile. Opened containers stay in memory while unlocked.

## Saving and recovery

Open container browses the selected destination. Save as creates a new filename
and never intentionally overwrites an existing file. Normal saves update the opened
remote container and create a separate timestamped encrypted backup beside it.
Both files are read back and verified before remote success is reported.

Enable **Also save locally on every remote save** to keep a verified encrypted
local copy as well as the remote container and remote timestamped backup. Each
local copy uses the container name, date/time and a unique suffix.

Even with that option off, any failed remote save or remote backup automatically
attempts an encrypted local recovery copy. This covers unavailable Wi-Fi/cellular,
server failures and permission errors. The app says **Saved locally — remote save
pending**, preserves the remote version baseline, and allows closing only after a
durable copy exists. If both destinations fail, edits remain in memory and a failure
is shown. Do not end the app until a save succeeds.

Use **Container > Local recovery copies** and the container password to reopen a
copy. Save container retries the same server path when the connection profile still
matches, or Save as selects a new destination. Recovery copies live in the app's
private storage, not Downloads; Android deletes them when uninstalling or clearing
app data. Reopen and save them to your chosen destination before uninstalling.
There is no automatic background synchronization queue or automatic backup pruning.

Remote conflicts preserve edits and offer Save a copy. SMB/SFTP use exclusive
cooperative lock files, hash rechecks and atomic replacement. Other programs that
ignore those locks can still race the final replacement; avoid concurrent editing
with external clients. After an interrupted connection, a stale `.kfd-lock` may
require administrator removal only after confirming no writer is active. WebDAV
uses server-enforced conditional ETags. A server that ignores conditional requests
is not suitable for shared editing.

Local plaintext MR key exports and DKF export are disabled by default for remote
profiles and can be enabled separately in Storage settings. Import received keys
into an open container to save them encrypted with the selected storage policy.

## Validation

Automated tests cover encryption/recovery, save coordination, remote conflicts,
read-back failures, bounded reads, WebDAV conditional requests, redirect rejection,
and XML entity rejection. Loopback SFTP and SMB tests use synthetic data only.
Android Keystore/UI behavior, real NAS/WebDAV compatibility, network interruption
on a phone, and phone lock/reopen flows require beta testing on the target hardware.
No radio/MR protocol changes are included; send algorithms individually in Emulate
and do not use KVL Keyload-All.


To repeat synthetic network tests, install Paramiko 5.0.0 and Impacket 0.13.1 into
an isolated Python environment and run `python KFDmobile/Tests/storage-smoke-server.py`
from the repository root (ensure `work/storage-smoke-stop` does not already exist).
Set `KFD_SFTP_SMOKE` to the absolute path of `work/storage-smoke-info.json` and
`KFD_SMB_SMOKE_PORT` to the number in `work/smb-smoke-port.txt`, then run the .NET
Tests project. Append `-- --storage-only` for storage checks only. Create
`work/storage-smoke-stop` to shut down the fixture. This uses generated SSH host
keys, synthetic credentials and test bytes only. The Windows fixture adapts
Impacket's POSIX rename implementation to Windows handle semantics; real NAS
compatibility is still a separate validation requirement.
