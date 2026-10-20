# shadPS4 support (unofficial, untested)

Changed files: src/Engine.cs, src/Storage.cs, src/App.cs, CHANGELOG.md.

Build: run build.ps1 on Windows (same as before). Then start shadPS4, load Bloodborne CUSA03173 1.09,
get into your own world, and click Connect to Bloodborne.

Assumption this port rests on: shadPS4 runs the PS4 executable's x86-64 code natively, so the function
and global offsets in src/Commands.cs (from the bbport build of 1.09) should be identical. This has NOT
been confirmed on a real shadPS4 run. If Connect says the game was not found, or a required function
changed, the offsets differ and need re-deriving. Send the activity log from %LOCALAPPDATA%\\HuntersWorkshop.

Safety: back up your saves first. Grants write through the live game and request an autosave.
Guest grants stay refused. Do not use this on the public Seamless Hunt server with items you would not
want to be flagged for; check the community rules.
