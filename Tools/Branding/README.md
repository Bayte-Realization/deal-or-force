# Brand artwork exports

`node Tools/Branding/build-brand-pack.mjs [output-folder]` converts the selected
masters into studio/game PNG, outlined SVG, ICO, WebP, JPG, TIFF, raster PDF,
social-square and 1080p/4K splash variants. Uses existing Node and ImageMagick;
does not call an image service or install a package.

The private working masters live in ignored `art/branding/2026-10-27/`.
They can be restored from the delivered kit's `00_Sources/`; copy its root
`README_RU.md` there too. They are not a runtime dependency. The game consumes
only the selected PNGs under `game/gfx/interface/` and the root icon files.

Default output: `D:/Godot Projects/Bayte Realization Brand Kit/2026-10-27`.
`00_Sources/PROVENANCE.json` retains the built-in imagegen prompts for the new
parchment plaque and DoF icon. The approved studio image is traced, not redesigned.
SVGs contain paths, not embedded PNGs or third-party font files. Textured parchment
remains raster artwork; 4K exports upscale that source, not its native detail.

`node Tools/Branding/verify-brand-pack.mjs [output-folder]` checks the manifest,
real SVG paths/alpha, all ICO sizes, runtime copies and the new EN/RU strings.
It does not replace visual review at small sizes or in the running game.

Both Windows export (`export_presets.cfg`) and WPF launcher consume `icon.ico`.
Godot's window icon consumes `icon_small.png`. These keep the three-letter **DoF**
monogram. Existing distributed EXEs are not rewritten by this conversion command;
they need a normal subsequent build.
