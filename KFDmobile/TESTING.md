# Validation guidance

Use only synthetic keys and a suitable test radio. Record app version, phone model/Android version, adapter model/firmware and radio model/firmware. Never include operational keys or passwords in reports.

Verify EKC create/open/save/reopen, incorrect-password rejection, key-length limits, groups, sorting, autosave, local recovery, USB disconnect/reconnect, radio inventory and confirmed loading. Verify remote storage against your own SMB/SFTP/WebDAV server. Check both close-on-leave settings and phone locking; unsuccessful saves must retain unsaved edits.

Host tests exercise container interoperability and mock protocol exchanges. They do not prove hardware compatibility. Startup of 1.0.0 was reported verified on Samsung XCover7 Pro running Android 16. No new hardware validation was performed for public publication.

Known limitation: KVL Keyload-All can fail in Emulate. Transfer ARC4, AES and DES individually. Firmware flashing, radio interoperability and USB timing need validation for each hardware combination.
