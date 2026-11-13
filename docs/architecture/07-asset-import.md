# 07 - Asset import, removal, materials and prefab creation (recon for Phase 4, Part B)

Read-only recon of the decompiled `Assembly-CSharp` (game 1.14.1k). No game code is reproduced; only class and member names, signatures, enum values and one-line paraphrases. Evidence is `File.cs:line` in `decompiled/Assembly-CSharp/`. Tags: **[C]** confirmed from code, **[I]** inferred, **[U]** unverified (needs a run in the game).

Scope: what the editor does for "Import Asset", "Delete", "Reimport/Apply", "Create > Material", "Create > Prefab", and what a plugin may call. Nothing here was run.

## 0. Summary of what matters

- One method does the whole import: `PummelMod.AddAsset(string filepath, string folder, bool copy = true)` (`PummelMod.cs:898`). It is UI-coupled only at the end (an error popup) and **contains two ways to delete user files** (3.1, 3.2). A plugin should not call it with `copy: true`.
- The narrow, safe layer is `PummelMod.ImportFile(FileInfo file, bool forceNewGuid = false)` (`PummelMod.cs:1056`): it imports a file that is already inside the mod's `Assets` folder, registers it and writes the `.pmeta`. The plugin does the copy itself (free name, lowercase extension, only files it created are ever deleted on failure).
- Everything in the import path is synchronous on the main thread. Success is visible as soon as the call returns (asset registered and decoded); thumbnails are rendered on the next `EditorSystem.Update`.
- The importer table also accepts `.pgf`, `.pmat`, `.pfab`, and the UI menu offers `.bmp` which has **no** importer (NRE). A plugin needs its own extension allow-list.
- Removal (`PummelMod.RemoveAsset(ModAsset)`, `PummelMod.cs:827`) deletes file + `.pmeta` immediately, is not undoable, checks nothing about references, and does not protect the open prefab, the selection or the mod's preview image. The UI wrapper does two of those checks (6.4).
- Material edits and import settings live in memory until "Apply" (`EditorSystem.ReimportAsset`) or the next full save (`PummelMod.Save` re-saves every external asset).
- Prefab files (`.pfab`) are written straight into a truncating `FileStream`; a failure mid-write leaves a truncated file, and a truncated `.pfab` makes the whole mod fail to load (8.7).
- No Steam/Workshop call exists anywhere in these paths; the only write outside the mod folder is a directory created by the PNG/JPG importer (10).

## 1. The import path step by step

### 1.1 UI entry points [C]

| Step | What | Evidence |
|---|---|---|
| Button | Asset Browser toolbar "Import Asset" (opens a menu, left-click popup `AddCustomAssetPopup`) with three items: Texture (`png,jpg,jpeg`, folder `Textures`), Mesh (`obj`, `Props`), Audio (`wav`, `Audio`) | `FileBrowserWidget.cs:428-448` |
| Alternative menu | Asset Browser / right-click context menu "Import Asset" with a wider filter: Texture `png,jpg,jpeg,bmp`, Mesh `obj`, Audio `wav,mp3,ogg` | `EditorSystem.cs:2221-2231` |
| Menu item | `EditorSystem.DrawAssetImportButton(label, filetype, folderName)`: only checks the editor is not in/entering/exiting play mode; calls `ImGui.WindowsOpenFileDialog(filter, modPath, folderName)` | `EditorSystem.cs:2212-2219` |
| Dialog | `ImGui.WindowsOpenFileDialog` stores `folderName` in a **static** field, subscribes `ImportFileDialogCallback`, calls the native `OpenFileDialog(owner, filterList, defaultPath, callback)` (Windows modal dialog, plugin DLL `ModEditorPlugin_Release`), then unsubscribes. The callback gets **one** `(NFDResult, string path)` - no multi-select | `ImGui.cs:832-858` |
| Callback | `ImGui.ImportFileDialogCallback`: on `NFDResult.Okay` calls `EditorSystem.Instance.CurrentMod.AddAsset(path, folder)` inside a try/catch that only logs; on success focuses the Asset Browser, `SelectAsset(list.Last())`, `ForceOpenCustomAssetBrowserFolder(folder)` | `ImGui.cs:844-868` |
| Drag and drop of OS files onto the window | **No handler found** in the decompiled code (all drag/drop is ImGui-internal: asset to hierarchy/scene, hierarchy to browser). The native plugin DLL could do it [U] | grep over `*.cs` |

The disk sub-folder is the fixed `folder` argument (`Textures`, `Props`, `Audio`) combined with `PummelMod.FullAssetsFolder` (`<mod>\Assets\`, `PummelMod.cs:107`). It is **not** the Asset Browser's currently shown folder and not chosen by the user [C]. The browser's "Custom/<folder>" placement is a separate virtual folder (`ModAsset.assetFolder`, set by each importer: `/Props/`, `/Textures/`, `/Audio/`, `/Materials/`, `/Prefabs/`); disk location has no effect on it [C] (`FileBrowserWidget.cs:562`, importers).

### 1.2 `PummelMod.AddAsset(filepath, folder, copy)` [C] (`PummelMod.cs:898-939`)

1. (copy) `new FileInfo(filepath)`, `Path.GetFileNameWithoutExtension`, `Path.Combine(FullAssetsFolder, folder)`, `Directory.CreateDirectory` if missing.
2. (copy) up to 100 candidate names `<stem>`, `<stem>(1)`..`<stem>(99)` + the source extension (case kept); first one where `File.Exists` is false wins. Only the asset file is tested, not its `.pmeta`.
3. (copy) `File.Copy(source, target)` (no overwrite). The whole block is in one try/catch that, on any exception, calls `DeleteFailedAsset(text)` and returns null (see 3.1/3.2).
4. `ImportFile(new FileInfo(target))` (outside any try/catch: exceptions propagate to the caller).
5. If the result is null and `copy`: `DeleteFailedAsset(target)` (target file and target `.pmeta`).
6. If the result is null: `EditorSystem.ShowFailedToImportCustomAssetPopup(filepath)` = a modal `YesNoPopup` "Import Failed" (`EditorSystem.cs:2207`). While it is open, plugin edit tools report INVALID_STATE.
7. Returns the list (may be non-null but empty, see 2.4).

`copy: false` registers the file where it is (the editor's screenshot code uses it for `Textures\ScreenshotN.jpg`, `EditorSystem.cs:1488`); the `.pmeta` is then written **next to that file, wherever it is**.

### 1.3 `PummelMod.ImportFile(FileInfo, forceNewGuid)` [C] (`PummelMod.cs:1056-1087`)

`PummelAssetImporter.LoadFile(mod, file, isRuntime, isBackgroundInit, forceNewGuid)` (`PummelAssetImporter.cs:57`): `GetImporter(file.Extension)` (lower-cased; table in 2.1) then `importer.SetCurrentMod(mod)` then `importer.Load(file, ...)`. **There is no null check on the importer** in `LoadFile` (the plural `LoadFiles` has one), so an extension without an importer throws a NullReferenceException (`PummelAssetImporter.cs:59-60`).

Then for each returned `ModAssetExternal`: `m_newAssets.Add(a)` and `Assets.AddAsset(a, IsBackgroundInit)`. (The "same guid already known -> drop" branch only applies when `EditorSystem.Instance == null`, i.e. never in the editor.)

`ModAssetList.AddAsset` (`ModAssetList.cs:181`): if an asset with the same guid exists it is **merged** (name, Unity object, file path, atlas data replaced; the registered object keeps its identity and its own import-setting fields); otherwise it is inserted sorted by guid, `SetupVariables()` runs, and, if not background-loading and the current mod is an editor mod, `EditorSystem.OnAddAsset` fires, which makes the Asset Browser place the node (`FileBrowserWidget.OnAddAsset`).

### 1.4 `.pmeta` and GUID [C]

`PummelAssetImporter.TryCreateOrLoadMetaAsset<T>` (`PummelAssetImporter.cs:119`): no `<file>.pmeta` -> `new T()` (field initialiser `guid = SerializableGuid.NewGuid()`), `Init(file)` (name = file name without extension, `assetLocation = External`), `SaveMetaFile()` **immediately**. Existing `.pmeta` -> `JsonUtility.FromJson<T>`; any exception -> recreate with a new guid, overwriting the file (`:153`). `forceNewGuid` only replaces `guid`. `SaveMetaFile` returns false on IO error -> importer gets null.

### 1.5 Decode [C]

Per type (2.1): image `LoadImage` on the main thread, audio `UnityWebRequestMultimedia` busy-wait on the main thread, OBJ via the native `LoadObjFile`. All synchronous. Details in 11.

### 1.6 Appearance in the browser [C/I]

`OnAddAsset` event places the node in `Custom/<assetFolder>`. Thumbnails: `EditorSystem.Update` calls `CurrentMod.OnPostImport()` every frame (`EditorSystem.cs:1608-1611`), which renders previews for `m_newAssets` on the main thread and clears the list (`PummelMod.cs:1089-1160`). So a thumbnail appears one frame later; the asset is usable immediately. `EditorSystem.Update` returns earlier in play mode, so no thumbnails then.

### 1.7 Narrowest reusable entry points

| Entry | Takes | Assumes |
|---|---|---|
| `PummelMod.ImportFile(FileInfo, bool forceNewGuid=false)` **(recommended)** | A file already under `<mod>\Assets\` | Main thread; mod loaded and being the editor mod (`EditorSystem.OnAddAsset`, `FileBrowserWidget` alive); extension has an importer (else NRE); no selection, no dialog, no popup |
| `PummelMod.AddAsset(string, string, bool)` | Source path + folder name | Same, plus `EditorSystem.Instance` (popup on failure); both deletion hazards (3); the 100-name loop |
| `ImGui.ImportFileDialogCallback(NFDResult.Okay, path)` | Same as above | Static `folder` field must be set; also moves selection/focus; not usable without it being set - do not use |
| `PummelAssetImporter.LoadFile(mod, FileInfo, ...)` | File in the mod | Does not register the asset (caller must `AddAsset`); no benefit over `ImportFile` |

## 2. Accepted file types, failure behaviour, limits

### 2.1 Extension table [C] (`PummelAssetImporter.cs:79-114`)

`GetImporter` lower-cases the extension, so case is accepted there.

| Extension(s) | Importer | Asset class (AssetType) | Offered in browser button | Offered in context menu |
|---|---|---|---|---|
| `.obj` | `PummelOBJImporter` | `PummelProp` (Unity type `Prop`) + nested `PummelMesh` | yes | yes |
| `.wav`, `.mp3`, `.ogg` | `PummelAudioImporter` | `PummelAudioClip` (`AudioClip`) | `.wav` only | `wav,mp3,ogg` |
| `.png`, `.jpg`, `.jpeg` | `PummelImageImporter` | `PummelTexture` (`Texture2D`) | yes | yes |
| `.bmp` | **none** | - | no | **yes (filter only)** |
| `.pgf` | `PummelVisualScriptImporter` | `PummelVisualScript` | no | no |
| `.pmat` | `PummelMaterialImporter` | `PummelMaterial` | no | no |
| `.pfab` | `PummelPrefabImporter` | `PummelPrefab` | no | no |

The wiki lists WAV only for audio and BMP as "avoid using" (`docs/md/Importing_Audio.md`, `Importing_Images.md`); code accepts mp3/ogg and rejects bmp. A plugin allow-list: `.png .jpg .jpeg .wav .mp3 .ogg .obj`; never `.pgf .pmat .pfab` (they are created by the editor, and `.pfab/.pmat` bytes from outside are interpreted as binary with side effects), never `.bmp`.

### 2.2 Case handling [C/I]

`GetImporter` lower-cases, but `PummelAudioImporter` switches on the **exact-case** `file.Extension` (`PummelAudioImporter.cs:16-27`): `.WAV` yields `AudioType.UNKNOWN` and tag `Unknown`. Whether Unity then sniffs the format is [U]. `PummelMod.RefreshImportedFiles` groups by exact-case extension but the lookup lower-cases again. Avoidance: write the copy with a lower-case extension.

### 2.3 Unsupported / empty / corrupt / large files

| Case | Behaviour | Evidence |
|---|---|---|
| Extension without importer (`.bmp`, `.txt`, none) | NRE out of `ImportFile`. In `AddAsset(copy:true)` the copy is **left in `Assets\<folder>`** (no `.pmeta`, no popup; the dialog callback logs "Exception Importing"). On mod load `LoadFiles` ignores it with a warning. | `PummelAssetImporter.cs:57-62`, `PummelMod.cs:929`, `ImGui.cs:864` [C] |
| Zero-byte / corrupt PNG, JPG | `Texture2D.LoadImage` returns false -> importer returns null -> `AddAsset` deletes copy + `.pmeta`, shows popup. `.pmeta` was already written first. | `PummelImageImporter.cs:246,96` [C] |
| Zero-byte / corrupt / mislabelled audio | `UnityWebRequest` result != Success or null clip -> "Failed creating audio clip" -> null -> same cleanup + popup. Popup text itself says "mp3 renamed to wav will fail". | `PummelAudioImporter.cs:41,58-71`, `EditorSystem.cs:2207` [C] |
| Zero-byte / garbage OBJ | `ModImport.LoadObjFile` false, or `numVertices < 3`, or invalid UV index -> log + `return new List<>()` (**empty, not null**) -> `AddAsset` sees non-null: no cleanup, no popup; the copy **and its new `.pmeta` stay**, nothing is registered. It is retried (and fails again silently) at every mod load. What the native loader does with zero bytes is [U]. | `PummelOBJImporter.cs:109-160` [C] |
| OBJ where the `.pmeta` could not be written | `TryCreateOrLoadMetaAsset` returns null; the OBJ importer dereferences it (`GetModAsset(pummelProp.MeshAsset)` before any null check) -> NRE | `PummelOBJImporter.cs:100-101` [C] |
| Very large file | **No limit in code.** Image: `File.ReadAllBytes` + `LoadImage` (Unity texture limits apply [U]); audio: whole clip via `DownloadHandlerAudioClip`; OBJ: `File.ReadAllText` + native parse. | importers [C] |

### 2.4 Limits in code [C]

None found for: file size, texture dimensions, vertex/triangle count, audio length/sample rate, name length, allowed characters (grep over importers, `PummelMod`, validators; `ModMinigameValidator`/`ModBoardValidator` only warn about a missing preview image). Behaviours tied to size:

- Mesh: more than 65535 vertices sets `IndexFormat.UInt32` (`PummelOBJImporter.cs:169`); wiki says such meshes break Mesh Colliders (`docs/md/Mesh_Asset.md`).
- Texture: compression only if `compression != None` and width and height are both multiples of 4 (`PummelImageImporter.cs:266`). Resize to 640x360 only when loading in the background (never in the editor).
- Names: the asset name is the file stem; invalid file-name characters cannot occur for an existing file; rename via the UI does no validation and lets the IO exception surface in a popup (`ModAssetExternal.cs:68-110`).
- Windows path length: not handled; behaviour on > 260 chars is [U] (see hazard H3 for what a path exception does inside `AddAsset`).

Policy limits are therefore the plugin's job (suggested: file size cap, image dimension cap read from the PNG/JPG header, audio cap, OBJ text size and face-count cap, name charset and length).

## 3. Name collisions and the deletion hazards

### 3.1 The 100-duplicates case [C] (`PummelMod.cs:912-927`)

Trigger: all of `<stem>.<ext>`, `<stem>(1).<ext>` ... `<stem>(99).<ext>` already exist in the target folder. The loop ends with `text` = the `(99)` candidate, which exists. `File.Copy` (no overwrite) throws IOException; the catch calls `DeleteFailedAsset(text)`, which deletes that **existing** `(99)` file and its `.pmeta` (`PummelMod.cs:941-951`). Consequence: loss of one existing custom asset and its sidecar (guid lost -> references dangle). Avoidance: the plugin picks and verifies the target name itself (3.4) and never relies on the loop.

### 3.2 NEW finding: the source file is deleted when the failure happens before the target name is chosen [C] (`PummelMod.cs:900-927`)

`text` starts as the **source path**. It is reassigned to the target inside the candidate loop only. Any exception from `new FileInfo(filepath)`, `Path.GetFileNameWithoutExtension`, `Path.Combine(FullAssetsFolder, folder)`, `Directory.Exists/CreateDirectory(<Assets>\<folder>)` reaches the catch with `text == filepath`, and `DeleteFailedAsset(filepath)` then deletes **the source file and `<source>.pmeta`**. Triggers: invalid characters or a colon in the source path or in `folder`, path too long, `Assets\<folder>` exists as a file or cannot be created (permissions), `folder` null. This is the hazard that violates "never modify or delete a file the plugin did not create". It also applies to a source inside another mod's folder. Avoidance: never call `AddAsset(copy:true)`; do the copy in the plugin (3.4).

### 3.3 Other collision behaviour [C]

- Same name, free suffix: becomes `name(1).ext` (UI path). The new asset's `name` is then `name(1)`.
- Orphan `<target>.pmeta` (sidecar without its asset file): the existence test ignores it; `TryCreateOrLoadMetaAsset` **loads it** (old guid, old settings, old material data) for the new file; if the import then fails, `DeleteFailedAsset` deletes it. Avoidance: require that neither `<target>` nor `<target>.pmeta` exists.
- Case-insensitive file system: `Name.png` vs `name.png` are the same file; `File.Exists` handles that, but the **asset-name uniqueness checks** used for prefabs/materials compare names case-sensitively (8.2, 7.1).
- `AddDuplicatedAsset` (Ctrl+D, `PummelMod.cs:873`) loops without a bound until `<stem>(n)` and `<stem>(n).pmeta` are both free, then copies file and sidecar, imports with `forceNewGuid: true`. Safe against overwrite. Props keep the nested `PummelMesh` guid of the original (only the outer guid is renewed) [I].

### 3.4 Pre-check that makes the hazards unreachable

Plugin-owned copy: (a) resolve the source to a full path, require it to be a regular existing file with an allowed extension, size within the cap; (b) the target folder is one of three constants (`Textures`, `Props`, `Audio`) under `FullAssetsFolder`, verified to resolve inside it after `GetFullPath`, created by the plugin before anything else (so no exception can occur in an unsafe place); (c) choose the first free `<stem>[(n)]<lowercase ext>` such that neither the file nor the file + `.pmeta` exists, with a plugin-chosen upper bound that returns an error instead of falling through; (d) copy with `File.Copy(src, dst, overwrite: false)`; (e) call `ImportFile`; (f) on null/empty/exception, delete only `dst` and `dst + ".pmeta"` and only if the plugin created them in this call.

## 4. What is written to disk, when

| What | When | How | Evidence |
|---|---|---|---|
| Copied asset file `Assets\<folder>\<name>.<ext>` | At import, before decoding | `File.Copy` (no overwrite) | `PummelMod.cs:920` |
| `<file>.pmeta` | At import, **before** decoding (so a failed decode leaves it until `DeleteFailedAsset`); again on every `Save()` of the mod, on Apply/Reimport, on prefab/material creation | `File.WriteAllBytes` (in place, not atomic) of `JsonUtility.ToJson` of the asset with the Unity object detached | `ModAssetExternal.cs:48-66`, `PummelAssetImporter.cs:128` |
| `.pmeta` content | texture: base fields + `m_compression`, `m_filter`, `m_wrapMode`; prop: base + nested `m_meshAsset` (`PummelMesh` with `m_recalculateNormals`, own guid); audio: base fields only; prefab: base + `isEnabled`; material: base + `m_shaderName` + `m_shaderData` (byte[] holding the binary "PMAT" blob). Base fields: `enabled, name, guid, tags, assetTypeString, atlasIndex, atlasPtrIndex, assetLocation, assetFolder, runtimeAtlas`. `filePath/fileName` are not stored | classes above; `docs/mod-format.md` 3.3 |
| `.pmat` | `PummelMaterial.Save`: file containing only the string "PMAT", then the `.pmeta` | `FileMode.Create` | `PummelMaterial.cs:49-56` |
| `.pfab` | `PummelPrefab.Save`: full hierarchy, then the `.pmeta` | `File.Open(FileMode.Create)` + `SaveTransform` straight into the stream | `PummelPrefab.cs:67-78` |
| Texture cache directory | On every PNG/JPG import, even in the editor: `<persistentDataPath>\PummelModCache\<publishedFileId>\` is created (not the cache files; `m_useCache` is false in the editor) | `Directory.CreateDirectory` | `PummelImageImporter.cs:74`, `ModCacheSystem.cs:18-37` |
| Nothing else | No thumbnail files, no index | - | - |

Import writes immediately; it does **not** wait for `save_mod`. `save_mod` (`PummelMod.Save`, `PummelMod.cs:567`) then rewrites the `.pmeta` of every non-`HideAndDontSave` external asset (`PummelMesh` is `HideAndDontSave`, but it is serialised inside its prop) and the `.pfab`/`.pmat` of every prefab/material, unconditionally.

Failed import: partial files remain in these cases: unsupported extension (copy only), OBJ failures (copy + `.pmeta`), exception after the copy inside `ImportFile` (copy, maybe `.pmeta`). They are cleaned only on the null-result path. The source file is never moved, modified or locked by the copy path (`File.Copy` opens it for reading, closed on return) [C]; it is deleted in the 3.2 case.

## 5. Import settings per asset type

| Type | Setting (field, type, default) | Where shown | Effect |
|---|---|---|---|
| Texture | `m_compression` `PummelTextureCompression` {Normal, HighQuality, None}, default Normal; `m_filter` `FilterMode` {Point, Bilinear, Trilinear}, default Bilinear; `m_wrapMode` `TextureWrapMode` {Repeat, Clamp, Mirror, MirrorOnce}, default Repeat. All `[SerializeField]` private, no setters; getters `GetCompression/GetFilterMode/GetWrapMode` | `PummelTexture.DrawInspector` (read-only "Image resolution" + three enum combos) `PummelTexture.cs:33-42` | Applied at decode: `Compress(highQuality)` if not None and dims divisible by 4; filter and wrap set on the `Texture2D`. No sRGB/mipmap/max-size/alpha options: `new Texture2D(1,1)` + `LoadImage` defaults (`PummelImageImporter.cs:240-290`) [C] |
| Mesh (OBJ) | `PummelMesh.m_recalculateNormals` bool, default false, public property `RecalculateNormals` | `PummelProp.DrawInspector`: read-only Tris/Verts + "Recalculate Normals" checkbox `PummelProp.cs:38-49` | Normals recalculated if the file has none or the flag is set. No scale, collider or unit options [C] |
| Mesh (always applied) | handedness fix: x of vertices and normals negated and triangle order reversed when the mod's `ModCreatedVersion >= 1` (new mods) (`GetCreatedVersion`, `PummelMod.cs:219`) | - | `PummelOBJImporter.cs:76-90,182` [C] |
| Mesh (always applied) | one hidden `Standard` material per submesh; submeshes follow the OBJ material ids; a neighbouring `<name>.mtl` (same folder as the copy, **not copied by the editor**) is read only for those ids | - | `PummelOBJImporter.cs:104-106,198-210` [C] |
| Audio | none; read-only channels, frequency, duration; duration tag (`< 5s`, `5s-30s`, `30s-1m`, `1m-5m`, `> 5m`) and format tag added at import | `PummelAudioClip.DrawInspector` | - |
| Prefab | `isEnabled` bool (default true) | none (set by the open prefab's object active state) | `PummelPrefab.cs:9,67-71` |
| Material | shader + all shader properties (section 7) | `PummelMaterial.DrawInspector` | - |

**Apply** (Inspector bottom bar, enabled only when dirty) calls `EditorSystem.ReimportSelectedAsset()` -> `ReimportAsset(asset)` (`InspectorWidget.cs:68`, `EditorSystem.cs:902-933`): `asset.Save()` (writes `.pmeta`, plus `.pmat`/`.pfab` for those types), then if the source file exists `CurrentMod.ImportFile(new FileInfo(asset.filePath))` (re-decode, merge by guid), `SerializeSelectedObject()` (new clean baseline), focus the browser. **Revert** = `JsonUtility.FromJsonOverwrite` of the clean snapshot (+ `PummelMaterial.Load()`), `EditorSelectionManager.cs:390`. Dirty = `Serialize()` bytes differ from the snapshot (`:381`); this is computed for the selected asset only.

**"Unapplied Import Settings" popup** (buttons Apply / Revert / Cancel; Cancel also reverts, `EditorSystem.cs:935-950`): raised by `OnAttemptedToDeselectDirtyAsset` when `DeselectSelectedAsset()` is called while the selected asset is dirty (clicking empty browser space, selecting another asset, `SelectAsset` of another asset while dirty) (`EditorSelectionManager.cs:326-375`, `EditorSystem.cs:1941`). It is a modal popup: plugin edit tools would get INVALID_STATE. A plugin that edits import settings must therefore apply them immediately (set field, then `ReimportAsset`) and not leave an asset selected and dirty.

**Missing source file**: `ReimportAsset` first calls `asset.Save()` (recreates the `.pmeta`), then, because the file is gone, `CurrentMod.RemoveAsset(asset)` and `DeselectSelectedAsset()` - i.e. a reimport of an asset whose file vanished removes the asset and deletes the `.pmeta` (confirmed doc 02) [C].

**Side effect on references (inferred hazard H8)**: texture/audio reimport creates a **new** `Texture2D`/`AudioClip` object and the asset list swaps the object into the registered asset; props and materials reuse their existing Unity object. Components/materials that still hold the old texture object are no longer resolvable by `GetAssetWithObject` and would serialise as null at the next save [I, U]. After a texture reimport, re-assign the texture on materials (or reload) and verify.

## 6. Removal

### 6.1 What `PummelMod.RemoveAsset(ModAsset pa)` does [C] (`PummelMod.cs:827-865`)

If `pa` is a `ModAssetExternal`: `OnDestroy` (frees atlas slot, `Object.Destroy` of the Unity object; `PummelProp` also destroys its mesh; `PummelPrefab.OnDestroy` nulls `PrefabReference` on every `ModPrefabInstance` in the scene, `PummelPrefab.cs:94-110`) then `DeleteFile(filePath)`: `File.Delete` of the file then of `<file>.pmeta`, each in its own try/catch that only logs (a locked file leaves file and sidecar behind while the asset is gone from memory; it returns on next load with the same guid). Then `pa.OnRemove(isRunTime)` (`PummelProp.OnRemove` removes the nested mesh asset: second delete of the same path, harmless) and `Assets.RemoveAsset(pa)` -> `List.Remove` + `EditorSystem.OnRemoveAsset` -> `FileBrowserWidget` removes the node. Non-external assets (internal/runtime) are removed from the mod list only; no file is touched.

### 6.2 UI wrapper [C] (`EditorSystem.cs:1955-1990`)

Delete menu/Del key -> `DeleteAsset` -> confirmation popup "Are you sure you want to permanently delete X? This action cannot be undone!" (Yes/Cancel) -> `DeleteAssetResult`: if the asset is the open prefab then `CurrentPrefab = null` first (which saves the prefab); `CurrentMod.RemoveAsset`; `DeselectSelectedAsset()` if it was selected. The editor does **not** scan for or warn about references.

### 6.3 References after removal

- Saved form of an asset reference is the asset's guid, resolved at write time by `WriteObject` -> `GetAssetWithObject(object)` and at read time by `ReadObject` -> `GetAssetWithGuid` (`Extensions.cs:534-585`). After removal the asset is no longer in the list, so on the next save a reference to it is written as "none" (`false`), i.e. **references become null silently on save** [C for the mechanism; I that every reference type goes through it: components/materials/action parameters use it, `ModPrefabInstance.PrefabGUID` and the `ModItem/ModSpawner` prefab guid fields use `GetWrappedAsset/SetWrappedAsset` which also yield Empty for a null asset (`WorkshopController.cs:821-840`)].
- In memory the component still points at the destroyed Unity object (pink material, silent audio source) until reloaded.
- On disk, files saved before the removal still hold the guid; if the mod is reopened without saving, load logs "Couldn't find object with guid" and sets null (`Extensions.cs:572-576`).
- Prefab removal: instances get `PrefabReference = null` with the label "(Missing Prefab!)" (`ModPrefabInstance.cs:55-57`, `PummelPrefab.cs:105`).
- Mod preview image: `GetPreviewImageGuid()` (`MinigameMod.cs:126`, `BoardMod.cs:95`; `ModWorkshopItem.previewImageGuid`) is not protected; removing that texture leaves a dangling settings guid and validator warning "Missing ... preview image" (`ModMinigameValidator.cs:28`, `ModBoardValidator.cs:36`). The plugin must refuse to remove it (it belongs to the Workshop-item side that stays off limits).
- Undo: not undoable and not on any undo stack (doc 03, undo section). Scene undo snapshots that contain the guid would restore a dangling guid [I].

### 6.4 What the UI does that a direct call must replicate [C/I]

Refuse if the asset is the open prefab (the editor sets `CurrentPrefab = null` first; the plugin must not do that implicitly because it saves the prefab); deselect the asset if it is the selected asset, using `DeselectSelectedAsset(changingMod: true)` (the normal overload raises the Unapplied popup and does nothing when the selection is dirty; a destroyed selected asset makes the inspector throw every frame) [I]; refuse during play mode.

### 6.5 Custom vs built-in [C]

`ModAsset.assetLocation` is a guid: `AssetLocationGuid.Internal/External/Runtime` (`AssetLocationGuid.cs`); `SetupVariables` derives `InternalAsset/ExternalAsset/RuntimeAsset`. `ModAssetExternal.Init` sets `External` on every load, so the location is never read from the `.pmeta`. Built-in assets live in `WorkshopController.Instance.Assets` (separate list, Addressables, `assetTypeString` set) and are never in `mod.Assets` as external. The browser's "Custom" root shows what `OnAddAsset` placed; the picker (`ChooseAssetPopup`) searches `CurrentMod.Assets` then `WorkshopController.Assets`. Runtime entries are scene behaviours registered by `ModBehaviourMonitor` in `mod.Assets` (guid lookup); they are not assets and a `RemoveAsset` on one would only unlist it. `DrawAsset` only selects `ModAssetExternal` instances, so the UI cannot delete built-ins or runtime entries [C]. **External does not imply "inside the mod's Assets folder"**: `AddAsset(path, "", copy:false)` registers an external asset at any path and `RemoveAsset` would delete that path. A plugin removal guard must therefore check `ExternalAsset`, class in an allow-list, and `Path.GetFullPath(filePath)` strictly under `FullAssetsFolder`.

### 6.6 Finding all references before removal (plugin side) [I]

Walk the three places where guids/objects are held: (1) every `ModBehaviour` under `m_hierarchyRoot` **and** `m_prefabsRoot` (include inactive; the plugin's `ComponentReader`/`ValueConverter` already read asset-typed properties by reflection and `ValueConverter.cs:218` maps objects to assets); (2) every material of the mod (`PummelMaterial.UnityMaterial`, textures via shader property names/`GetTexture`); props reference their `Prop` (and mesh); prefab-typed fields (`ModPrefabInstance`, `ModItem`, `ModSpawner`, see `RequiredMembers`); (3) action parameters (asset-typed action properties; `ActionParams.cs:448` already resolves them) and mod settings (preview image, music/ambience, lighting presets: the settings tree `get_open_mod` reads). Compare by guid after mapping with `GetAssetWithObject`. Not found in code: a built-in "find references" feature [C].

## 7. Materials

### 7.1 Creation [C] (`EditorSystem.CreateNewMaterial()`, `EditorSystem.cs:2425-2438`; menu `DoAssetCreateMenu`, `:2263-2279`)

Right-click in the browser or the Import Asset area: Create > Material. The method: file name `Assets\Materials\<GetUniqueAssetName<Material>()>.pmat` (`Material_0`, `Material_1`, ... checked against **asset names of type Material**, case-sensitive, not against disk; `AssetFilePrefixes`, `:16`, `:2598`); `Directory.CreateDirectory(Materials)`; `new PummelMaterial(FileInfo)` (new `Material` with shader "Standard Decal Receiver", hide flags HideAndDontSave); shader replaced by the internal Shader asset whose guid is `EditorSystem.defaultShaderGuid` (a serialized field, value in the scene/prefab data [U]; NRE if it does not resolve); `assetFolder = "/Materials/"`, tag "Materials"; `CurrentMod.AddAsset` (registers + thumbnail queue); browser folder switched to Materials; **`pummelMaterial.Save()` immediately** (writes `.pmat` stub and `.pmeta`). The menu then selects the new asset; the method itself does not select. Not on any undo stack. The `.pmat` is overwritten if a file of that name exists on disk without a loaded Material of that name (case difference, unimported orphan) [I; avoidance: check disk case-insensitively first].

### 7.2 Storage [C]

`.pmat` = the 4-byte-string "PMAT" only; data = `.pmeta` fields `m_shaderName` + `m_shaderData` (version 0.2 blob: property count, then per shader property name, type byte, value (Color, Vector, Float/Range float, Texture as `WriteObject` = guid or none); keyword list; main-texture scale and offset) (`PummelMaterial.cs:58-125`). Load (`PummelMaterial.Load`, `:127`): finds the shader by name among **internal** Shader assets, falls back to `Standard`; rebuilds the material; for minor version >= 1 properties are matched by **index in the current shader**, so the blob is only valid for the shader it was written with (a shader change must go through `Serialize()`/`Save()`, not hand-edited bytes). Textures are looked up by guid (built-in or mod textures, `(Texture2D)` cast); a missing one logs and becomes null.

### 7.3 Shaders and properties [C for the inspectors; shader list U]

Shader choice: the "Shader" field in `PummelMaterial.DrawInspector` opens the asset picker for Shader assets restricted to `AssetLocation.Internal` (built-in shaders only; `PummelMaterial.cs:205-213`) and sets `Material.shader`. The full shader list is data in the game's asset list [U]; `list_assets type Shader includeBuiltIn` enumerates it at runtime. Inspectors exist for:

| Shader name | Inspector | Properties shown (name, type, limit) |
|---|---|---|
| `Standard`, `Standard Decal Receiver` | `StandardShaderInspector` | `_Mode` enum `MaterialRenderingMode` {Opaque 0, Cutout 1, Fade 2, Transparent 3}; `_Cutoff` float 0..1 (Cutout only); `_MainTex` Albedo texture; `_Color` color; `_MetallicGlossMap` texture (keyword `_METALLICGLOSSMAP` set when non-null); `_Metallic`, `_Glossiness` ranges taken from the shader (Unity Standard 0..1 [I]); `_BumpMap` texture (keyword `_NORMALMAP`, `_BumpScale` forced to 1); `_ParallaxMap` texture (keyword `_PARALLAXMAP`) + `_Parallax` range from shader (0.005..0.08 [I]); `_OcclusionMap` texture; Emission bool (keyword `_EMISSION`) + `_EmissionColor` HDR + `_EmissionMap`; main texture tiling and offset (Vector2) |
| `Custom/ModBoardSpaceShader` | `CustomBoardSpaceInspector` | `_MainTex`, tiling/offset, `_EdgeColor`, `_InsideColor`, `_IconColor` (colors), `_Metallic`, `_Glossiness`, `_IconScale` (ranges from shader) |
| `Particles/Additive Intensify Mod` | `ParticleShaderInspector` | `_TintColor`, `_MainTex`, tiling/offset, `_InvFade` (range), `_Glow` (range) |
| `Legacy Shaders/Particles/Alpha Blended Mod` | `AlphaBlendedShaderInspector` | `_TintColor`, `_MainTex`, tiling/offset, `_InvFade` (range) |
| any other | `DefaultShaderInspector` | every shader property not marked `HideInInspector`: Color, Vector, Float (free), Range (slider with shader limits), Texture (`TextureDummy` picker); tiling/offset after each |

`StandardShaderInspector.UpdateKeywords` (`:100-129`, also run from `PummelMaterial.Load` via `UpdateKeywords`) derives from `_Mode`: keywords `_ALPHATEST_ON`, `_ALPHABLEND_ON`, `_ALPHAPREMULTIPLY_ON`, `_SrcBlend`, `_DstBlend`, `_ZWrite`, override tag `RenderType`, `renderQueue` (-1/2450/3000/3000). The inspector enforces the keyword/`_BumpScale` logic **only while it is drawn**; a plugin that sets properties directly must call `PummelMaterial.UpdateKeywords()` and set the texture keywords itself [I].

### 7.4 Texture assignment and use [C]

Material texture slots take a `Texture2D` object chosen from the picker (`AssetLocation.All`: built-in or mod textures); persisted as the texture asset's guid. A material is assigned to a prop with `ModProp.customMaterials` (`Material[]`, `ModProp.cs:24,107`) or to renderers with `materials` (existing `set_property` already resolves a material asset guid to `PummelMaterial.UnityMaterial`; nothing further is needed there). What is missing for an agent is only: create material, set shader, set material properties (typed by shader), assign textures, save the material. Imported OBJ props carry hidden `Standard` materials per submesh that are not assets (`PummelOBJImporter.cs:198-210`).

### 7.5 When edits are written, undo, dirty [C]

Edits mutate the live `Material` directly; nothing is written until Apply (`ReimportAsset` -> `Save`) or a full mod save. The material counts as dirty while it is the **selected** asset and `Serialize()` differs from the snapshot. No undo. Material identity is stable across reimport (the importer reuses the registered object) so renderers keep working. Editing a material that is not selected never shows "dirty" and is only persisted by Apply or `save_mod` [I].

## 8. Prefabs

### 8.1 Creation [C] (`EditorSystem.CreateNewPrefab(ModTransform prefabTransform = null)`, `EditorSystem.cs:2411-2423`)

Two UI routes: Asset Browser Create > Prefab (empty prefab, `prefabTransform = null`; the menu then selects it) and **dragging a hierarchy object onto the Asset Browser** (`FileBrowserWidget.cs:258-281`; one object with its subtree; refused with a message popup if the subtree contains `ModPrefabInstance` or `ModBoardSpace`; the hierarchy rule "no multi-select" is just that one payload).
Steps: name = `GetUniqueAssetName<PummelPrefab>(source name or null)` (the source object's name if no `PummelPrefab` asset has that exact name, else `name_1`...; empty: `Prefab_0`, `Prefab_1`, ...; compared to asset names only); `Assets\Prefabs\<name>.pfab`; `new PummelPrefab(FileInfo, source)` instantiates a copy of the source (or a new `GameObject`), parents it under `m_prefabsRoot`, resets position/rotation/scale of the prefab root to origin/identity, adds `ModTransform`/`ModPrefab`; `assetFolder "/Prefabs/"`, tag; `CurrentMod.AddAsset` (registers); browser folder; **`Save()` immediately** (`.pfab` + `.pmeta`); `PrefabObject.SetActive(false)`. It does not enter the edit context and does not change selection (the menu does). The new object name is not sanitised for the file system: an object name with `/ \ : * ? " < > |` or a trailing dot/space fails (exception thrown after registration in memory: the asset is registered with no file) [I]; name uniqueness is case-sensitive but NTFS is not: "Cube" vs an existing "cube" file overwrites it via `FileMode.Create` [I].
Cross references: instantiating copies component values; actions inside the copy that referenced objects of the source subtree keep the source's guids (the self-reference remap is only run on prefab *instances* and on `forceNewGuid` imports, `PummelPrefabReferenceUpdater`) [I, U]. Fresh component guids come from the `ModBehaviour` constructor (doc 03) [I].

### 8.2 `.pfab` and its `.pmeta`

Binary written by `ModHierarchySerializer.SaveTransform(BinaryWriter, Transform, ignoreRoot, ignoreChildren)` (`ModHierarchySerializer.cs:79`) with a version header; loaded by `PummelPrefabImporter` (`LoadVersion`, `LoadHierarchy`, `LoadComponents`, `LoadValues`). `.pmeta` has the base fields + `isEnabled`. Written at creation, at `CurrentPrefab` change away from the prefab, at Apply/Reimport and **at every `save_mod`** for every prefab in the mod (`PummelMod.Save` loop).

### 8.3 Edit context (`EditorSystem.CurrentPrefab`, `EditorSystem.cs:437-495`) [C]

Entering: `PummelPrefab.Open()` (double-click in the browser or an instance, or the Prefab inspector button) = `EditorSystem.CurrentPrefab = prefab`: clears selection, **saves the previously open prefab** (`isEnabled` from the active state, `Save()`, deactivates it), remembers the scene camera, activates the prefab object, switches `m_prefabsRoot` active / `m_hierarchyRoot` inactive, focuses the camera. Hierarchy shows `PrefabObject` as the root with a "Return to Hierarchy" bar (`HierarchyWidget.cs:310`). Leaving through the UI menu item: stores `isEnabled`, **`ForceUpdate(prefab)` on every `ModPrefabInstance` in the scene that references it** (this is the only propagation to live instances), `CurrentPrefab = null` (setter saves the prefab and hides it), then `ReimportAsset(prefab)` (`HierarchyWidget.cs:310-328`). Setting `CurrentPrefab = null` directly skips the instance refresh and the reimport.

### 8.4 Which plugin tools work in the prefab context

From the plugin sources: all object/component/action tools resolve their targets via `EditContext` (creation root = `PrefabObject`, addressable = the prefab object and its non-locked descendants) and so work inside a prefab context; `get_hierarchy` reports `context: "prefab"` (`HierarchyTools.cs:64`); `create_object` refuses kinds the editor disables there (prefab instance, board space) (`ObjectEditTools.cs:125`, `ObjectKinds`); the undo tool uses the per-prefab undo manager (doc 01/03). `save_mod` works. **Refused**: `open_test_mod`/`create_test_mod` (and any lifecycle switch) - `ModTools.CheckCanSwitchAway` (`ModTools.cs:413-418`) refuses because leaving the mod sets `CurrentPrefab = null`, which would **implicitly save the prefab** and rewrite its files outside an explicit save; playtest tools refuse because the Play button saves the prefab and returns to the scene (`PlaytestTools.cs:105,132`). There is no tool that enters or leaves the prefab context today. For prefab creation the plugin does not need to enter it; for populating a prefab it would need `enter_prefab`/`leave_prefab`; `leave_prefab` must replicate the UI sequence (instance `ForceUpdate`, `CurrentPrefab = null`) and decide on `ReimportAsset` (see H6: reimport destroys the in-memory prefab before it parses the file). The lifecycle refusal needs no change for Part B as long as no prefab is left open by a tool.

### 8.5 Placing instances and propagation

Instances: existing `create_object kind prefabInstance` = `AddGameObject(NewPrefabInstance)` + `ModPrefabInstance.PrefabReference` (`ModPrefabInstance.cs:20-48`): `UpdatePrefab()` destroys the old child copy and instantiates `PrefabObject` as a child (new component guids, `PummelPrefabReferenceUpdater`, `ModPrefab` and outline components stripped). Triggers of `UpdatePrefab`: setting `PrefabReference` (not in play mode), `OnFinishedLoad` (mod load), `OnPostDuplicate`, `ForceUpdate`. Saved scene stores only the reference and the instance's transform (children not saved), so after reload every instance reflects the current `.pfab`. Without `ForceUpdate`, live instances stay stale until reload [C/I].

### 8.6 GUIDs and undo inside a prefab [C/I]

Component guids inside the `.pfab` are stored and stable; instances always get fresh guids per instantiation and action self-references are remapped by old->new mapping. Forced new guids on `forceNewGuid` import (Duplicate) run `AssignAndRecordNewGUIDsToComponents` + `UpdateActionSelfReferences` (`PummelPrefabImporter.cs:LoadValues`). Undo: one `EditorUndo` per open prefab, selection resets when the context changes; asset creation itself is not undoable.

### 8.7 Truncated-file hazard, detection, avoidance [C]

`PummelPrefab.Save` opens the `.pfab` with `FileMode.Create` (truncates immediately) and serialises directly into the file stream; an exception in any component's `OnBeforeSerialize`/writer, a full disk or a crash leaves a truncated file (scene/postprocess binaries, by contrast, are buffered in a `MemoryStream` first: `ModDataReaderWriter.SaveScene`, `:569-587`). Consequences: (a) on the next mod open `PummelPrefabImporter` reads past the end; the exception propagates out of `RefreshImportedFiles` (which has a try/catch that turns it into `ModLoadResult.ImportFilesException`, `PummelMod.cs:953-996`) and the **whole mod fails to load** [C]; (b) in the running editor, `ReimportAsset(prefab)` first destroys the registered `PrefabObject` (`PummelPrefabImporter.cs:84`) and then fails reading, losing the in-memory prefab. Avoidance: dry-run `SaveTransform` into a `MemoryStream` (no file touched) and abort on exception; write through a temp file in the same folder and replace; verify (length equals the dry-run length, or re-read the header) before registering success; never call `ReimportAsset` on a prefab unless the file was just verified; remember `save_mod` rewrites all prefabs, so one component whose serialisation throws damages every prefab write in that call (the existing `PummelMod.Save` has no try/catch, doc 02).

## 9. Sources outside the mod

With the plugin-owned copy: the source is only opened for reading by `File.Copy`; the `.pmeta` of a source is never consulted (only the copy's); no write, rename or lock happens at the source; so reading from another mod's folder has no side effect on that mod [C]. Things that do care about the path:

- Read-only source: fine (copy of a read-only file keeps the read-only attribute on the copy [I]; the importer reads only, but later `SaveMetaFile` writes the sidecar, not the file; a later `RemoveAsset`/overwrite of a read-only copy would fail the delete, leaving file and sidecar [I]). Clear the attribute on the copy.
- Network / UNC paths, very long paths, non-ASCII: .NET copy semantics; failures raise exceptions inside the unsafe region of `AddAsset` (3.2). Non-ASCII in the **mod folder** path matters to the audio loader, which passes the raw path string (not a `file://` URI) to `UnityWebRequestMultimedia.GetAudioClip` (`PummelAudioImporter.cs:56`) and to the OBJ native call for the OBJ text only [U]; the source path itself is irrelevant after the copy.
- `AddAsset(copy:false)` or `ImportFile` on a file outside the mod: writes a `.pmeta` next to it and later removal would delete it - never allowed in the plugin.
- `.obj` with `.mtl`: the loader looks for the `.mtl` next to the **copy**; the editor does not copy it (1.2) [C].

## 10. Workshop and cache [C]

No import, removal, material or prefab method touches Steam: grep over `PummelAssetImporter`, the five importers, `ModAsset*`, `PummelMod` (asset parts), `PummelPrefab`, `PummelMaterial`, `EditorSystem` asset handlers and `FileBrowserWidget` finds no `Steam*`, no `SteamUGC`, no `WorkshopModInstaller`. `workshopItem.publishedFileId` is read only to name the texture cache folder. `PummelModCache`: the PNG/JPG importer creates `<persistentDataPath>\PummelModCache\<publishedFileId>\` (directory only, and only because `GetPathStringsForFile` runs before the `m_useCache` check) and in the editor never reads or writes cache files (`m_useCache = EditorSystem.Instance == null && Settings.UseModCache`, `PummelImageImporter.cs:61`); `DeleteCacheForTexture` and `CreateTextureCache` run only when `m_useCache` is true. Audio, OBJ, material, prefab: no cache. `ModCacheSystem` sweep functions are not reachable from these paths. The `previewImageGuid` of `ModWorkshopItem` is the one Workshop-item field these operations can indirectly touch (removing the preview texture, 6.3).

## 11. Threading and timing [C]

| Operation | Thread / timing | Completion signal |
|---|---|---|
| `AddAsset` / `ImportFile` | Synchronous on the caller. On the main thread everything inlines (`GameManager.RunOnMainThread` runs the action directly when already on the main thread, `GameManager.cs:823-829`; exceptions then propagate). From a worker thread `RunOnMainThread` waits on the dispatcher and **logs and swallows** exceptions | The call returns: non-null non-empty list, and `Assets.GetAssetWithGuid(guid)` finds an asset whose `unityObjectAddressable != null`. Failure: null (image/audio/pmeta write), **empty list** (OBJ failures), or an exception (no importer, OBJ pmeta write failure, prefab parse) |
| Image decode | `PummelImageImporter.MustRunOnMainThread()` false; on the main thread: `LoadImage` inline. Off the main thread: a coroutine on the main thread plus a blocking wait (`DoAsyncTextureRead`) | same |
| Audio decode | `UnityWebRequestMultimedia` + **busy-wait spin** `while (!isDone)` on the calling thread (`PummelAudioImporter.cs:58-62`): blocks the game until done; a stuck request would hang the game [U] | same |
| OBJ | native `LoadObjFile` inline; the native handle is never freed (small leak) | same |
| Prefab import | Whole load inside `RunOnMainThread` | same |
| Thumbnails | Next `EditorSystem.Update` after the import (`OnPostImport`), skipped in play mode | `PummelMod.m_newAssets` (private) becomes empty; the asset's `atlasIndex`/preview |
| `RemoveAsset`, `CreateNewMaterial`, `CreateNewPrefab`, `ReimportAsset` | synchronous, main thread (they touch Unity objects) | return |
| `Destroy` of the Unity objects | deferred to end of frame | - |

A plugin tool must run all of these via its main-thread helper, and the audio spin makes a size/length cap advisable.

## 12. Entry points and members for the plugin

### 12.1 Per operation

| Operation | Method signature | What it does | Writes / deletes | UI-coupled? | Evidence |
|---|---|---|---|---|---|
| Import (recommended) | `List<ModAssetExternal> PummelMod.ImportFile(FileInfo file, bool forceNewGuid = false)` | importer dispatch, `.pmeta`, decode, register | `.pmeta` (new/overwritten if corrupt) | no (browser event only) | `PummelMod.cs:1056` |
| Import with copy | `List<ModAssetExternal> PummelMod.AddAsset(string filepath, string folder, bool copy = true)` | copy + import + cleanup + popup | copy, `.pmeta`; **deletes source / existing target in hazard cases** | popup on failure | `PummelMod.cs:898` |
| Reimport / Apply | `void EditorSystem.ReimportAsset(ModAssetExternal asset)` | `Save()`, re-import, or `RemoveAsset` if the file is missing; selection serialise + browser focus | `.pmeta` (+`.pmat`/`.pfab`); may delete | focus + selection snapshot | `EditorSystem.cs:908` |
| Rewrite sidecar only | `bool ModAssetExternal.SaveMetaFile()`; `virtual void ModAssetExternal.Save()` | write `.pmeta` (and `.pmat`/`.pfab`) | yes | no | `ModAssetExternal.cs:48-66` |
| Remove | `void PummelMod.RemoveAsset(ModAsset pa)` | destroy, delete file + `.pmeta`, unlist | **deletes** | no (but selection/open-prefab checks are the caller's) | `PummelMod.cs:827` |
| Remove (UI) | `EditorSystem.DeleteAsset(ModAssetExternal)` | confirmation popup then the above | same | popup, selection | `EditorSystem.cs:1955` |
| Duplicate | `List<ModAssetExternal> PummelMod.AddDuplicatedAsset(string filepath)` | copy file + sidecar with `(n)`, import with new guid | 2 files | no | `PummelMod.cs:873` |
| Create material | `PummelMaterial EditorSystem.CreateNewMaterial()` | see 7.1 | `.pmat` + `.pmeta` | switches browser folder; needs `defaultShaderGuid` | `EditorSystem.cs:2425` |
| Material edit | `PummelMaterial.UnityMaterial` (property), `SetShader(Shader)`, `Load()`, `UpdateKeywords()`, `Save()` | direct Unity `Material` access | `Save()` writes | no | `PummelMaterial.cs` |
| Create prefab | `PummelPrefab EditorSystem.CreateNewPrefab(ModTransform prefabTransform = null)` | see 8.1 | `.pfab` + `.pmeta` (truncating) | switches browser folder | `EditorSystem.cs:2411` |
| Name helper | `string EditorSystem.GetUniqueAssetName<T>(string prefix = null)` | candidate names (asset names only) | none | no | `EditorSystem.cs:2598` |
| Enter prefab | `EditorSystem.CurrentPrefab` setter (or `PummelPrefab.Open()`) | see 8.3; saves the previous prefab | `.pfab`+`.pmeta` of the prefab left | camera/selection | `EditorSystem.cs:437` |
| Refresh instances | `void ModPrefabInstance.ForceUpdate(PummelPrefab)` | re-instantiate child copy | none | no | `ModPrefabInstance.cs:123` |
| Serialise hierarchy (dry run) | `static void ModHierarchySerializer.SaveTransform(BinaryWriter w, Transform t, bool ignoreRoot = false, bool ignoreChildren = false)` | binary hierarchy | none if given a `MemoryStream` | no | `ModHierarchySerializer.cs:79` |
| Selection hygiene | `EditorSelectionManager.DeselectSelectedAsset(bool changingMod = false)`, `GetSelectedAsset()`, `IsSelectedAssetDirty()`, `SerializeSelectedObject()` | see 5, 6.4 | none | selection state | `EditorSelectionManager.cs:326-395` |

### 12.2 Members to add to `RequiredMembers`

Types/members (public unless noted): `PummelMod.ImportFile(FileInfo, Boolean)`, `PummelMod.RemoveAsset(ModAsset)`, `PummelMod.FullAssetsFolder/FullTexturesFolder/FullMaterialsFolder/FullPrefabsFolder` (properties), `PummelMod.Assets`, `PummelMod.GetPreviewImageGuid()`, `PummelMod.GetModPath()`; `ModAssetList.assets`, `GetAssetWithGuid`, `GetAssetWithObject`, `GetAllAssetsOfType(Type)`; `ModAsset.guid/name/assetLocation/assetFolder/assetTypeString/ExternalAsset/InternalAsset/unityObject/unityObjectAddressable/AssetType`; `ModAssetExternal.filePath/fileName/Save/SaveMetaFile/Serialize/HideAndDontSave`; `GameManager.MetaFileExtension`; `AssetLocationGuid.External/Internal/Runtime`; `PummelTexture` private fields `m_compression`, `m_filter`, `m_wrapMode` and getters; `PummelTextureCompression` values Normal/HighQuality/None; `PummelMesh.RecalculateNormals`; `PummelProp.MeshAsset/PropInstance`; `PummelAudioClip.UnityAudioClip`; `PummelMaterial.UnityMaterial/SetShader/Load/Save/UpdateKeywords`, private `m_shaderName`, `m_shaderData`; `PummelPrefab.PrefabObject/isEnabled/Save/Open`; `EditorSystem.ReimportAsset(ModAssetExternal)`, `CreateNewMaterial()`, `CreateNewPrefab(ModTransform)`, `GetUniqueAssetName<T>(String)`, `defaultShaderGuid`, `CurrentPrefab` setter, `GetSelectionManager()`, `AssetFilePrefixes`; `EditorSelectionManager.DeselectSelectedAsset(Boolean)/GetSelectedAsset/IsSelectedAssetDirty/SerializeSelectedObject`; `ModPrefabInstance.ForceUpdate(PummelPrefab)/PrefabReference`; `ModHierarchySerializer.SaveTransform(BinaryWriter, Transform, Boolean, Boolean)`; `EditorPopupManager.CurrentPopup`, `CloseTopPopup()` (to close the "Import Failed" popup if `AddAsset` were used); `MaterialRenderingMode` values; shader property names above (`_Mode, _Cutoff, _MainTex, _Color, _MetallicGlossMap, _Metallic, _Glossiness, _BumpMap, _BumpScale, _ParallaxMap, _Parallax, _OcclusionMap, _EmissionColor, _EmissionMap, _TintColor, _InvFade, _Glow, _EdgeColor, _InsideColor, _IconColor, _IconScale`); shader names `Standard`, `Standard Decal Receiver`, `Custom/ModBoardSpaceShader`, `Particles/Additive Intensify Mod`, `Legacy Shaders/Particles/Alpha Blended Mod`. Private members the plugin would read: `PummelMod.m_newAssets` (completion probe, optional).

## 13. Hazards and how to avoid them

| # | Hazard | Trigger | Consequence | Avoidance |
|---|---|---|---|---|
| H1 | 100-duplicates delete | 100 existing `(n)` names in the target folder, then `AddAsset(copy:true)` | Existing `(99)` file and its `.pmeta` deleted | Plugin picks the name, bounded loop that errors out; require file and `.pmeta` both absent |
| H2 | Source file deleted by the failure handler | Exception in `AddAsset` before the target name is assigned (bad path chars, long path, folder cannot be created, null folder) with `copy:true` | The **source** file and `<source>.pmeta` are deleted (user's original, maybe in another mod) | Never call `AddAsset(copy:true)`; own copy with pre-validated paths; create the target folder first |
| H3 | Orphan `.pmeta` adopted or deleted | A `<target>.pmeta` exists without its file | Old guid and settings reused; deleted if import fails | Refuse a target whose `.pmeta` exists |
| H4 | Unsupported extension | `.bmp` (menu offers it), `.txt`, none; or `.pgf/.pmat/.pfab` | NRE after the copy (junk file), or foreign binary interpreted as game data | Allow-list `.png .jpg .jpeg .wav .mp3 .ogg .obj` |
| H5 | Silent OBJ failure | Bad OBJ: importer returns an empty list | Copy and `.pmeta` stay, nothing registered, retried at every load | Treat empty list as failure; delete only the files the plugin created in this call |
| H6 | `.pfab` truncated or lost on reimport | Exception during `PummelPrefab.Save` (in-place write); `ReimportAsset(prefab)` on a bad file | Mod will not load (`ImportFilesException`); in-memory prefab destroyed before parse | Dry-run serialise to memory, atomic replace, verify; do not reimport prefabs unverified; run a prefab dry run before every `save_mod` when prefabs exist |
| H7 | Corrupt `.pmeta` replaced silently | Truncated/invalid `.pmeta` at load or reimport | New guid, settings lost (material data too), references dangle | Never edit sidecars; validate JSON before any rewrite; report guid changes |
| H8 | Texture/audio reimport breaks object identity | `ReimportAsset` on texture/audio | Materials/components holding the old object serialise null [I, U] | Re-assign textures/clips after reimport; verify with `get_object`; or avoid reimport (re-import settings only via new asset) |
| H9 | Removal of referenced asset | `RemoveAsset` with live references | References become null at next save (silent), pink/silent in memory; undo cannot restore | Scan references first (6.6) and refuse unless explicitly forced or references listed; require save-then-reload check |
| H10 | Removal of the open prefab, selected asset or preview image | `RemoveAsset` on them | Implicit prefab save / destroyed object selected (inspector exceptions) / dangling Workshop-item preview guid | Refuse open prefab; deselect with `changingMod: true`; refuse `GetPreviewImageGuid()` asset |
| H11 | External is not "under Assets" | `AddAsset(copy:false)` on any path registers it as external | `RemoveAsset` deletes a file outside the mod | Removal guard: full path strictly under `FullAssetsFolder`, class allow-list, `ExternalAsset` |
| H12 | Case-insensitive name collision on create | New prefab/material name equal to an existing file differing only in case, or an unloaded orphan file | `FileMode.Create` overwrites the existing `.pfab`/`.pmat` | Check the disk case-insensitively; pass only sanitised names |
| H13 | Unsafe prefab name | Hierarchy object name with invalid file chars | Exception after in-memory registration (asset without file) | Validate `Path.GetInvalidFileNameChars()`, length, trailing dot/space; rename the object first (existing `set_object`) |
| H14 | Modal popups | Failed `AddAsset` (popup), Unapplied Import Settings popup (dirty selected asset being deselected) | All edit tools return INVALID_STATE until a human closes it | Use `ImportFile`; never leave a dirty asset selected; close or detect via `EditorPopupManager.CurrentPopup` |
| H15 | Audio spin-wait | A request that never completes | Game freezes | Size/duration caps; lower-case extension; test with a small file first |
| H16 | Material keywords not updated | Setting properties without the inspector | Wrong blend/keywords, normal map ignored | Call `UpdateKeywords()` and set texture keywords per 7.3 |
| H17 | Texture cache directory | Any PNG/JPG import | Creates `PummelModCache\<id>\` in the game's data folder (outside the mod) | Accept and document (directory only); do not touch its contents |
| H18 | Whole-save rewrite | `save_mod` after import | Every `.pmeta`/`.pfab`/`.pmat` rewritten in place; one failing prefab aborts later writes | See H6; diff `.pmeta` guids before/after in tests |

## 14. Open questions / to verify in the game

1. Does `ReimportAsset` on a texture/audio break references held by materials and components (H8)? Test: assign texture to a material, change a filter setting, Apply, save, reload, inspect.
2. What does `LoadObjFile` do with zero bytes, with non-ASCII text, with huge files (crash vs false)? Is the native call safe to run on a fuzzed file (it is native code)? Cap and sanitise on the plugin side until tested.
3. Does Unity's audio loader accept `AudioType.UNKNOWN` for `.WAV`/`.MP3` upper-case copies? Does the spin-wait ever deadlock for a missing/locked file? Test with a deleted-after-copy file.
4. JsonUtility behaviour for a truncated `.pmeta` (exception vs default object) and for an empty file; exact JSON shape of `m_shaderData` and of the `guid` struct.
5. Value of `EditorSystem.defaultShaderGuid` (which shader a new material gets) and the complete built-in shader list (`list_assets type Shader includeBuiltIn`); shader property ranges for `_Metallic/_Glossiness/_Parallax` as shipped.
6. Does `Directory.CreateDirectory(Textures)` or `File.Copy` behave differently for long (>260) or UNC paths in this Mono runtime? Which exception type does `new FileInfo` throw for invalid characters in Mono on Windows (affects H2 and H13)?
7. After `CreateNewPrefab(sourceObject)`: are actions referencing objects inside the source subtree left pointing at scene objects? Do clone guids differ from the source's? What does Save write for the active state?
8. Does anything re-link materials when a texture asset object is replaced (H8) or when a removed asset is re-imported with the same guid (e.g. restore from a backup)?
9. Is `PummelMesh` (nested in the prop `.pmeta`) ever registered in `mod.Assets` (the code never adds it) - what does the "Meshes" tag and the wiki's "Mesh Asset" refer to in the browser?
10. Do `get_object`/`set_property` on `customMaterials` with a material asset created by the plugin work before the asset is saved (needs only the registered `Material`)? And is a thumbnail ever required for the asset to be usable?
11. Whether `EditorSystem.Update`'s `OnPostImport` thumbnail pass can throw for a failing preview renderer (it would leave `m_newAssets` populated and retry every frame).
12. Whether `RemoveAsset` of a texture used as `Material` main texture is followed by a visible error at save, and what the Mod Validator reports for dangling guids.

## 15. Corrections and findings while building the tools (plugin 0.8.0)

Checked against `decompiled/Assembly-CSharp/` when the asset tools were implemented. Where this section and the sections above differ, this section wins.

1. **H8 is confirmed, and `.pmeta` is the source of the new settings.** `EditorSystem.ReimportAsset` (`EditorSystem.cs:908`) calls `asset.Save()` and then `PummelMod.ImportFile` on the same file. The importer builds a *fresh* asset from the just-written `.pmeta`, decodes into a **new** `Texture2D` (`PummelImageImporter.LoadTextureFromFile`, `:240`), and `ModAssetList.AddAsset` (`:181`) merges by guid: name, `unityObjectAddressable` (the new texture), path and atlas data are copied onto the registered asset; the old texture is not destroyed. Everything that held the old object by identity (materials' texture slots, component properties, action parameters, `MinigameDefinition.screenshotTexture`) no longer maps back to the asset (`ModAssetList.GetAssetWithObject` compares `unityObject`) and is saved as none. The tool therefore applies `filterMode`/`wrapMode` in place (no re-import), re-points materials' texture slots itself and refuses a compression change while other things use the texture.
2. **Props keep identity.** `PummelOBJImporter.Load` reuses the registered `Prop` (`GetModAsset(pummelProp)`, `:100,202`) and only replaces `prop.mesh`; the nested `PummelMesh` guid is never registered (open question 9), so a **new `Mesh`** is made on every import. Objects already in the scene keep the old mesh object until reload (not verified in the game).
3. **`UpdateKeywords` uses a stale shader name.** `PummelMaterial.UpdateKeywords` (`PummelMaterial.cs:192`) switches on `m_shaderName`, which is only refreshed by `Serialize()`. The tool calls `StandardShaderInspector.UpdateKeywords(pmat)` / `DefaultShaderInspector.UpdateKeywords(pmat)` directly, chosen by the live shader name, and saves afterwards.
4. **The inspector enforces more than 7.3 listed.** `_Cutoff` only while `_Mode` is Cutout; `_Parallax` only with a height map; `_EmissionColor`/`_EmissionMap` only with Emission on; the Standard inspector also sets `_BumpScale` to 1 with a normal map. `Standard Decal Receiver` uses the same inspector as `Standard`. The shader-range properties (`_Metallic`, `_Glossiness`, `_Parallax`, `_IconScale`, `_InvFade`, `_Glow`) are read from the shader at run time (`GetPropertyRangeLimits`); `_IconScale`, `_InvFade` and `_Glow` have no documented fallback, so they are refused when the range cannot be read.
5. **`CreateNewMaterial` can overwrite.** It names the file from *asset names* (`GetUniqueAssetName<Material>`) and writes `.pmat` with `FileMode.Create` (`PummelMaterial.Save`): an orphan `Materials\Material_N.pmat` on disk without a loaded material is overwritten. The tool asks the editor for the name first and refuses when the file or its `.pmeta` exists. A custom name is applied with `ModAssetExternal.Rename`, after checking both target names are free (its own check only looks at the asset file).
6. **Removal paths.** `PummelMod.RemoveAsset` deletes `filePath` and `filePath + ".pmeta"` (`DeleteFile`, `PummelMod.cs:841`); for a prop `PummelProp.OnRemove` removes the nested mesh asset, whose path is the same file (`PummelProp.Init`). The tool refuses when the nested path differs. A selected, non-dirty asset is deselected with the normal overload first (no popup); dirty selections are already refused by the edit-ready check.
7. **Preview image.** `GetPreviewImageGuid()` for minigames is `m_minigameDefinition.screenshotTextureGuid` (not the Workshop item). The tool treats it as a reference and never removes that texture, even with `force`.
8. **Not wrong but narrowed:** the doc 07 table suggested `ImportFile` registers the asset "visibly" at once; the importer's `OnAddAsset` placement and the thumbnail pass (`OnPostImport`) run on the next frames, so a `list_assets` right after the call already shows the asset, the Asset Browser tile one frame later.
9. **Not read statically:** what `ModImport.LoadObjFile` does with malformed text, non-ASCII paths in the audio loader, `AudioType` handling of odd-case extensions (the copy is written lower-case, so it does not matter), and the value of `defaultShaderGuid` are still only checkable in the game.

## 16. Corrections and findings while building the prefab tools (plugin 0.8.0)

Checked against `decompiled/Assembly-CSharp/` when `create_prefab`, `open_prefab`, `close_prefab` and the `.pfab` write protection were built. Where this section and the sections above differ, this section wins.

1. **A dry run is possible and exact.** `ModHierarchySerializer.SaveTransform(BinaryWriter, Transform, bool, bool)` is a public static method that writes only to the given writer; `PummelPrefab.Save` calls it with `File.Open(filePath, FileMode.Create)` and nothing else (`PummelPrefab.cs`: `Save`). The plugin runs the same call into a `MemoryStream` (the editor's undo snapshot does this every frame), so the in-memory bytes are exactly what the real save writes (apart from the root object's active flag after `CreateNewPrefab` deactivates the prefab). The 8-byte header is `BinaryWriter.Write("PMH")` (`03 50 4D 48`) + ushort 1 + ushort 0. There is still no read-only parse (`LoadTransform` instantiates objects), so completeness is proven by length equality with the dry run, not by parsing. Doc 07 8.7's "write through a temp file and replace" is not possible: the file is written by the game; the plugin restores instead.
2. **`save_mod` and the scene file.** `PummelMod.Save` calls `ModAssetExternal.Save` for every non-`HideAndDontSave` asset in list order, with no try/catch: the first exception aborts the loop and leaves the throwing prefab truncated (later assets are not written). `ModDataReaderWriter.SaveScene` serialises into a `MemoryStream` first, so a serializer exception cannot truncate the scene; only a failing final file write could (unchanged).
3. **Source objects are copied, not replaced.** `new PummelPrefab(file, source)` does `Object.Instantiate(source.gameObject)` (the clone is created at the scene root, then `ModTransform.SetParent(m_prefabsRoot)`), resets the root's position/rotation/scale and never touches the source (`PummelPrefab.cs` constructor). Nothing is replaced by an instance.
4. **Guids.** `ModBehaviour.m_guid` is a protected, non-serialised field assigned in the constructor, so every component in the clone gets a fresh guid [I; confirm in the game]. Open question 7 is answered for the references: `PummelPrefabReferenceUpdater` runs only for instances and `forceNewGuid` imports, so action targets (`ModAssetReference<Component>`) in the clone keep the **source's** guids and keep pointing at scene objects; instances remap only references that point into the prefab. The plugin therefore refuses any subtree holding object references (found through the value converter's `objectGuid`), and warns when parts of a component could not be read.
5. **8.1 names.** `GetUniqueAssetName<PummelPrefab>(prefix)` returns the prefix itself first (`i == 0`), then `prefix_1`.., and for no prefix `Prefab_0`..; it compares only prefab asset names, case-sensitively. The file name is that string + `.pfab` with no sanitising. The plugin checks the editor's name (characters, file on disk), chooses a free final name itself and applies it with `ModAssetExternal.Rename` when it differs.
6. **8.3 is confirmed and sharpened.** The `CurrentPrefab` setter saves the prefab it leaves (`isEnabled`, `Save()`, deactivate), but when the old prefab is null it only stores the camera, so `open_prefab` from the scene writes nothing. Opening another prefab while one is open saves the first without `ForceUpdate`: the plugin refuses that. The "Return to Hierarchy" handler (`HierarchyWidget.cs:310-328`) is deselect, `isEnabled`, `ForceUpdate` on each scene `ModPrefabInstance` whose `PrefabReference` is the prefab, deselect, `CurrentPrefab = null`, `ReimportAsset(prefab)`. `close_prefab` does all but the last; `ReimportAsset` saves a second time, destroys the in-memory prefab (`PummelPrefabImporter.LoadMetaData`, `DestroyImmediate`) and parses the file, which only adds the size tag recalculation. Not calling it means the "Small/Large" size tag of the asset is not recomputed until the mod is reopened [I].
7. **8.4 verified per tool.** `Deleteable` refuses only the `ModPrefab` root; `Duplicate` of the root clones the whole prefab into itself (`ModPrefab.OnPostDuplicate` removes the marker; the UI allows it); `EditorSystem.GetUndoManager()` creates one `EditorUndo` per prefab in `m_undoManagerList` (kept after leaving), so `undo` in a prefab context is that prefab's own stack; instances of the prefab inside a prefab are destroyed on duplicate (`Paste`) and `NewPrefabInstance` is disabled in the menu.
8. **Removal of a prefab.** `PummelPrefab.OnDestroy` (called by `PummelMod.RemoveAsset`) sets `PrefabReference = null` on every scene instance (`UpdatePrefab` then destroys their child copy) and destroys the prefab object; spawner, item and action guids are left dangling (the reference scan lists them first). Instances inside other prefabs are not touched (the editor forbids them). The prefab's entry in `m_undoManagerList` is not removed by the game.
9. **Not read statically / game only:** awake side effects of the instantiated copy while it is still at the scene root, the exact value of the root's active flag after creation, whether `ForceUpdate` of very many instances is slow, and the thumbnail pass for a new prefab.

## 17. Phase 4 review fixes (plugin 0.8.0, unreleased)

A read-only review of the Phase 4 work found C1, C2, H1, M1, M2 and L1-L11. Checked again against `decompiled/Assembly-CSharp/` while fixing; where this section and the sections above differ, this section wins.

1. **Links (C1).** `PummelMod.ReadAssetFileInfos` uses `GetFiles("*.*", AllDirectories)`, which follows junctions, so a file behind a junction below `Assets` registers with a path that is string-wise inside the sandbox; `PummelMod.DeleteFile`, `ModAssetExternal.SaveMetaFile` (`File.WriteAllBytes`), `PummelPrefab.Save`/`PummelMaterial.Save` (`File.Open(..., FileMode.Create)`), `ModDataReaderWriter.SaveObjectToJson`/`WriteByteDataToFile` and `SavePreviewScreenshot` all write or delete through a junction, a symlink or a hard link. Fix: `Core/SandboxPath.cs`, one central check before every plugin file operation and every game call that writes or deletes for the plugin (table in docs/plugin-api.md, "Links inside a sandbox mod"): strictly below the mod root, no reparse point on any folder from the root to the parent (or the root itself), the file not a symlink and with exactly one hard link (`GetFileInformationByHandle`; unknown = refused). `PummelMod.Save`'s targets (re-derived): Data `Meta.json`, `WorkshopItem.json`, `Lighting.json`, `PostProcessing.data`, `MainScene.scene`, the mod-type file (`MinigameDefinitionData.json` / `MapDetails.json` / `ItemDetails.json`) and `ModSettings.json`, then per non-`HideAndDontSave` external asset its `.pmeta` and, for `PummelMaterial`, `PummelPrefab` and `PummelVisualScript`, the file itself; `SaveCurrentMod` adds `Data/Preview.jpg`. `PummelProp.OnRemove` removes the nested `PummelMesh` too (its own `filePath` + `.pmeta`). Loading a mod writes a `.pmeta` for every asset file without one (`TryCreateOrLoadMetaAsset`), so `open_test_mod` refuses when a file behind a link lacks one. The drift test checks these facts in the real IL.
2. **Playtest save (C2).** `SetEditorState(EnteringPlayMode)` calls `PummelMod.Save()` synchronously before `AddModInBackground`; the same dry run, safety copy, verification and restore as `save_mod` now wrap it (`PlaytestFlow.SaveProtection`).
3. **References (H1).** Action lists also live in `SimpleModPlayerSettings` (six Event Triggers plus the hidden `hitTrigger`), which are not scene objects; `ItemDetails` holds `prefabGuid`, `recievePrefabGuid`, `heldPrefabGuid` and `iconTexture`; `ModWorkshopItem` holds `previewImageGuid` and `previewFiles[].guid`. All are scanned now (`ReferenceCollector`), truncated arrays mark the scan incomplete (L1), Workshop references block removal without `force` (L2), and a settings scan that throws makes the asset unremovable even with `force` (L3).
4. **Decoding (M1).** `TryCreateOrLoadMetaAsset` writes the `.pmeta` before the importer decodes (`PummelAudioImporter.GetAudioClip` busy-waits on the main thread). The copy is now staged as `<name>.mcp-import` (no importer for that extension, so `RefreshImportedFiles` skips it), every content check runs on the copy (also OBJ face lines and the audio duration from the WAV/OGG/MP3 header, `[Assets] MaxAudioSeconds` / `MaxCompressedAudioMB`), then it is renamed and registered; a journal (`McpRecovery/import-pending.json`) lets the next start rename a copy whose decode crashed the game to `.mcp-import-failed`.
5. **Smaller ones.** M2: an incomplete subtree scan refuses `create_prefab`. L4: `FullPrefabsFolder` must be `<mod>\Assets\Prefabs`. L5: the restore swap falls back to `File.Copy(overwrite)` when `File.Replace` is missing; a failed restore keeps the safety copy as `McpRecovery/<file>.<time>.mcp-snapshot`. L6: a screenshot is assigned only to the mod that was open when the call started. L7: commit budgets sized for the work after the commit (`Core/CommitBudgets.cs`). L8: `ModAssetExternal.Rename` can move the file but not the `.pmeta` and raises a popup; `RenameGuard` decides success, undoes a half-done move only when provably safe and never reports success otherwise. L11: the rollback deletes the `.pmeta` only when this call provably created it.
6. **Still game-only:** the Win32 probe under the game's Mono runtime (it fails closed: an unreadable link count refuses), whether `File.Replace` exists there, and the decode times behind the budgets.
