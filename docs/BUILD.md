# Build and validation

The plugin targets `netstandard2.1` and references assemblies from an existing SPT 4.1.6 / EFT 0.16.9.40743 installation. Full interface checks additionally require Fika 2.4.3. Game and dependency assemblies must not be committed or uploaded to CI.

Use a .NET 10 SDK, or a .NET 9 SDK with `-p:TestTargetFramework=net9.0` for the test project. The configuration and native patch harnesses also require Windows .NET Framework 4.7.2-compatible assemblies.

## Local plugin build

From the repository root in PowerShell, provide the path to the client installation:

```powershell
$gameRoot = Read-Host 'SPT client directory'
dotnet build src/ImmersiveVoice.csproj -c Release "-p:GameRoot=$gameRoot" -p:DebugType=None -p:DebugSymbols=false
dotnet run --project tests/Tests.csproj -c Release "-p:GameRoot=$gameRoot" -- "$gameRoot" src/bin/Release/netstandard2.1/ImmersiveVoice.dll
```

The DLL version excludes checkout-specific commit IDs. The installation instructions and corresponding-source archive record the reviewed public release commit.

Only `ImmersiveVoice.dll` belongs in the client plugin directory. Do not distribute reference DLLs or debug symbols.

## Checks without game files

```text
dotnet run --project tests/Tests.csproj -c Release -p:PortableTests=true
python -m unittest discover -s tests -p "test_*.py"
python tools/check_public_release.py
```

Portable tests run the existing pure rules and lifecycle matrix. They explicitly skip checks against EFT/Fika binaries. GitHub Actions runs these portable checks, the Python log parser checks, the publication guard, and a pinned secret scanner.

The full local configuration and native hook fixtures are in `tests-config/` and `tests-native/`. The configuration fixture takes the installed ConfigurationManager DLL path as its runtime argument. These checks do not open a Unity window or launch a raid.

## Packaging

After building the plugin, run:

```text
python tools/package_release.py --dll src/bin/Release/netstandard2.1/ImmersiveVoice.dll --output package
```

The packager uses an explicit file allowlist and verifies archive contents and hashes. The installation ZIP contains the plugin, Chinese installation instructions, GPL license, notices and SHA-256 checksums. A separate corresponding-source ZIP contains the files tracked at the release commit, without Git history, logs or local build artifacts. Package only after the working tree is clean; source archives must correspond to the released binary.

## Squad dialogue protocol (2.15.0)

The Fika adapter resolves the optional API once and registers a fixed nine-integer packet. Fika's installed serializer produces 38 bytes including its packet hash, before transport envelopes. A Covering playback receipt reuses this layout and is accepted only for the granted responder of a cover call. The caller must also hear the matching native clip before considering Ready. No audio, coordinates or persistent account IDs are sent. All Fika peers, including a headless host, must register the same protocol version. Clients send nothing before the host capability message. Audio continues through native Player.Say / PhrasePacket.

The tests include 48 four-human arrival/audio permutations, expiry/reconnect cases, real installed serializer round-trips, and a simulated four-client transport using the production adapter. This is not a live UDP or Unity audio test. Pure-model allocation/timing checks do not measure overall raid CPU usage.
