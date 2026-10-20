# Release verification — 0.2.0

20 October 2026. Windows x64, Bloodborne CUSA03173 1.09. The official visy 0.6.7 and 0.6.8 executable hashes are pinned in `data/profile.json`.

## Automated checks

| Layer | Result | Coverage |
| --- | --- | --- |
| Build | Passed, warnings as errors | x64 Framework build, embedded UI/catalogs/art/icon |
| Catalog and local storage | 42 checks passed | Lost/Uncanny variants, upgrades, invalid quantities/IDs/shapes, six effect slots, packet, atomic preferences, corrupt-file recovery, launch arguments, solo/party backups, manifests and failed logging |
| Exact emitted bridge in Unicorn | 18 cases passed | System V arguments/alignment, once-only claims, loading/death/character/own-world guards, missing managers, failed gem allocation/identity/type, cleanup and save requests |
| Exact bridge on the real Windows CPU | 9 checks passed | Item, five gem shapes, two rune shapes, duplicate suppression, save requests and all 16 YMM registers across deliberately clobbering mocks |
| WPF UI | 13 checks passed | Searches, reinforcement choices, favorites, independent effect slots, clearing extras, rune mode, empty states, preferences and create-button visibility at 1390×930 and 1120×780 |

Unicorn does not implement the AVX XSAVE component used here. That pair is modeled explicitly in emulator tests and independently executed as real instructions by the CPU tests. Mocked calls test bridge mechanics; they do not establish game compatibility.

## Live integration

The final app was tested against both official visy versions using copied saves, config, user directories and Workshop data. Fixtures use loopback-only party networking with UPnP/STUN disabled. Normal source solo and party save hashes matched before and after every completed integration run. Private saves and raw logs are excluded from the repository and release.

Confirmed for 0.6.7 and 0.6.8:

- Attach using exact executable and game-code checks; retain the existing tick hook and restore it on disconnect.
- Add +10 Lost Chikage, +10 Uncanny Rakuyo and Fire Paper. A Pebble at its limit reports no inventory increase.
- Create radial, triangle, waning, circle and droplet gems with Physical ATK UP +27.2% (26619), Add physical ATK +15 (17120), increased stamina costs (41101), and three empty slots. Inspect all eight instance fields after each grant.
- Create standard Moon Caryll rune (1100000, source 6) and verify type, shape and effects.
- Request the game's saving system after successful grants, wait for it to flush, exit and launch a fresh isolated process.
- Verify both +10 weapons, five gem shapes with exact primary/secondary/curse fields, and the rune survive the fresh launch.
- Detect the fixture's `--user` path and create the automatic verified snapshot independently of the normal installation.

The reload check caught a missing save request during development. The shipped bridge now calls the byte-verified 1.09 SaveRequest function after successful grants; the expanded reload test passes with that fix.

## Limits

Results cover representative grants on one Windows PC. Every catalog row, combination, animation, socketing outcome, unused item and real multi-PC synchronization is not tested. Custom gems can exceed natural drop rules. Running-game disk snapshots are best effort; a closed-game backup is strongest. Unknown builds are refused. No claim of zero bugs is made.

Reproduce with `dev/verify.ps1`. The opt-in live fixture is documented in DEVELOPMENT.md.
