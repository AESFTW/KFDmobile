# KFDmobile

KFDmobile is an independent Android fork/port of [KFDtool](https://github.com/KFDtool/KFDtool), based on the [OmahaCommSys fork](https://github.com/omahacommsys/KFDtool) of the Windows application. This repository starts with the 1.0.0 source snapshot and preserves upstream credits and licenses.

See [release history](CHANGELOG.md), [upstream credits](CREDITS.md), and [downloadable releases](https://github.com/AESFTW/KFDmobile/releases).

Android 8+ application for KFDtool container management and USB keyloader communication. Current version: 1.0.0

Source is in `KFDmobile/`. Startup of 1.0.0 was verified on a Samsung XCover7 Pro running Android 16.

## Build and Test

Install .NET SDK 10.0.401, the Android workload (`dotnet workload install android`), Java 21, and Android SDK platform/build tools 36. Configure the Android SDK and Java paths for your machine.

```powershell
cd KFDmobile
dotnet run --project Tests/Tests.csproj
dotnet build App/App.csproj -c Release -t:SignAndroidPackage -p:AndroidSdkDirectory="YOUR_ANDROID_SDK" -p:JavaSdkDirectory="YOUR_JDK_21"
```

The startup fix requires `AndroidEnableMarshalMethods=false`; retain this project setting. Signing keys are not in the repository. A newly generated signing key cannot update an APK signed with the existing release key.

## Features and Validation

- Encrypted EKC containers, keys and groups; password-protected local DKF export.
- USB communication, MR emulation, received-key export and optional container import.
- Light, dark and system display modes.
- Original KFD100 firmware updater and upstream flashing links for other devices.

See [storage setup](KFDmobile/STORAGE.md) and [validation guidance](KFDmobile/TESTING.md). Emulate Keyload-All may fail: transfer ARC4, AES and DES individually. MR export writes plaintext key data to device Downloads when explicitly used. The included desktop EKC fixture contains synthetic test data only.

Public source history starts at 1.0.0. The production APK uses the existing release signing identity; signing keys are not distributed.

Upstream attribution and dependency licenses are in `KFDmobile/THIRD-PARTY-NOTICES.md` and the accompanying license files.
