# Third-party notices

Hunter's Workshop is distributed under GPL-3.0-or-later. See LICENSE. Names and trademarks belong to their respective owners.

## visy / bloodborne_pc_windows_port

Source: https://github.com/visy/bloodborne_pc_windows_port

Research baseline: `97fe3c9733799129d484a331fcfa69d3c7870df4` (0.6.7).
Additional verified release: `f2abe115a7c15b693cc7a060ca425e1414e90921` (0.6.8).

GPL-3.0. Copyright belongs to the upstream contributors identified in that repository. `src/Commands.cs` adapts the main-thread game-call and dynamic gem instance sequence documented by the upstream party inventory implementation. The emitted machine code, Windows process bridge, desktop application, backup code and tests are maintained here. No upstream port executable or game binary is redistributed.

## Noxde / Bloodborne-save-editor

Source: https://github.com/Noxde/Bloodborne-save-editor

Pinned revision: `b8eecb5d2a73066954cdb54b65349ea65b1ded62`.

GPL-3.0. Copyright belongs to Noxde and the upstream contributors. Item names, descriptions, gem/rune names, effect descriptions and ratings in `data/catalog.json` are adapted from the repository's resource JSON, joined to locally inspected Bloodborne 1.09 parameter IDs. Catalog preparation is in `dev/prepare_data.py`. Existing game line breaks are normalized for display. Variant classification includes names with apostrophes.

## Artwork

`assets/emblem.png`, `assets/workshop.png` and the derived multi-size `assets/HuntersWorkshop.ico` are original AI-generated app artwork. They contain no extracted game assets. See ASSETS.md. They are provided with this GPL project to the extent copyright applies.

## Development dependencies

The application has no bundled third-party runtime package. Development bridge tests optionally use Unicorn 2.1.4 (GPL-2.0, installed separately through pip). Python, Windows, .NET Framework and GitHub Actions retain their respective licenses and are not redistributed in the release ZIP.
