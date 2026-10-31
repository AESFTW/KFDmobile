# Third-party notices

The container format, adapter commands and P25 implementation are derived from
[OmahaCommSys KFDtool](https://github.com/omahacommsys/KFDtool), revision
`fe4dcaf57d040620bfbba251984428d6b8b7728a`, under the MIT license. See
UPSTREAM-LICENSE.txt. The original project attribution is retained in the source.

USB serial control sequences were adapted from
[usb-serial-for-android](https://github.com/mik3y/usb-serial-for-android), under
the MIT license. See USB-SERIAL-LICENSE.txt.

NLog is distributed under the BSD 3-Clause license. System.Security.Cryptography.Xml
and the .NET runtime are distributed under the MIT license. Package license
information is included in their NuGet packages. This is an independent test
port and is not an official Zebra, Samsung or KFDtool release.


## Remote storage dependencies

- SSH.NET 2026.0.0, MIT, source commit `7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e`: https://github.com/sshnet/SSH.NET
- SMBLibrary 1.5.8.1, LGPL-3.0-or-later, source commit `582635541d2ec0c84eac5f66cbebd41e2d35c7ae`: https://github.com/TalAloni/SMBLibrary
- BouncyCastle.Cryptography 2.7.0, MIT: https://github.com/bcgit/bc-csharp/tree/release-2.7.0
- Microsoft.Extensions.Logging.Abstractions 8.0.3 and System.Security.Cryptography.Xml/Pkcs 10.0.13, MIT: https://github.com/dotnet

License texts and the exact, unmodified SMBLibrary source archive are in `ThirdParty/`.
The LGPL applies to SMBLibrary; its inclusion does not change upstream KFDtool's MIT attribution.
Users may modify or replace SMBLibrary and reverse engineer the combined work for debugging
such modifications. The complete KFDmobile source and build instructions accompany each release.
To relink: unpack `ThirdParty/SMBLibrary-1.5.8.1-source.zip`, build/package a modified
SMBLibrary using its included solution, and replace the SMBLibrary PackageReference in
`Core/Core.csproj` with that package or a reference to the rebuilt assembly and dependencies.
Build `App/App.csproj` using the README instructions and sign the resulting APK with your own
Android key. No vendor secret is required to build or install a modified version. Android
requires uninstalling an existing differently signed APK first; export your containers before
uninstalling because Android removes app-private recovery copies and credentials.

The APK includes these license texts under Settings > Third-party licenses.
