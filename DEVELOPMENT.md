# Development

## Build

Windows x64 with .NET Framework 4.8 is required. `build.ps1` invokes the Windows Framework C# compiler with optimization, UTF-8 source, x64 output, an asInvoker manifest, embedded UI/catalog/artwork and warnings as errors. The output is `bin/HuntersWorkshop.exe`. No NuGet dependencies are used.

```powershell
.\build.ps1
python -m pip install -r dev\requirements.txt
.\dev\verify.ps1
.\package.ps1
```

## Architecture

- `App.cs`, `Main.xaml`, `Theme.xaml`: WPF UI. Game and backup operations run on a worker thread while grants are disabled. State is inspected once per second.
- `Catalog.cs`: embedded parameter catalog, typed validation and compatibility profiles.
- `Engine.cs`, `Native.cs`: strict executable hashes, game function verification, process memory access, safe tick-hook installation/removal, command queue, inventory verification and timeout handling.
- `Commands.cs`: emitted x64 System V bridge. Preserves GPRs, flags, the 128-byte red zone and x87/SSE/AVX state. Commands execute at SprjFlipper::Update on the game's main thread and chain the port's verified existing trampoline.
- `Storage.cs`, `ProcessLaunch.cs`: local settings, atomic file replacement, resilient diagnostics, save directory detection and verified disk snapshots.

The bridge uses one 128-byte command packet with idle/queued/executing/done states. An atomic compare/exchange claims a queued packet exactly once. Character identity, loading, life and own-world status are checked again on the game thread. The app serializes grants and refuses automatic retries after uncertainty. Remote executable allocation is retained until game exit after detachment so an in-flight hook return cannot execute freed memory.

## Tests

`dev/verify.ps1` runs catalog/storage tests, emitted-byte tests in Unicorn, real Windows CPU XSAVE/XRSTOR checks, and UI rendering/interactions. It does not launch the game. Test artifacts and isolated preferences stay under `test-output/` and are ignored by git.

`--self-test OUTPUT` writes the exact bridge bytes and command packet used by the Python tests. `--ui-test OUTPUT` renders the app's own WPF controls and checks disconnected UI behavior. These are developer switches, not normal app usage.

## Opt-in live game verification

Close the game first. This copies your existing save into a separately named directory, launches only that isolated copy, grants items, waits for saving, terminates only the process it created, and compares normal solo/party save hashes afterwards. Keep fixture directories private; they contain your saves and machine paths.

```powershell
python dev/live_fixture.py --port "D:/Bloodborne/native" --game "D:/Bloodborne/game/CUSA03173" --fixture "D:/PrivateFixtures/workshop-020" --expanded
python dev/live_fixture.py --port "D:/Bloodborne/native" --game "D:/Bloodborne/game/CUSA03173" --fixture "D:/PrivateFixtures/workshop-020" --reuse --verify-existing
```

The harness uses the port's optional verified renderer settings, a private config, private user/data directories, fixture party settings with UPnP and STUN disabled, and a private Workshop data root. It requires an already playable character. It is never run by CI. The probe validates new gem fields in live memory and, on reload, verifies persistent weapon and gem inventory entries. It does not prove every item/effect combination or real multi-PC synchronization.

## Catalog maintenance

Normal builds use the committed catalog. To regenerate it, obtain the pinned Noxde resources identified in THIRD_PARTY_NOTICES.md and export your own 1.09 parameter rows to JSON. Run `python dev/prepare_data.py --params path/to/params.json --names path/to/save-editor/src-tauri/resources`. Do not commit game files or save fixtures. Executable compatibility hashes must only be added after verifying the exact release, function checks and live integration behavior.

## Packaging

`package.ps1` stages only the executable, its framework config, README, license, changelog, testing report and notices, then creates the versioned ZIP and SHA256SUMS. Personal settings, saves, logs and test artifacts are never part of the archive. Source is provided separately by the repository/tag.
