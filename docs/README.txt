YFT LIVERY MAKER
================

Turns livery artwork into GTA V / FiveM livery .yft files, with the texture
as a DDS with 10 mipmaps, and adds everything the car needs (carcols.meta
entries and mod shop names).

No install: just run YftLiveryMaker.exe. It needs .NET Framework 4.8, which
Windows 10 and 11 already have.


HOW TO USE
----------
1. Open car / resource folder...
   Pick the car's resource (the folder with fxmanifest.lua), a whole vehicle
   pack, or your resources folder. Every car that has YFT liveries is listed:
   pick yours. (Or open one of the car's livery .yft files directly.)

2. Add images...  (or drag them onto the window)
   PNG, PSD, TGA, JPG, BMP, TIFF or DDS, painted on the car's livery template.
   Each image becomes one livery. The "Liveries on this car" panel lists every
   livery the car already has, with a preview. For each image, SAVE AS picks:
   - Add as a new livery (the next free number, e.g. mycar_livery_5), or a
     new livery with your own file name
   - Replace one of the car's liveries. You can also drop an image straight
     onto a livery in the panel, or double-click it.
   You can edit the mod shop name and the label. They're filled in to follow
   the car's own naming (e.g. mycar_livery_5 / mycar_LIV5). Renaming a
   replaced livery updates its existing name instead of adding a second one.

3. Texture: leave it on DXT5, "Same as the car's livery", 10 mipmaps.

4. Output, then Make liveries.
   - Save to a new folder: makes a folder laid out like your resource. Copy it
     over the resource and let Windows replace files.
   - Put straight into the car's resource: writes the files in place. Every file
     it changes is backed up first (in Backups, inside the output folder).

5. Restart the resource (or the server). The liveries are in the mod shop
   under Livery.

Dark mode is the default. Switch to Light with the Dark | Light toggle at the
top right; your choice is remembered.


WHAT IT DOES
------------
- Copies one of the car's existing livery .yft files and swaps only the
  livery texture inside it. The model, bones, shader and flags stay exactly as
  the car expects, so the result works like the car's own liveries.
- Makes the texture the way GTA liveries are made: DXT5, the same size as the
  car's livery, 10 mipmaps (2048 down to 4x4), with transparent edges cleaned
  up so there are no dark outlines.
- Re-opens every .yft it writes and checks it: the texture is the new one,
  the shader uses it, and nothing else in the file changed. If a check fails,
  nothing is written for that livery.
- Adds the new liveries to the car's mod kit in carcols.meta (as
  VMT_LIVERY_MOD entries copied from the car's own), and their names to the
  car's vehicle_names.lua. If the car uses plain names as labels, those go
  straight into carcols.meta instead.
- Writes HOW_TO_INSTALL.txt (or WHAT_CHANGED.txt) with exactly what it did.
- Can also save each texture as a .dds file.


GOOD TO KNOW
------------
- 10 mipmaps: GTA stores each mipmap at a quarter the size of the one above,
  and DXT textures can't go below 4x4. So 2048x2048 (and 4096x2048) holds
  exactly 10. A 1024 texture can only hold 9. To get 10 from a small image,
  keep Size on "Same as the car's livery" (usually 2048).
- Paint on the car's livery template. Art made for a different layout will
  land in the wrong places.
- Transparent parts of your image show the car's paint colour through.
- DXT5 works everywhere. BC7 is a little sharper but needs DirectX 11.
  Uncompressed is very large.
- A 4096 DXT5 livery uses about 21 MB. FiveM warns about assets over 16 MB,
  so 2048 is the safe choice.
- Files that start with FXAP are locked by FiveM Asset Escrow, and no tool
  can open them. Pick one of the car's unlocked livery .yft files instead.
- Works with normal (FiveM / legacy) files and with Enhanced edition files.
  The output is the same kind as the file it copies.


COMMAND LINE (optional)
-----------------------
yftlivery.exe does the same job in batch:

  yftlivery scan  "D:\server\resources\[cars]\my_pack"
  yftlivery make  "D:\server\resources\[cars]\my_pack" art1.png art2.png --car mycar --out D:\out
  yftlivery make  ... --replace mycar_livery_3=art3.png   (replace a livery; repeatable)
  yftlivery make  ... --install          (write straight into the resource, with backups)
  yftlivery make  ... --format DXT5 --mips 10 --size match|keep|1024|2048|4096

Other commands: inspect (show what's inside a .yft), extract (embedded
textures to .dds), dds (image to .dds), decode (image or .dds to .png),
selftest.


LICENCE
-------
YFT Livery Maker is free, and its own code is open source under the MIT
licence (LICENSE.txt). It includes CodeWalker.Core by dexyfex and other
third-party code under their own terms; see THIRD-PARTY-NOTICES.txt.
Source and updates:
https://github.com/JustBlueDolphin/YftLiveryMaker

Only use it on vehicles you have the rights to modify. It can't open files
locked by FiveM Asset Escrow. Not affiliated with Rockstar Games, Take-Two
Interactive or Cfx.re (FiveM).
