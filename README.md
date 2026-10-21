# Hunter's Workshop for shadPS4

Item spawner and blood gem studio for **Bloodborne 1.09 (CUSA03173) running in shadPS4** on Windows.
This is the Hunter's Workshop interface and catalog, stripped of everything that only served the visy native port
and re-attached to shadPS4 the same way the Bloodborne shadPS4 mod menu does it.

## Use

1. Start **shadPS4.exe** (not just the Qt launcher) and load a living hunter in your own world.
2. Run `HuntersWorkshop.exe` and click **Connect to Bloodborne**. Wait for **Hunter ready**.
3. Pick an item or build a gem and grant it. Do not grant while shadPS4 is paused (F9); resume first.

Requirements: Windows 10/11 x64, .NET Framework 4.8, the unmodified 1.09 eboot. Back up saves first; the first grant per character also makes a verified snapshot.
Run both programs at the same privilege level.

## How it attaches

shadPS4 runs the PS4 code natively, so the game lives inside `shadPS4.exe` and the 1.09 eboot sits at `0x800000000`.
Connect checks the Give/Create/Tick/GiveList/Release/SaveRequest prologues there, then patches the per-frame `Tick`
function with a jump to a small bridge that runs queued grants on the game's main thread.

Differences from the original that matter on shadPS4:
- Fixed image base (with a scan only as fallback) instead of bbport process/hash detection.
- Thread check while patching skips threads that vanished or cannot be read (shadPS4 churns helper threads; the old check aborted on the first one).
- A hook left by a killed session is removed automatically instead of requiring a game restart.
- A command not picked up within 8 s (paused or loading) is cancelled safely and reported, instead of locking the session until restart.
- Every check logs expected vs actual bytes, and every status change is logged, to `%LOCALAPPDATA%\HuntersWorkshop\activity.log`.

If Connect fails, send that log.

## Build

`.\build.ps1` (Windows .NET Framework compiler, no SDK or NuGet). Output: `bin\HuntersWorkshop.exe`.

## License

GPL-3.0-or-later. See LICENSE, THIRD_PARTY_NOTICES.md and ASSETS.md. Independent community tool, unaffiliated with FromSoftware, Sony or shadPS4.
