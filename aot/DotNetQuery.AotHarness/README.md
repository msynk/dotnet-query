# DotNetQuery.AotHarness

A console harness that exercises the generic-heavy hot paths of `DotNetQuery.Core` (cache, observers,
mutations, infinite queries, persistence) so NativeAOT and trimming can be verified end-to-end.

The project sets `PublishAot=true` and `TreatWarningsAsErrors=true`, so any `IL2xxx` (trim) or `IL3xxx`
(AOT) analyzer warning fails the build.

## Verifying

```bash
# Full NativeAOT publish - must complete with zero IL2xxx/IL3xxx warnings and produce a native binary.
dotnet publish aot/DotNetQuery.AotHarness -c Release -r win-x64      # or linux-x64 / osx-arm64

# Run the produced native binary; exit code 0 == all runtime checks passed.
./aot/DotNetQuery.AotHarness/bin/Release/net10.0/win-x64/publish/DotNetQuery.AotHarness.exe
```

A NativeAOT publish requires the platform C/C++ toolchain (on Windows, the Visual Studio "Desktop
development with C++" workload; on Linux, `clang` + `zlib`). The trim/AOT **analysis** runs during IL
compilation and surfaces every warning even before the native link step.

## What a clean run proves

- The core's `IsAotCompatible` annotations are accurate - no reflection or dynamic code on the exercised
  paths.
- The published app is self-contained and starts without the shared .NET runtime.
