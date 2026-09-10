# Wildlands Toolkit 0.2.0

The largest Wildlands Toolkit update so far, focused on mesh editing, mod distribution and safer
archive modification.

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

- Static props, furniture and vehicle parts no longer request an unnecessary skeleton during FBX export
- Mesh export failures are now reported instead of appearing to do nothing
- Fixed several glTF import issues involving transforms, material ranges, skin ownership and tangent
  handedness
- Fixed several clustered and plain mesh buffer and index-range issues
- Fixed several texture conversion and mip-chain edge cases
- Fixed archive metadata and prefetch handling when containers are added or removed
- Removed obsolete diagnostics and unnecessary status-bar output

## Disabled for this release

- The experimental BuildTable editor is temporarily disabled because its generic character-item path
  is not ready for public use. BuildTable resources can still be inspected, extracted and replaced as
  raw resources.
