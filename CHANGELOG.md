# Wildlands Toolkit 0.2.0

The largest Wildlands Toolkit update so far, focused on mesh editing, mod distribution and safer
archive modification.

- Addon archives carry complete `.data` containers, because the game replaces a container as a
  whole. Each addon builds its containers on top of what the game currently loads, earlier addons
  included, so independent edits to the same weapon container coexist. When two addons change the
  same resource, the newer one wins and the install says so.
- Resource deletions work in addon mode as well.
- New addon archives use a patch number above every existing addon in their archive family,
  including when an earlier addon was removed and left a gap in the numbering.
- Addon installation combines independent resource edits queued against different copies of the
  same container within an archive family. Conflicting edits to the same resource are rejected.
- Texture imports offer an optional **Replace matching copies** checkbox to include identical
  textures and streamed mips across containers and archives. Matching uses current contents,
  including pending edits, so independent replacements are preserved. Targets are listed before import.
- Skeleton resources can be exported as Blender-readable GLB armatures and imported again after
  editing their rest-pose positions and rotations.
- Skeleton resources now open in a dedicated 3D viewer with the same top-toolbar workflow as the
  mesh viewer, including live GLB import, export, framing and local bone-axis display.
- The mesh viewer now has a scene hierarchy for draw ranges and the armature, per-item visibility,
  selection details and a viewport outline for the selected hierarchy item.
- Added a strict parser coverage audit that separates byte-exact roundtrips from formats that can be
  created without copying unknown source bytes.
- Added `parseraudit`, which checks each supported reader and writer over every installed archive and
  writes a machine-readable report. The current complete game corpus finishes without parser errors.
- The mesh viewer paints clothing like the game: camo patterns, solid colours and dirt follow the
  disassembled `CHR_cloth_Detail` shader, and a Style list shows every customization style of the
  item. The old name-based camo guesser and the `camo` command are gone.
- The mesh viewer renders with Direct3D 11: physically based lighting with soft shadows,
  full-resolution textures with normal and gloss maps, a LOD list, and a Pending changes switch for
  before and after comparisons without writing to the game.

## Added

- Full glTF mesh export and import workflow
- Preservation of UV maps, vertex colours, normals, tangents, materials, draw ranges and skin weights
- Support for multi-object meshes, multiple materials and nested transforms
- Automatic reconstruction of missing or incompatible skin weights
- FBX export with skeletons and skin weights
- OBJ export and import for static and simpler meshes
- Cross-archive skeleton index for rigged mesh exports
- Persistent mod projects with author, version and operation history
- Portable `.wlmod` packages with pre-installation review and conflict checking
- Support for updating an already installed version of the same `.wlmod` package
- Support for adding and removing individual resources
- Support for adding and removing complete `.data` containers
- **Find copies in game** for locating duplicate resources across installed archives
- Dedicated Changes window with grouped operations
- **EDITED** and **REMOVED** markers in the archive browser
- Automatic update notifications
- Recoverable crash reports

## Improved

- Archive discovery now includes every DLC directory
- Archive loading and navigation are now asynchronous
- Redesigned mesh and texture viewer toolbars
- Added a dedicated mesh export dialog that shows the selected format and exported contents, keeps
  skeletons and skin weights enabled by default, and explains that OBJ is geometry-only
- Improved mesh material previews and viewer information
- Improved validation of imported meshes before changes are queued
- Improved skeleton matching and fallback weight generation
- Improved texture decoding, encoding, DDS handling and streamed-mip validation
- Improved raw resource validation for IDs, resource types and file structure
- Improved `.wlmod` validation, archive conflict detection and installation safety
- Improved archive rebuilding for resource and container additions or removals
- Improved Apply confirmation, disk-space checks and `.original` backup protection
- Improved separation between Free Mode changes and mod-project changes
- Improved error messages, tooltips, status messages and keyboard shortcuts
- Improved format validation and round-trip coverage for archives, meshes, skeletons, textures and
  BuildTables
- Refactored large parts of the codebase to reduce duplication and separate unrelated systems

## Fixed

- Addons written as partial containers (only the changed resources) left the game stuck on the
  splash screen. Addons installed with such a build have to be deleted and installed again
- Importing a mesh no longer destroys the hidden vertex colour of skinned clothing. It drives the
  tint zones and camo of vests, shirts and pants; without it every strap got the wrong solid colour
  and camo in game. OBJ files and glTF files without colours now take it from the matching triangle
  of the original mesh
- Static props, furniture and vehicle parts no longer request an unnecessary skeleton during FBX export
- Mesh export failures are now reported instead of appearing to do nothing
- Fixed several glTF import issues involving transforms, material ranges, skin ownership and tangent
  handedness
- glTF export and import no longer flip the V texture coordinate. glTF measures V from the top of
  the image, which is where the game measures it too, so the flip mirrored every imported UV map
  vertically. OBJ and FBX measure V from the bottom and keep their flip
- Fixed several clustered and plain mesh buffer and index-range issues
- Fixed several texture conversion and mip-chain edge cases
- Fixed archive metadata and prefetch handling when containers are added or removed
- Removed obsolete diagnostics and unnecessary status-bar output

## Disabled for this release

- The experimental BuildTable editor is temporarily disabled because its generic character-item path
  is not ready for public use. BuildTable resources can still be inspected, extracted and replaced as
  raw resources.
