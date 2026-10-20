# Artwork and app icon

Created using the built-in OpenAI image generation tool. No reference images, game assets, official symbols, logos, or extracted artwork were supplied. The resulting raster files are embedded in the standalone executable.

| Asset | Final path | Purpose |
| --- | --- | --- |
| Transparent emblem | `assets/emblem.png` | App identity and header |
| Workshop illustration | `assets/workshop.png` | Wide header atmosphere |
| Multi-resolution icon | `assets/HuntersWorkshop.ico` | Windows executable, taskbar and shortcut |

Production prompt set:

**Emblem:** Original independent gothic utility app icon, centered on a genuinely transparent background. A bold blood-red faceted gem inside a weathered silver Victorian lantern arch, with a crescent above it. Premium, restrained detail, strong silhouette readable at 32 pixels. No text, letters, watermark, official Bloodborne marks, or existing game assets.

**Workshop:** Original premium painterly Victorian cathedral workshop at night. Blue-black and charcoal tones with a restrained ruby glow. A ruby lantern, work table and candles concentrated in the right third; atmospheric dark negative space on the left for interface text. Wide 3:1 composition. No characters, weapons, game assets, text, logos or watermark.

The ICO was exported from the transparent emblem at 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixels using Pillow. The original alpha channel is preserved.
