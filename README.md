# (Probably) the first Ghost Recon Wildlands Toolkit

An open modding tool for **Tom Clancy's Ghost Recon Wildlands**. It opens the game's
`.forge` archives, shows what is inside them, and writes changes back so that you can
modify your game.

> **Downloads are on the [Releases](../../releases) page.**
> Building from source is only needed if you want to change something on your own.

---

## Features

### Archives

- Opens every `.forge` of the game and lists its entries
- Searches neighbouring archives in the background, so a resource is found even when it lives in a different archive or file
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
- Exports **binary FBX** with skeleton and skin weights, without the Autodesk SDK
- Finds the matching skeleton across all archives through a bone name index

### Materials and appearance

- Reads the parameter list of a material with resolved names and the textures it names
- Resolves texture sets to their textures

### Weather and lighting

- Editor for `TimeOfDayPropertyControllerData` and `WeatherPropertyControllerData`
- Day curves with their control points: editable, filterable, resettable
- The same curves as editable text, out and back in without losing a byte

### Command line

The Wildlands CLI is part of the project which is also usable for batch texture work but it is recommended to use the normal GUI instead.
A documentation for it will probably be available later.

---

## Getting started

1. Download the release and start `Wildlands.Toolkit.exe`
2. On first start it asks for the Wildlands folder. It also offers to index every
   skeleton in the game, which takes a few minutes and is only needed to export
   skinned meshes with their rig. You can decline and do it later
3. Pick an archive on the left, step into an entry and select the resource you want to modify

### Example: Replacing a texture

1. Select the texture, open the texture viewer, press **Replace...**
2. Pick an image. The dialog shows the target format, the mip levels and **which resources will be written**, before you agree to anything
3. The change lands on a pile and is shown right away. Nothing is written yet
4. **Apply changes** writes every change in one pass, **Discard** throws the pile away

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
Please report any issue you encounter on this Discord server in general chat or send me a DM.
> **[Discord Server](https://discord.gg/gVGSFAcYQB)**

Visit my website [here](https://alphaglyph.dev) to learn more about me and my projects or to get my Discord username.

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
hash      <name> [name...]                  the class hash of a type name

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
skeletons <file.forge> [n] [cache.bin]      check every skinned mesh against its skeleton
skelindex <folder> <cache.bin>              index every skeleton across a folder of archives

sets      <file.forge> [n]                  which textures the texture sets point at
mats      <file.forge> [n]                  whether every draw range finds its material
params    <file.forge> [n] [out.txt]        walk every material parameter list
camo      <file.forge> [more.forge...]      match every camo option to its texture
guess     <list.txt> <file.forge> [...]     check candidate names against real hashes

timecycle    <file.data> <name> [out.txt]   write one time cycle out as editable text
settimecycle <file.data> <name> <in.txt> <out.data>   read that text back in
weather      <file.forge> [n]               rewrite every time cycle, byte for byte

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
