using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Wildlands.Formats.Models;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;
using Wildlands.Formats.Weather;
using Wildlands.Toolkit;
using static Commands;

if (args.Length < 2 && (args.Length == 0 || args[0] != "memtraceprobe"))
{
    Console.WriteLine("usage: wlcli <command> <args>");
    Console.WriteLine("  blobs <file.data>              list the compressed blobs of a data file");
    Console.WriteLine("  dump  <file.data> <outdir>     write each decompressed blob to disk");
    Console.WriteLine("  list  <file.forge> [count]     list forge entries");
    Console.WriteLine("  entry <file.forge> <index> <out>  write one forge entry to disk");
    Console.WriteLine("  res   <file.data> [count]      list the resources inside a data file");
    Console.WriteLine("  tex   <file.data>              read every texture in a data file");
    Console.WriteLine("  mips  <file.forge> [name] [n]  show where each mip level of a texture comes from");
    Console.WriteLine("  get   <file.data> <name> <out>    write one resource to disk");
    Console.WriteLine("  mesh  <file.data> <name>       what a mesh holds");
    Console.WriteLine("  sets  <file.forge> [n]         which textures the texture sets point at");
    Console.WriteLine("  mats  <file.forge> [n]         whether every draw range finds its material");
    Console.WriteLine("  matinfo <file.forge> <container> <material>  show texture-selector and TextureSet links");
    Console.WriteLine("  params <file.forge> [n] [out.txt]  walk every material parameter list and count what is in it");
    Console.WriteLine("  camo  <file.forge> [file2.forge...]  match every camo option to the texture it wears");
    Console.WriteLine("  ranges <file.data> <name>      how its index buffer splits over the draw ranges");
    Console.WriteLine("  meshes <file.forge> [n]        count the geometry containers and vertex formats");
    Console.WriteLine("  geometry <file.forge> [n]      decode every mesh and check it against its own header");
    Console.WriteLine("  fbx   <file.data> <name> <out> [cache.bin]  write one mesh as a binary FBX file");
    Console.WriteLine("  objout <file.data> <name> <out.obj>  write one mesh as a Wavefront OBJ file");
    Console.WriteLine("  objin <file.data> <name> <in.obj> <out.data>  replace one mesh's geometry from an OBJ file");
    Console.WriteLine("  objcycle <folder|file.data> [n]  export every mesh to OBJ, read it back and compare the geometry");
    Console.WriteLine("  gltfout <file.data> <name> <out.glb> [cache.bin]  write one mesh as binary glTF, with skin and every uv set");
    Console.WriteLine("  gltfin <file.data> <name> <in.glb> <out.data>  replace one mesh's geometry from a glTF file");
    Console.WriteLine("  gltfcycle <folder|file.data> [n]  export every mesh to glTF, read it back and compare everything");
    Console.WriteLine("  hash  <name> [name...]        the class hash of a type name");
    Console.WriteLine("  hashscan <binary> <hash> [hash...]  resolve CRC32 hashes from strings retained in a binary");
    Console.WriteLine("  audit <file.forge> [n]         count the textures that cannot be shown, and why");
    Console.WriteLine("  recode <file.forge> [n]        decode every texture, encode it again and measure what was lost");
    Console.WriteLine("  texcycle <file.forge> [n]      write every texture out as dds, read it back and rebuild the resource");
    Console.WriteLine("  texout <file.forge> <filter> <outdir>  write every matching texture out as png, top level only");
    Console.WriteLine("  texin  <file.forge> <indir>    read a folder of png back in, subfolders included, and write the archive");
    Console.WriteLine("  guess <list.txt> <file.forge> [file2.forge...]  check candidate names against real hashes");
    Console.WriteLine("  skeletons <file.forge> [n] [cache.bin]  for every skinned mesh, check the matching Skeleton's hierarchy");
    Console.WriteLine("  skelcycle <folder|file.data>   read and rewrite every Skeleton byte for byte");
    Console.WriteLine("  skelcheck <folder|file.data>   check Skeletons against the published Anvil documentation");
    Console.WriteLine("  buildcycle <folder|file.data>  parse and rewrite every BuildTable byte for byte");
    Console.WriteLine("  buildaudit <file.forge> [container filter]  roundtrip and duplicate every safe BuildTable row in an archive");
    Console.WriteLine("  meshcycle <folder|file.data> [n]  read and rewrite every Mesh byte for byte");
    Console.WriteLine("  skelindex <folder-with-forges> <cache.bin>  index every Skeleton resource across a folder of archives");
    Console.WriteLine("  pack  <file.data> <out.data>   read a data file and write it back, then compare both");
    Console.WriteLine("  setres <file.data> <name> <in.bin> <out.data>  replace one resource and write the data file");
    Console.WriteLine("  putentry <file.forge> <index> <file> [out.forge]  put a file into an archive entry, in place or into a rebuilt copy");
    Console.WriteLine("  rebuild <file.forge> <out.forge>  write every entry again in order, then compare both");
    Console.WriteLine("  timecycle <file.data> <name> [out.txt]  write one time cycle out as editable text");
    Console.WriteLine("  settimecycle <file.data> <name> <in.txt> <out.data>  read that text back and rebuild the data file");
    Console.WriteLine("  weather <file.forge> [n]       read and rewrite every time cycle in an archive, byte for byte");
    Console.WriteLine("  weatherprops <file.forge> [n]  census PropertyPath hashes in time-of-day and weather controllers");
    Console.WriteLine("  profileprops <file.forge> <hash> [hash...]  show the actual curves for selected property hashes");
    Console.WriteLine("  crackprops <file.forge> <source> [source...]  match unknown property hashes only against literal strings or extracted file names");
    Console.WriteLine("  graphicsaudit <file.forge> <out.json>  write a structured inventory of every graphics controller and curve");
    Console.WriteLine("  guessprops <audit.json> [words.txt] [parts]  combine graphics terms and match unknown leaf hashes");
    Console.WriteLine("  graphicsprofile <file.data> <resource> <profile.json> [out.data]  preview or apply a graphics profile");
    Console.WriteLine("  refs  <file.data> <name|0xid>  which resources hold this resource's id");
    Console.WriteLine("  refsforge <archive.forge> <container filter> <id> [id...]  find exact ids inside one Forge container");
    Console.WriteLine("  refs32forge <archive.forge> <container filter> <value> [value...]  find exact 32-bit values inside one Forge container");
    Console.WriteLine("  namesforge <archive.forge> <container filter> <resource filter>  list matching resources in one Forge container");
    Console.WriteLine("  hexdb <archive.forge> <container filter> <id> [bytes] [offset]  print database resource bytes");
    Console.WriteLine("  tagmap <archive.forge> <container filter> <tag> [tag...]  inspect BuildTag-to-column-mask entries");
    Console.WriteLine("  comparedb <archive.forge> <container filter> <baseline id> <candidate id> [...]  compare database resource bytes");
    Console.WriteLine("  locfind <game folder> <string id>  find one localized string in every installed package");
    Console.WriteLine("  locstats <game folder>  measure localized string id ranges in installed packages");
    Console.WriteLine("  xrefall <game folder> <id> [id...]  find exact 64-bit references in every installed data container");
    Console.WriteLine("  find64 <folder|file.data> <id> [id...]  find exact little-endian 64-bit values in resources");
    Console.WriteLine("  copies <archive.forge> <id>  list every installed copy of one exact resource id");
    Console.WriteLine("  ids <archive.forge> <id> [id...]  resolve exact resource ids from the archive index");
    Console.WriteLine("  prefetchrefs <archive.forge> <entry id> [id...]  inspect exact 64-bit references in one entry's prefetch block");
    Console.WriteLine("  where <game folder> <name> [--all]  which archives hold a resource, and which one the game loads last");
    Console.WriteLine("  handles <game folder> <archive.forge> [name prefix]  check that every BuildTable model handle reaches a Forge entry");
    Console.WriteLine("  lodsizes <game folder> <archive.forge> [name prefix]  check that every LODSelector names the real size of the LOD it streams");
    Console.WriteLine("  agree <game folder> <archive.forge> [name prefix]  check that every installed copy of a container offers the same options");
    Console.WriteLine("  buildinfo <archive.forge> <container filter> [table filter]  show BuildTable row tags and selectors");
    Console.WriteLine("  objects <file.data> [resource filter]  list the embedded objects of each resource by class");
    Console.WriteLine("  objectcheck <folder with .data>  hold the object scanner against the BuildTable parser");
    Console.WriteLine("  animinfo <file.data> [filter]  what an animation holds, track by track");
    Console.WriteLine("  animcycle <folder|file.data>  read and rewrite every Animation byte for byte");
    Console.WriteLine("  animvalues <folder with .data>  check decoded rotations, key times and smoothness");
    Console.WriteLine("  skycycle <folder with .data>  read and rewrite every LayeredSky byte for byte");
    Console.WriteLine("  skyout <file.data> <outdir>  write every sky layer as a dds file");
    Console.WriteLine("  classdump <folder with .data> <class> <outdir> [count]  write resources of one class to disk");
    Console.WriteLine("  classcensus <folder with .data>  count every resource class and how many bytes it holds");
    Console.WriteLine("  prefetchblock <archive.forge> <entry id>...  print one prefetch block as hex");
    Console.WriteLine("  prefetchcycle <game folder>  read and rewrite every prefetch block byte for byte");
    Console.WriteLine("  metacycle <game folder>  read and rewrite every GlobalMetaFile byte for byte");
    Console.WriteLine("  installaddon <game folder> <package.wlmod>  install a mod package as its own addon archive");
    Console.WriteLine("  makepatch <source.forge> <out.forge> <entry name|prefix*>...  write a new patch archive holding those entries");
    Console.WriteLine("  dblists <archive.forge> <container filter> <resource id> [id...]  find counted id lists in one database resource");
    Console.WriteLine("  handlelists <archive.forge> <container filter> [table filter] [id...]  show the BuildTable handle lists that own sub-tables");
    Console.WriteLine("  armorymeta <game folder> <build tag> [build tag...]  resolve gameplay records for exact BuildTags");
    Console.WriteLine("  armorylists <game folder> <record id>  group binary list candidates containing an exact record ID");
    Console.WriteLine("  charactersmithcheck <game folder> <record id>  verify an in-memory CharacterSmith row insertion");
    Console.WriteLine("  vestregistrycheck <game folder> <record id>  verify all currently proven vest registry insertions in memory");
    Console.WriteLine("  vestaddcheck <game folder> <character archive> <model> <diffuse> <normal> <mask1> [row]  build a complete vest plan in memory");
    Console.WriteLine("  vestpack <game folder> <character archive> <model> <diffuse> <normal> <mask1> <project folder> <package> [row] [internal name] [display name]  build and validate an installable vest package");
    Console.WriteLine("  tagmapcheck <game folder> <template tag> <new tag>  validate effective BuildTag column-map insertions in memory");
    Console.WriteLine("  storeregistrycheck <game folder> <StoreObjectInfo id>  verify an in-memory StoreObjectInfo registry insertion");
    Console.WriteLine("  storeobjectcheck <game folder> <StoreObjectInfo id> <record id>  verify an in-memory StoreObjectInfo clone");
    Console.WriteLine("  targetresolvecheck <game folder> <asset id> [id...]  time targeted BuildTable reference resolution");
    Console.WriteLine("  duprowcheck <archive.forge> <container> <table> <row>  verify an in-memory row duplication");
    Console.WriteLine("  taglistcheck <archive.forge> <container> <table> <row>  verify an in-memory BuildTags insertion");
    Console.WriteLine("  memwatch <process|pid> <u32:0xvalue|u64:0xvalue> [...]  compare exact values in two live process states");
    Console.WriteLine("  memfind <process|pid> <u32:0xvalue|u64:0xvalue> [...]  find exact values in a live process");
    Console.WriteLine("  memcontexts <process|pid> <locked value> <free value> [...]  compare nearby runtime records");
    Console.WriteLine("  memsave <process|pid> <out.json> <u32:0xvalue|u64:0xvalue> [...]  save a live value snapshot");
    Console.WriteLine("  memdiff <before.json> <after.json>  compare two saved live value snapshots");
    Console.WriteLine("  memread <process|pid> <address> <length>  read a live process memory range");
    Console.WriteLine("  memregion <process|pid> <address> <out.bin>  save the readable region containing an address");
    Console.WriteLine("  memtrace <process|pid> <address> [address...]  trace one to four aligned 4-byte addresses");
    Console.WriteLine("  memtraceprobe                       isolated hardware-watchpoint test target");
    return 1;
}

try
{
    switch (args[0])
    {
        case "blobs":
            return ReadBlobs(args[1], null);
        case "dump" when args.Length >= 3:
            return ReadBlobs(args[1], args[2]);
        case "list":
            return ListForge(args[1], args.Length >= 3 ? int.Parse(args[2]) : 20);
        case "entry" when args.Length >= 4:
            return WriteEntry(args[1], int.Parse(args[2]), args[3]);
        case "res":
            return ListResources(args[1], args.Length >= 3 ? int.Parse(args[2]) : 20);
        case "tex":
            return ReadTextures(args[1]);
        case "mips":
            return ListMips(args[1], args.Length >= 3 ? args[2] : "", args.Length >= 4 ? int.Parse(args[3]) : 10);
        case "get" when args.Length >= 4:
            return GetResource(args[1], args[2], args[3]);
        case "geometry":
            return CheckGeometry(args[1], args.Length >= 3 ? int.Parse(args[2]) : 2000);
        case "fbx" when args.Length >= 4:
            return ExportFbx(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
        case "ranges" when args.Length >= 3:
            return ShowRanges(args[1], args[2]);
        case "meshes":
            return CensusMeshes(args[1], args.Length >= 3 ? int.Parse(args[2]) : 2000);
        case "mesh" when args.Length >= 3:
            return ShowMesh(args[1], args[2]);
        case "sets":
            return CheckTextureSets(args[1], args.Length >= 3 ? int.Parse(args[2]) : 2000);
        case "mats":
            return CheckMaterials(args[1], args.Length >= 3 ? int.Parse(args[2]) : 2000);
        case "matinfo" when args.Length >= 4:
            return ShowMaterialLinks(args[1], args[2], args[3]);
        case "camo":
            return MatchCamo(args[1..]);
        case "params":
            return CheckParameters(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue,
                args.Length >= 4 ? args[3] : null);
        case "objout" when args.Length >= 4:
            return ExportObj(args[1], args[2], args[3]);
        case "objin" when args.Length >= 5:
            return ImportObj(args[1], args[2], args[3], args[4]);
        case "objcycle":
            return CheckObjRoundTrips(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
        case "gltfout" when args.Length >= 4:
            return ExportGltf(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
        case "gltfin" when args.Length >= 5:
            return ImportGltf(args[1], args[2], args[3], args[4]);
        case "gltfcycle":
            return CheckGltfRoundTrips(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
        case "hash":
            return HashNames(args[1..]);
        case "hashscan" when args.Length >= 3:
            return ScanHashes(args[1], args[2..]);
        case "audit":
            return Audit(args[1], args.Length >= 3 ? int.Parse(args[2]) : 500);
        case "recode":
            return Recode(args[1], args.Length >= 3 ? int.Parse(args[2]) : 200);
        case "texcycle":
            return TexCycle(args[1], args.Length >= 3 ? int.Parse(args[2]) : 200);
        case "texhdr" when args.Length >= 3:
            return ShowTextureHeaders(args[1], args[2]);
        case "texout" when args.Length >= 4:
            return ExportTextures(args[1], args[2], args[3]);
        case "texin" when args.Length >= 3:
            return ImportTextures(args[1], args[2]);
        case "guess" when args.Length >= 2:
            return GuessTypes(args[1], args[2..]);
        case "skeletons":
            return CheckSkeletons(args[1], args.Length >= 3 ? int.Parse(args[2]) : 20000,
                args.Length >= 4 ? args[3] : null);
        case "skelcycle":
            return CheckSkeletonRoundTrips(args[1]);
        case "skelcheck":
            return CheckSkeletonsAgainstDocs(args[1]);
        case "buildcycle":
            return CheckBuildTableRoundTrips(args[1]);
        case "buildaudit":
            return AuditForgeBuildTables(args[1], args.Length >= 3 ? args[2] : "");
        case "meshcycle":
            return CheckMeshRoundTrips(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
        case "skelindex" when args.Length >= 3:
            return BuildSkeletonIndex(args[1], args[2]);
        case "pack" when args.Length >= 3:
            return PackData(args[1], args[2]);
        case "setres" when args.Length >= 5:
            return SetResource(args[1], args[2], args[3], args[4]);
        case "rebuild" when args.Length >= 3:
            return RebuildArchive(args[1], args[2]);
        case "putentry" when args.Length >= 4:
            return PutEntry(args[1], int.Parse(args[2]), args[3], args.Length >= 5 ? args[4] : null);
        case "timecycle" when args.Length >= 3:
            return ShowTimeCycle(args[1], args[2], args.Length >= 4 ? args[3] : null);
        case "settimecycle" when args.Length >= 5:
            return ApplyTimeCycle(args[1], args[2], args[3], args[4]);
        case "weather":
            return CheckTimeCycles(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
        case "weatherprops":
            return CensusWeatherProperties(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
        case "profileprops" when args.Length >= 3:
            return ProfileWeatherProperties(args[1], args[2..]);
        case "crackprops" when args.Length >= 3:
            return CrackPropertyNames(args[1], args[2..]);
        case "graphicsaudit" when args.Length >= 3:
            return GraphicsAudit.Write(args[1], args[2]);
        case "guessprops" when args.Length >= 2:
            return PropertyGuesser.Run(args[1], args.Length >= 3 ? args[2] : null,
                args.Length >= 4 ? int.Parse(args[3]) : 3);
        case "graphicsprofile" when args.Length >= 4:
            return ApplyGraphicsProfile(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
        case "refs" when args.Length >= 3:
            return FindReferences(args[1], args[2]);
        case "refsforge" when args.Length >= 4:
            return FindForgeReferences(args[1], args[2], args[3..]);
        case "refs32forge" when args.Length >= 4:
            return FindForge32BitValues(args[1], args[2], args[3..]);
        case "namesforge" when args.Length >= 4:
            return FindForgeResourceNames(args[1], args[2], args[3]);
        case "hexdb" when args.Length >= 4:
            return PrintDatabaseResource(args[1], args[2], args[3],
                args.Length >= 5 ? ParseFlexibleInt32(args[4]) : int.MaxValue,
                args.Length >= 6 ? ParseFlexibleInt32(args[5]) : 0);
        case "tagmap" when args.Length >= 4:
            return InspectBuildTagColumnMaps(args[1], args[2], args[3..]);
        case "comparedb" when args.Length >= 5:
            return CompareDatabaseResources(args[1], args[2], args[3], args[4..]);
        case "locfind" when args.Length >= 3:
            return FindLocalizedString(args[1], args[2]);
        case "locstats" when args.Length >= 2:
            return MeasureLocalizedStringIds(args[1]);
        case "xrefall" when args.Length >= 3:
            return ArchiveReferenceCensus.Run(args[1], args[2..]);
        case "find64" when args.Length >= 3:
            return Find64BitValues(args[1], args.Skip(2).ToArray());
        case "copies" when args.Length >= 3:
            return FindAssetCopies(args[1], args[2]);
        case "ids" when args.Length >= 3:
            return FindResourcesById(args[1], args[2..]);
        case "prefetchrefs" when args.Length >= 3:
            return InspectPrefetchReferences(args[1], args[2], args[3..]);
        case "where" when args.Length >= 3:
            return WhereIsResource(args[1], args[2], args.Contains("--all"));
        case "handles" when args.Length >= 3:
            return CheckModelHandles(args[1], args[2], args.Length >= 4 ? args[3] : "");
        case "lodsizes" when args.Length >= 3:
            return CheckStreamedLodSizes(args[1], args[2], args.Length >= 4 ? args[3] : "");
        case "agree" when args.Length >= 3:
            return CheckCopiesAgree(args[1], args[2], args.Length >= 4 ? args[3] : "");
        case "buildinfo" when args.Length >= 3:
            return InspectBuildTables(args[1], args[2], args.Length >= 4 ? args[3] : "");
        case "objects" when args.Length >= 2:
            return ListAnvilObjects(args[1], args.Length >= 3 ? args[2] : "");
        case "objectcheck" when args.Length >= 2:
            return CheckAnvilObjects(args[1]);
        case "animinfo" when args.Length >= 2:
            return ShowAnimation(args[1], args.Length >= 3 ? args[2] : "");
        case "animcycle" when args.Length >= 2:
            return CycleAnimations(args[1]);
        case "skycycle" when args.Length >= 2:
            return CycleLayeredSkies(args[1]);
        case "skyout" when args.Length >= 3:
            return ExportLayeredSky(args[1], args[2]);
        case "animpose" when args.Length >= 2:
            return PoseAnimation(args[1], args.Length >= 3 ? args[2] : "");
        case "animvalues" when args.Length >= 2:
            return CheckAnimationValues(args[1]);
        case "animformats" when args.Length >= 2:
            return DeriveAnimationFormats(args[1]);
        case "skelbones" when args.Length >= 4:
            return DumpSkeletonBones(args[1], args[2], args[3]);
        case "animtry" when args.Length >= 3:
            return TryAnimationStrides(args[1], args[2..]);
        case "classdump" when args.Length >= 4:
            return DumpResourceClass(args[1], (uint)ParseResourceId(args[2]), args[3],
                args.Length >= 5 ? int.Parse(args[4]) : 20);
        case "classcensus" when args.Length >= 2:
            return CensusResourceClasses(args[1]);
        case "prefetchblock" when args.Length >= 3:
            return DumpPrefetchBlock(args[1], args[2..]);
        case "prefetchcycle" when args.Length >= 2:
            return CyclePrefetchBlocks(args[1], args.Length >= 3 ? args[2] : "",
                args.Length >= 4 && args[3] == "dump");
        case "metacycle" when args.Length >= 2:
            return CycleGlobalMetaFiles(args[1]);
        case "installaddon" when args.Length >= 3:
            return InstallPackageAsAddon(args[1], args[2]);
        case "makepatch" when args.Length >= 4:
            return CreatePatchArchive(args[1], args[2], args[3..]);
        case "dblists" when args.Length >= 4:
            return InspectDatabaseLists(args[1], args[2], ParseResourceId(args[3]),
                args.Length >= 5 ? args[4..].Select(ParseResourceId).ToList() : []);
        case "handlelists" when args.Length >= 3:
            return InspectHandleLists(args[1], args[2], args.Length >= 4 ? args[3] : "",
                args.Length >= 5 ? args[4..].Select(ParseResourceId).ToList() : []);
        case "armorymeta" when args.Length >= 3:
            return InspectArmoryMetadata(args[1], args[2..]);
        case "armorylists" when args.Length >= 3:
            return InspectArmoryRegistries(args[1], args[2]);
        case "charactersmithcheck" when args.Length >= 3:
            return CheckCharacterSmithInsertion(args[1], args[2]);
        case "vestregistrycheck" when args.Length >= 3:
            return CheckVestRegistryInsertions(args[1], args[2]);
        case "vestaddcheck" when args.Length >= 7:
            return CheckVestAddPlan(args[1], args[2], args[3], args[4], args[5], args[6],
                args.Length >= 8 ? int.Parse(args[7]) : 1);
        case "vestpack" when args.Length >= 9:
            return CreateVestPackage(args[1], args[2], args[3], args[4], args[5], args[6],
                args[7], args[8], args.Length >= 10 ? int.Parse(args[9]) : 1,
                args.Length >= 11 ? args[10] : "VirtusVest",
                args.Length >= 12 ? args[11] : "Virtus Vest");
        case "tagmapcheck" when args.Length >= 4:
            return CheckBuildTagColumnMaps(args[1], args[2], args[3]);
        case "storeregistrycheck" when args.Length >= 3:
            return CheckStoreRegistryInsertion(args[1], args[2]);
        case "storeobjectcheck" when args.Length >= 4:
            return CheckStoreObjectInfoClone(args[1], args[2], args[3]);
        case "targetresolvecheck" when args.Length >= 3:
            return CheckTargetResolution(args[1], args[2..]);
        case "duprowcheck" when args.Length >= 5:
            return CheckDuplicatedBuildTable(args[1], args[2], args[3], int.Parse(args[4]));
        case "taglistcheck" when args.Length >= 5:
            return CheckBuildTagInsertion(args[1], args[2], args[3], int.Parse(args[4]));
        case "memwatch" when args.Length >= 3:
            return ProcessMemoryWatch.Run(args[1], args[2..]);
        case "memfind" when args.Length >= 3:
            return ProcessMemoryWatch.Find(args[1], args[2..]);
        case "memcontexts" when args.Length >= 4:
            return ProcessMemoryWatch.CompareContexts(args[1], args[2..]);
        case "memsave" when args.Length >= 4:
            return ProcessMemoryWatch.Save(args[1], args[2], args[3..]);
        case "memdiff" when args.Length >= 3:
            return ProcessMemoryWatch.Compare(args[1], args[2]);
        case "memread" when args.Length >= 4:
            return ProcessMemoryWatch.Read(args[1], args[2], args[3]);
        case "memregion" when args.Length >= 4:
            return ProcessMemoryWatch.SaveRegion(args[1], args[2], args[3]);
        case "memtrace" when args.Length >= 3:
            return ProcessMemoryTrace.Run(args[1], args[2..]);
        case "memtraceprobe":
            return ProcessMemoryTrace.Probe();
        default:
            Console.WriteLine($"unknown command: {args[0]}");
            return 1;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 1;
}

// A model a BuildTable row names is always the first resource of a Forge entry that carries
// its id. Measured over the shipped game: 29,637 of 29,637 containers in DataPC.forge start
// with the resource their entry is named after, and 2,487 of 2,487 model handles in the 843
// weapon BuildTables of DataPC_patch_01.forge name such an entry. A handle that names a
// resource buried inside somebody else's container is never resolved by the game, so this
// command is the check to run over a modified archive before starting Wildlands.
