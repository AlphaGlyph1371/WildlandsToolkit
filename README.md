<p align="center">
  <img src="assets/logo.png" alt="Wildlands Toolkit" width="380">
</p>

# (Probably) the first Ghost Recon Wildlands Toolkit

An open modding tool for **Tom Clancy's Ghost Recon Wildlands**. It opens the game's
`.forge` archives, shows what is inside them, and writes changes back so that you can
modify your game.

> **Downloads are on the [Releases](../../releases) page.**
> Building from source is only needed if you want to change something on your own.

---

## Features

Note: because most of you probably will not read all of that, here is the short version on meshes.
You **can** now replace the geometry of a mesh that already exists: export it as glTF, edit it in
Blender, import it back. Skin weights, every uv set and vertex colours survive the trip, and the
number of draw ranges is allowed to change.

**And it has been confirmed in the running game.** The body of an AK-12 was exported, scaled five
times over in Blender, imported back, and the game draws it: geometry intact down to every tooth of
the picatinny rail, textures sitting where they belong, lighting and shadows correct, the weapon
still held properly in the hands. That mesh is vertex format 2, the richest one in the game, with
three uv sets and vertex colours.

What you still can't do is add a brand new mesh that was not in the game before. And keep the
`.original` backups anyway.

### Archives

- Opens every `.forge` of the game and lists its entries, **including the ones in the `dlc_*` folders**
  (23 archives in a full install, not just the 10 in the main folder)
- Searches neighbouring archives in the background, so a resource is found even when it lives in a different archive or file
- **Replaces any resource with a raw file**, whatever its type: drop a `.Skeleton` or a `.BuildTable`
  straight in, from your own tools or from somewhere else. Bigger and smaller files are both fine
- Before it accepts a raw file it checks the type, the id and whether it still reads back, and says
  what is wrong instead of quietly breaking the archive
- Writes changes back into the game files

### Textures

- Views every texture the game ships
- Shows each mip level on its own, including the ones streamed from other archives or files
- Red, green, blue and alpha can be switched off separately in the viewer, as well as the background
- Exports as PNG, JPEG, BMP, TIFF or DDS
- **Replaces textures** and writes every affected resource, streamed mips included

Note: A DDS in the format and size of the target goes through untouched, everything else is encoded again

### Meshes

- 3D viewport with orbit, pan and zoom, built on WPF's own 3D
- Draws the real diffuse texture per draw range, back faces follow the original used material
- Exports and **re-imports binary glTF** (`.glb`): positions, normals, tangents, every UV set,
  vertex colours, skin weights and one material per draw range, all of it round-trips
- Exports **binary FBX** with skeleton and skin, without the Autodesk SDK
- Exports and re-imports Wavefront OBJ as well, for tools that want it
- Finds the matching skeleton across all archives through a bone name index, and uses it to give
  both FBX and glTF a real bone hierarchy instead of a flat list. **98.8% of skinned meshes do not
  ship their skeleton next to them**, so that index is what makes a usable rig possible at all
- Reads and writes `Mesh`, `Skeleton` and `BuildTable` **byte for byte**: 20450, 1662 and 3649
  resources of `DataPC.forge` come back out identical
- The skeleton reader is checked against the published AnvilNext documentation, hash for hash and
  invariant for invariant (`wlcli skelcheck`)

**Use glTF if you want the mesh to come back.** FBX export is still there and it carries the
skeleton and the skin, but glTF is the one that survives the round trip without losing anything.
OBJ works too and is the smallest common denominator, but it cannot carry skin weights, several
uv sets or vertex colours, so on a skinned mesh the toolkit has to guess the weights from the old
shape. It tells you when it does that.

### Materials and appearance

- Reads the parameter list of a material with resolved names and the textures it names
- Resolves texture sets to their textures
- Reads and rewrites `.BuildTable` resources byte for byte, all 3649 of them

**About BuildTables, because they had me confused for a while:** they are the game's variant
system, and they are where a lot of the "what does this thing look like" actually lives.

A column says *which property* gets set, a row is *one variant*, and a selector with tags and a
random seed picks the row. So what a shirt looks like is not decided in the mesh - it is decided
here. `TOPS_VAR_COLORALL` has nine rows, and each one points at another table:
`TOPS_solidColor_01-Black`, `_09-CoyoteBrown`, `_24-OliveDrab`, and so on.

They are nested nearly all the way down. Of the 87520 references I could resolve, **72325 point at
another BuildTable**. The rest land on texture specs, materials, shaders, LOD selectors, cloth and
skeletons. It goes well past clothing as well: inventory settings, vehicle lists, named characters
and even sound sets are wired up the same way.

Practically: if you want a piece of gear to look different, changing the mesh is often the heavy
way round, and bending one reference in the table chain is the light one. There is **no editor for
that yet** - for now BuildTables can only be swapped whole, through the raw replace path.

### Weather and lighting

- Editor for `TimeOfDayPropertyControllerData` and `WeatherPropertyControllerData`
- Day curves with their control points: editable, filterable, resettable
- The same curves as editable text, out and back in without losing a byte
- External JSON graphics profiles can be previewed on the exact open controller before they join the change list

Graphics profiles deliberately follow the raw browser: they only touch the controller that is
currently open, even when it came from an archive the game would not normally mount. The bundled
`GraphicsProfiles/Refined-Global-v0.2.json` and `Refined-Yungas-v0.2.json` are experimental,
controller-bound starting points rather than visually verified presets. The preview lists their exact ranges first; **Save** and then the main
window's **Apply changes** are still required before an archive is written.

Files beginning with `Calibration-` are deliberately obvious diagnostic switches, not visual
presets. Apply an `ON` profile only long enough to capture its comparison, then apply its matching
`RESTORE` profile to the same controller(s).

### Command line

The Wildlands CLI is part of the project which is also usable for batch texture work but it is recommended to use the normal GUI instead.
There is a full command reference at the bottom of this page.

The `...cycle` commands are the ones I actually trust the formats on. Point them at a folder of
extracted `.data` files and they check the whole lot; see the section above for the numbers.

---

## How much of this is actually tested

"It opens the file" and "it writes the file back correctly" are very different claims, so the CLI
has commands that read every resource of a kind, write it straight back, and compare byte for byte.
Over all of `DataPC.forge`:

| | rewritten | came out different |
|---|---|---|
| Meshes | 20450 | **0** |
| Skeletons | 1662 | **0** |
| BuildTables | 3649 | **0** |

For the import path there is `gltfcycle`, which exports every mesh, reads it back in and compares.
Over 2500 meshes: no failures, corners stay within half a percent of the mesh size, and every uv
set, all skin weights and all vertex colours come back unchanged.

Byte for byte is not the same as "the game accepts it", so that was tested separately: an AK-12
body, scaled 5x through the glTF round trip and written back into `DataPC.forge` and
`DataPC_patch_01.forge`, renders correctly in the running game. Geometry, normals, textures,
shadows and the skin all survive.

What is still untested is a heavily skinned character mesh - the AK-12 body has three bones, a
vest has eighty-five. The mechanism is the same, but nobody has looked at it in game yet.

---

## Getting started

1. Download the release and start `Wildlands.Toolkit.exe`
2. On first start it asks for the Wildlands folder. It also offers to index every
   skeleton in the game, which takes a few minutes. Say yes if you ever want to touch a
   character: **98.8% of skinned meshes do not ship their skeleton next to them**, and without
   that index they export as a pile of loose bones instead of a usable rig. Props and vehicle
   parts do not need it. You can decline and do it later
3. Pick an archive on the left, step into an entry and select the resource you want to modify

### Example: Replacing a texture

1. Select the texture, open the texture viewer, press **Replace** in the toolbar (or **Replace...** in the right-click menu)
2. Pick an image. The dialog shows the target format, the mip levels and **which resources will be written**, before you agree to anything
3. The change lands on a pile and is shown right away. Nothing is written yet
4. **Apply changes** writes every change in one pass, **Discard** throws the pile away

### Example: Replacing any other resource

1. Select it, right-click, **Replace with raw resource file...**
2. Pick your file. The toolkit checks that it is the same type, carries the same id and still reads
   back cleanly. If something is off it tells you what, and you can still go ahead
3. **Apply changes** writes it

This is the path for anything the toolkit has no editor for, and for files you built yourself.

### Example: Replacing a mesh

1. Select the mesh and press **Export** in the toolbar (or the button of the same name in the mesh
   viewer). Keep the default `.glb`
2. Edit it in Blender. Every material stays a separate material, and that is what becomes a draw
   range on the way back. You may add or remove one; the material list follows
3. Export it from Blender as glTF binary (`.glb`) again
4. Back in the browser, select the mesh and press **Replace** in the toolbar
5. Same as textures: the change lands on the pile, **Apply changes** writes it

The toolbar has **Export** and **Replace** side by side, so the way back in is as easy to find as
the way out. Everything still goes through the same pile of pending changes, and nothing touches
an archive until you press **Apply changes**.

One thing Blender cannot do for you: the bones. **The skeleton in your file is not imported.** The
mesh keeps the bone table it already has, and your joints are matched back onto it by name - so
leave the armature that came out of the export alone, and do not rename its bones. The toolkit
shows you how many matched before it writes anything, and if none of them do it stops instead of
guessing.

---

## One thing that will waste your evening

**The same resource often sits in several archives.** `W_ASR_AK47_body_LOD0` exists four times,
byte for byte identical, in `DataPC.forge`, `DataPC_patch_01.forge`, `DataPC_20_dlc.forge` and
`DataPC_29_dlc.forge`. Change one and the game may still load another, and it looks exactly like
your mod did nothing.

Before you change anything for real, ask:

```
wlcli where "G:\...\Wildlands" W_ASR_AK47_body_LOD0
```

It lists every archive that holds it, so you know how many you have to change. World map archives
are skipped by default because they are enormous; add `--all` when you need them too.

---

## Backups & file safety/file integrity

- Before the **first** write to an archive, a copy is made as `<archive>.original` and
  never touched again. That copy always holds the state before the first write, no matter how
  often you write afterwards
- **Close the game before writing.** A running game holds the archive open and prevents writing to it
- Nothing is written until you press **Apply changes** and wait until it shows "Applied in Xs" in the bottom left

---

## Requirements

- Recommended: Windows 10/11 (maybe older work too i haven't tested it)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Legit copy of Ghost Recon Wildlands

## Building from source

1. Download the source code
2. Extract the archive and open the `.slnx` file in [Visual Studio 2026](https://visualstudio.microsoft.com/de/downloads)

| Project | What it is |
|---|---|
| `Wildlands.Formats` | The format library. No UI dependency, usable on its own |
| `Wildlands.Toolkit` | The WPF application |
| `Wildlands.Cli` | the command line tool |

## Bug reports / Contact

This program is in early development which means there will probably be bugs or crashes.
Please report any issue you encounter on this Discord server in general chat or send me (Alpha) a DM.
> **[Discord Server](https://discord.gg/bUEkGCCX7t)**

Visit my website [here](https://alphaglyph.dev) to learn more about me and my projects or to get my Discord username.

## Credits

Some information is taken from this Anvil documentation: GhostReconWildlands-AnvilNext2.0-Documentation

---

## For experienced people: Command line reference

```
blobs     <file.data>                       list the compressed blobs of a data file
dump      <file.data> <outdir>              write each decompressed blob to disk
list      <file.forge> [count]              list forge entries
entry     <file.forge> <index> <out>        write one forge entry to disk
res       <file.data> [count]               list the resources inside a data file
get       <file.data> <name> <out>          write one resource to disk
refs      <file.data> <name|0xid>           which resources hold this resource's id
where     <game folder> <name> [--all]      which archives hold a resource (add --all for the world map)
hash      <name> [name...]                  the class hash of a type name
hashscan  <binary> <hash> [hash...]         resolve class hashes from strings kept in a binary

tex       <file.data>                       read every texture in a data file
mips      <file.forge> [name] [n]           show where each mip level comes from
audit     <file.forge> [n]                  count the textures that cannot be shown, and why
recode    <file.forge> [n]                  encode every texture again and measure what was lost
texcycle  <file.forge> [n]                  write every texture out as dds and rebuild it
texout    <file.forge> <filter> <outdir>    write matching textures out as png
texin     <file.forge> <indir>              read a folder of png back in and write the archive

mesh      <file.data> <name>                what a mesh holds
meshes    <file.forge> [n]                  count geometry containers and vertex formats
geometry  <file.forge> [n]                  decode every mesh and check it against its header
ranges    <file.data> <name>                how the index buffer splits over the draw ranges
fbx       <file.data> <name> <out> [cache]  write one mesh as a binary FBX file
gltfout   <file.data> <name> <out.glb>      write one mesh as binary glTF, with skin and uv sets
gltfin    <file.data> <name> <in.glb> <out.data>      replace one mesh's geometry from glTF
gltfcycle <folder|file.data> [n]            export every mesh to glTF, read it back and compare
objout    <file.data> <name> <out.obj>      write one mesh as a Wavefront OBJ file
objin     <file.data> <name> <in.obj> <out.data>      replace one mesh's geometry from an OBJ
objcycle  <folder|file.data> [n]            export every mesh to OBJ, read it back and compare
meshcycle <folder|file.data> [n]            read and rewrite every mesh byte for byte
skeletons <file.forge> [n] [cache.bin]      check every skinned mesh against its skeleton
skelcycle <folder|file.data>                read and rewrite every skeleton byte for byte
skelcheck <folder|file.data>                check skeletons against the published Anvil docs
buildcycle <folder|file.data>               parse and rewrite every build table byte for byte
skelindex <folder> <cache.bin>              index every skeleton across a folder of archives

sets      <file.forge> [n]                  which textures the texture sets point at
mats      <file.forge> [n]                  whether every draw range finds its material
params    <file.forge> [n] [out.txt]        walk every material parameter list
camo      <file.forge> [more.forge...]      match every camo option to its texture
guess     <list.txt> <file.forge> [...]     check candidate names against real hashes

timecycle    <file.data> <name> [out.txt]   write one time cycle out as editable text
settimecycle <file.data> <name> <in.txt> <out.data>   read that text back in
weather      <file.forge> [n]               rewrite every time cycle, byte for byte
weatherprops <file.forge> [n]               census property hashes in weather controllers
profileprops <file.forge> <hash> [...]      show curves for selected property hashes
crackprops   <file.forge> <source> [...]    match unknown hashes against names retained in files
graphicsaudit <file.forge> <out.json>       structured inventory of graphics controllers and curves
guessprops <audit.json> [words.txt] [parts] combine graphics terms and match unknown leaf hashes
graphicsprofile <file.data> <resource> <profile.json> [out.data] preview or apply a profile

pack      <file.data> <out.data>            read a data file, write it back, compare both
setres    <file.data> <name> <in.bin> <out.data>      replace one resource
putentry  <file.forge> <index> <file>       put a file into an archive entry, in place
rebuild   <file.forge> <out.forge>          write every entry again, then compare both
```

---

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE.txt) file for the full license text.

## Disclaimer

Not affiliated with or endorsed by Ubisoft. Ghost Recon and Wildlands are trademarks of
their respective owners. Use at your own risk. Modding game files can break your
installation, which is what the automatic backup is there for.
