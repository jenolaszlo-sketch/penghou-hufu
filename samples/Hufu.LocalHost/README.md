# Hufu.LocalHost

A non-packable Windows NTFS operator console showing governed AI tool usage with
the exact published Hufu preview.3 and IO/Luban preview.1 packages. It connects
real Windows identity, protected issuer/approval state, mandatory decision
evidence, a co-located journal and the controlled conditional file writer.

Read [the profile and command manual](../../docs/local-host-services.md) before
integrating it. The console is a trusted operator channel. Expose only bounded
preview/apply calls to agents; this does not isolate arbitrary code running under
the operator's Windows account. Each session covers one exact UTF-8 target in a
new private workspace, with complete bounded review and expiring exact approval.

```powershell
dotnet build samples/Hufu.LocalHost/Hufu.LocalHost.csproj -c Release
dotnet samples/Hufu.LocalHost/bin/Release/net10.0-windows/Hufu.LocalHost.dll help
dotnet test tests/Hufu.LocalHost.Tests/Hufu.LocalHost.Tests.csproj -c Release
./eng/Test-LocalHost.ps1
```

The smoke test allocates and cleans only its own GUID qualification directories,
with fixture content; it does not approve real work. This application is separate
from product hosts and requires no new NuGet publication.
