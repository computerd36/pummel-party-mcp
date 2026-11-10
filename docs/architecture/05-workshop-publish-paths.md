# 05 - Workshop publish / upload / delete paths (recon)

Purpose: enumerate every code path in Pummel Party's Assembly-CSharp that can publish, update, delete, subscribe, vote or otherwise write to the Steam Workshop / Steam Cloud, so PummelBridge can block them (hard rule 5).

Method: full-text search of the decompiled tree (about 2600 files) for the Steamworks.NET UGC / RemoteStorage surface, `PublishedFileId_t`, `UGCUpdateHandle_t`, the `*Result_t` types, web/HTTP classes and file-deletion APIs, then manual tracing of every write call site up to its UI entry point. Nothing was run. No game code is reproduced here; only class and member names, signatures and paraphrases.

Legend: **[C]** = confirmed by reading code. **[I]** = inferred (not provable from the decompile alone). All paths below are relative to `decompiled/Assembly-CSharp/`.

---

## 1. Summary (read this first)

* There is exactly **one** game-side publish chain, entirely inside `WorkshopModInstaller` plus the `WorkshopPopup` dialog. [C]
  * `WorkshopPopup.PublishMod(PummelMod)` (WorkshopPopup.cs:455) is the only caller of `WorkshopModInstaller.PublishItem()` (WorkshopModInstaller.cs:1016).
  * `PublishItem` is the only caller of `SteamUGC.CreateItem` (line 1023) and the only starter of the update chain: `BeginUGCItemContentUpdate` (1051), `OnSteamUpdateUGCQuery` (1060), `SubmitItemUpdate(UGCQueryHandle_t)` (1073), which calls `SteamUGC.StartItemUpdate` and `SteamUGC.SubmitItemUpdate`.
  * `WorkshopModInstaller.SubmitItemUpdate(UGCQueryHandle_t)` is `public` but has exactly one caller (`OnSteamUpdateUGCQuery`).
* **No non-click publish trigger exists** (section 4). Publishing needs a button press in `WorkshopPopup`. BUT, once that click happened, the dialog can itself fire two further irreversible Workshop calls without another click: an automatic "reset id to 256 and publish again as a NEW item" retry, and an automatic `SteamUGC.DeleteItem` on several failure codes (section 3.4). The delete can hit a real, existing item in at least one inferable situation. This is the most important finding.
* A second remote-delete path exists: `WorkshopBrowserItem.Delete()` (WorkshopBrowserItem.cs:386) calls `SteamUGC.DeleteItem`. Its UI wiring is in a Unity prefab, not in code, so reachability cannot be proven from the decompile (section 6.2).
* The **Published Mods window cannot delete, change visibility, or publish anything** [C]. It can list own published items, open one in the Workshop overlay, open an already-local copy in the editor, and "Install to mod editor", which downloads the item and copies it into the local mod folder (additive, no overwrite; section 6.1).
* "Save Mod" (menu, Ctrl+S, `AT_SaveMod`, entering play mode, rename, template creation, new-item creation) never touches Steam. It does rewrite `Data/WorkshopItem.json` every time (section 7).
* Steam Cloud / RemoteStorage writes: **none**. Only `SteamRemoteStorage.UGCDownload` and `UGCRead` (preview-image fetch) are used. [C]
* No HTTP upload channel: UnityWebRequest/WebClient/PlayFab hits are I2 Localization translation jobs, the PlayFab Party / XBL chat SDK, and the image/audio importers. None relate to Workshop. [C for the file list; the importer and I2 files were not read line by line, see section 9]

---

## 2. Call-site inventory

R/W column: **W** = remote write / irreversible, **R** = read-only query or local download, **L** = local-only.

### 2.1 Steamworks.SteamUGC write-type calls (complete)

| API | file:line | Enclosing method | R/W |
|---|---|---|---|
| `SteamUGC.CreateItem(appId, k_EWorkshopFileTypeFirst)` | WorkshopModInstaller.cs:1023 | `PublishItem()` | **W** (creates empty remote item) |
| `SteamUGC.StartItemUpdate` | WorkshopModInstaller.cs:1078 | `SubmitItemUpdate(UGCQueryHandle_t)` | **W** (begins update handle) |
| `SetItemTitle`, `SetItemDescription`, `SetItemUpdateLanguage`, `SetItemMetadata` (twice), `SetItemVisibility`, `SetItemTags` | WorkshopModInstaller.cs:1079-1084, 1091 | `SubmitItemUpdate(UGCQueryHandle_t)` | **W** (staging on handle) |
| `SteamUGC.SetItemContent(handle, <mod folder>/)` | WorkshopModInstaller.cs:1093 | same | **W** |
| `SteamUGC.SubmitItemUpdate(handle, changeNote)` | WorkshopModInstaller.cs:1094 | same | **W** (the commit) |
| `AddItemPreviewFile`, `UpdateItemPreviewFile`, `AddItemPreviewVideo`, `UpdateItemPreviewVideo`, `RemoveItemPreview` | WorkshopModInstaller.cs:1145, 1149, 1159, 1163, 1171 | `SetUGCPreviewFiles(UGCQueryHandle_t, PummelMod)` | **W** (RemoveItemPreview deletes remote previews) |
| `SteamUGC.SetItemPreview(handle, path)` | WorkshopModInstaller.cs:1183 | `SetUGCPreviewFiles` | **W** |
| `SteamUGC.DeleteItem` | WorkshopPopup.cs:483 | `DeleteBlankWorkshopItemOnFailedPublish(PummelMod)` | **W, destructive** |
| `SteamUGC.DeleteItem` | WorkshopBrowserItem.cs:388 | `WorkshopBrowserItem.Delete()` | **W, destructive** |
| `SteamUGC.SubscribeItem` | WorkshopModInstaller.cs:96 | `ActiveUGCItem.Subscribe(Action<...>)` | **W** (account subscription) |
| `SteamUGC.UnsubscribeItem` | WorkshopModInstaller.cs:103 | `ActiveUGCItem.UnSubscribe(Action<...>)` | **W** |
| `SteamUGC.SetUserItemVote` | WorkshopBrowserItem.cs:378 | `WorkshopBrowserItem.Upvote(bool)` | **W** |
| `SteamUGC.SetUserItemVote` | WorkshopVotePopup.cs:96 | `WorkshopVotePopup.VoteOnItem(ulong, bool)` | **W** |
| `SteamUGC.AddItemToFavorites` / `RemoveItemFromFavorites` | WorkshopBrowserItem.cs:396 / 400 | `WorkshopBrowserItem.OnFavorite()` | **W** |
| `SteamUGC.StartPlaytimeTracking` | GameBoardController.cs:1262 | board setup; guarded by "no EditorSystem instance and workshop non-internal" | **W** (playtime stat; inactive inside the editor) |
| `SteamUGC.StopPlaytimeTracking` | GameBoardController.cs:2948; MinigameController.cs:394 (`ReleaseMinigame`, same guard) | board end / minigame release | **W** (stat) |
| `SteamUGC.StopPlaytimeTrackingForAllItems` | GameManager.cs:413 | context not read | **W** (stat) |

Other Steamworks.NET UGC mutators (`AddDependency`, `RemoveDependency`, `AddAppDependency`, `AddItemKeyValueTag`, `RemoveItemKeyValueTags`, `SetAllowLegacyUpload`, `SetItemsDisabledLocally`, `AddContentDescriptor`, ...) are **not used** anywhere. [C]

### 2.2 SteamUGC / RemoteStorage read and local-download calls (must stay allowed)

| API | file:line | Enclosing method / purpose |
|---|---|---|
| `CreateQueryUGCDetailsRequest`, `SendQueryUGCRequest`, `ReleaseQueryUGCRequest` | WorkshopPopup.cs:117-123 | `CheckIfModIsPublishedAndPopulated()` (publish dialog constructor) |
| same | ModVisibilityWarningUI.cs:51-57 | warning UI (class not instantiated from code; prefab-hosted) |
| `CreateQueryUGCDetailsRequest` + `SetReturnAdditionalPreviews`, `SetReturnMetadata` | WorkshopModInstaller.cs:1053-1057 | `BeginUGCItemContentUpdate` (read-only, but part of publish chain) |
| `CreateQueryUGCDetailsRequest` | WorkshopModInstaller.cs:605, 657 | background detail / up-to-date checks |
| `CreateQueryUserUGCRequest` (Published) | PublishedModsPopup.cs:167 | `LoadWorkshopModData(uint)` |
| `CreateQueryUserUGCRequest` (Favorited) | WorkshopModInstaller.cs:407 | favourites refresh |
| `CreateQueryUserUGCRequest` / `CreateQueryAllUGCRequest` + setters | WorkshopWindowManager.cs:168-208 | in-game browser query |
| `GetQueryUGCResult`, `GetQueryUGCNumTags`, `GetQueryUGCTagDisplayName`, `GetQueryUGCMetadata`, `GetQueryUGCNumAdditionalPreviews`, `GetQueryUGCAdditionalPreview` | PublishedModsPopup.cs:180-186; WorkshopPopup.cs:129; WorkshopModInstaller.cs:672, 765, 1104, 1107; WorkshopWindowManager.cs:220-231 | result readers |
| `GetNumSubscribedItems`, `GetSubscribedItems`, `GetItemInstallInfo`, `GetItemState`, `GetItemDownloadInfo`, `GetItemUpdateProgress` | ModCacheSystem.cs:154-195; NewModPopup.cs:89-94; WorkshopModInstaller.cs:302, 475, 565, 583, 951, 1208; PublishedModsPopup.cs:210, 279 | loading subscribed mods as templates, installing, progress |
| `SteamUGC.DownloadItem` | PublishedModsPopup.cs:319; WorkshopModInstaller.cs:719 (callers 695, 786, 817, 863, 894) | Steam downloads content for an item; local effect only |
| `SteamUGC.GetUserItemVote` | WorkshopBrowserItem.cs:220; WorkshopVotePopup.cs:115 | read |
| `SteamRemoteStorage.UGCDownload` / `UGCRead` | PublishedModEntry.cs:34, 43; WorkshopBrowserItem.cs:222, 283; WorkshopModInstaller.cs:63, 75 | preview image fetch |

### 2.3 Negative search results

* `SteamRemoteStorage.FileShare`, `FileWrite`, `PublishWorkshopFile`, `UpdatePublishedFile*`, `CommitPublishedFileUpdate`, `DeletePublishedFile`, `SetUserPublishedFileAction`, `SubscribePublishedFile`: no occurrences. [C]
* `UnityWebRequest`, `HttpClient`, `WebClient`, `HttpWebRequest`: only I2.Loc translation job classes, PlayFab.Party / XBL SDK files, `PummelImageImporter`, `PummelAudioImporter` (and the csproj). None in Workshop or editor-save code. [C for the file list]
* Result/callback types tied to writes: `CreateItemResult_t` (WorkshopModInstaller.cs:180, 442), `SubmitItemUpdateResult_t` (182, 443), `DeleteItemResult_t` (WorkshopBrowserItem.cs:116, 214), `SetUserItemVoteResult_t` (WorkshopBrowserItem.cs:118, 218; WorkshopVotePopup.cs:61, 91), `RemoteStorageSubscribe/UnsubscribePublishedFileResult_t`. Each appears only alongside the call sites in 2.1.
* Overlay navigation (`SteamFriends.ActivateGameOverlayToWebPage`: WorkshopPopup:329, PublishedModsPopup:332, LoadModPopup:195, WorkshopBrowserItem:242, WorkshopModInstaller:1193, WelcomeScreenPopup, ModEditorGui:2077, MainMenuWidget:271, SteamForumButton) is navigation, not a write.

---

## 3. The publish / update flow (question 1)

### 3.1 UI entry

1. Editor main menu, File > "Publish Mod to Workshop" (MainMenuWidget.cs:131-135). Enabled only for Minigame and Board mods (`enabled`, line 35). It clears `CurrentPrefab` and calls `EditorPopupManager.AddPopup(new WorkshopPopup())`. [C]
2. `WorkshopPopup` constructor (WorkshopPopup.cs:91-109): sets the title, clears the installer's last submit result, subscribes `ClosePopup` to `WorkshopModInstaller.OnSuccessfulWorkshopPublish`, runs `ValidateModTags`, then `CheckIfModIsPublishedAndPopulated()`. When the mod's `publishedFileId` is not 256 that sends a read-only details query; the callback (line 127) sets `m_modIsPublishedAndPopulatedOnWorkshop` to "remote file size > 0", and if the remote owner is not the logged-in user it sets the local id back to 256 (in memory) and the flag false. It also copies remote title, description and visibility into the in-memory mod and sets `m_updatePreviewFiles = false`. [C]
3. `WorkshopPopup.Init()` (line 146) runs `EditorSystem.ValidateCurrentMod()` once and stores the result in `m_lastValidationResult`. [C]
4. `Draw()` builds the form. The real publish button (line 427) is drawn only if `m_lastValidationResult.IsModValid` (line 318); otherwise a disabled dummy (line 441). It is also disabled while uploading or when `SteamUser.BLoggedOn()` is false (`IsSteamOnline`, line 450). Label: "Publish update to workshop" when `m_modIsPublishedAndPopulatedOnWorkshop`, else "Publish mod to workshop". Click calls `PublishMod(currentMod)` (line 429). [C]

### 3.2 Publish execution

5. `WorkshopPopup.PublishMod(PummelMod)` (line 455) requires mod type Minigame or Board, a live `WorkshopModInstaller.Instance`, and Steam online. Then: clear last result; **`pm.Save()`** (full mod save, including `Data/WorkshopItem.json`); if id != 256, `EditorSystem.AddModToReinstallOnExit(id)`; `SetUpdateAdditionalPreviewFiles(m_updatePreviewFiles)`; **`WorkshopModInstaller.Instance.PublishItem()`**; set `m_isUploading`. It does **not** re-check validity; the validity gate is only the button state. [C]
6. `WorkshopModInstaller.PublishItem()` (line 1016): if `EditorSystem.Instance.CurrentMod` is non-null, read `CurrentMod.workshopItem.publishedFileId`. Equal to `ModWorkshopItem.DefaultWorkshopID` (256): **`SteamUGC.CreateItem`** (line 1023). Otherwise: `BeginUGCItemContentUpdate(new PublishedFileId_t(id))` (line 1027). [C]
7. `OnCreateItem(CreateItemResult_t, bool)` (line 1032): on OK, stores the new id into `CurrentMod.workshopItem.publishedFileId` (if still 256), calls `CurrentMod.DelayedSave()` (persists the id to `WorkshopItem.json`), then `BeginUGCItemContentUpdate(newId)`. On failure it only records the `EResult` in `m_lastSubmitItemResult`. [C]
8. `BeginUGCItemContentUpdate` (line 1051) sends a read-only details query (with additional previews and metadata). `OnSteamUpdateUGCQuery` (line 1060): on OK with at least one result, sets `ItemUpdateInProgress = true` and calls `SubmitItemUpdate(result.m_handle)`; otherwise records the error. [C]
9. `WorkshopModInstaller.SubmitItemUpdate(UGCQueryHandle_t)` (line 1073): `StartItemUpdate(appId, publishedFileId)`; sets title, description, language, visibility, tags; sets metadata (author name, author Steam id, unlocked-for-editing flag as JSON); `SetUGCPreviewFiles`; `SetItemContent(handle, <CurrentMod.GetModPath()> + "/")`; then **`SteamUGC.SubmitItemUpdate(handle, workshopItem.changeNote)`** and `Callback_SubmitItemUpdateResult.Set(...)`. [C]
10. `SetUGCPreviewFiles` (line 1098): if `m_updatePreviewFiles`, diffs remote additional previews against `workshopItem.previewFiles` and adds, updates or **removes remote preview entries** (any remote preview not matched locally is removed). Always calls `SetItemPreview` with the thumbnail asset chosen in the dialog or the mod's `Data/Preview.jpg`. [C]
11. `OnSubmitItemUpdateResult` (line 1186): on OK it **auto-subscribes** the account to the item (line 1191), opens the community page in the Steam overlay, and raises `OnSuccessfulWorkshopPublish` (closes the dialog, clears the change note). Otherwise stores the error. `ItemUpdateInProgress` is reset. [C]

### 3.3 Staging / packaging

There is **no staging folder and no copy step**. The content directory given to Steam is the live mod folder (`CurrentMod.GetModPath()` + "/"), so everything in the folder (including `Data/WorkshopItem.json`, `Meta.json`, any stray files) is uploaded. The only pre-upload steps are `ValidateCurrentMod()` (at dialog open), `ValidateModTags` (in-memory tag normalisation) and `pm.Save()`. [C]

### 3.4 Automatic follow-up actions after the click

All in `WorkshopPopup.Draw()` (lines 355-409); they run every frame while the popup is open and `WorkshopModInstaller.GetLastSubmittedItemResult()` is not "None". [C]

* On `k_EResultFileNotFound` (first time): `ResetPublishFileIdAndRetryPublish(PummelMod)` (line 471) sets the in-memory id to 256 and calls `PublishMod` again. That makes `PublishItem` take the **CreateItem** branch: a second, new Workshop item is created without a new click. One retry (`m_isRetryingPublish`).
* On `Fail`, `InvalidParam`, `AccessDenied`, `LockingFailed`, `LimitExceeded` (and `FileNotFound` after more than one attempt): `DeleteBlankWorkshopItemOnFailedPublish(PummelMod)` (line 479). If `m_modIsPublishedAndPopulatedOnWorkshop` is false it calls **`SteamUGC.DeleteItem(<mod's current publishedFileId>)`** and resets the local id to 256. Intent: clean up the empty shell from a failed first-time `CreateItem`.
* **Hazard [I]:** the guard is only the "published and populated" flag, not "this id was created in this session". The flag starts false and becomes true only after the details query returns with remote size > 0. If a mod with a real, existing id is published (a) before that query returns, (b) when the query failed or returned nothing, or (c) when the remote file size is 0, and the upload then fails with one of the codes above, the game deletes the existing Workshop item. After the first reset the repeated per-frame calls carry id 256. Not executed; derived from reading the code.

---

## 4. Can a publish start without a click in the publish dialog? (question 2)

Searched deliberately. All of these are **negative** (none reaches `PublishItem`, `SubmitItemUpdate`, `CreateItem` or `DeleteItem`):

| Trigger | Finding | Evidence |
|---|---|---|
| Save (menu "Save Mod", Ctrl+S) | Negative. `EditorSystem.SaveCurrentMod(bool delayed)` -> `PummelMod.Save()` / `DelayedSave()`, then last-modified update, preview screenshot, clear dirty flag. No Steam call. | MainMenuWidget.cs:94-101; EditorSystem.cs:808-821, 1676-1679 |
| Autosave | None found in the editor. Only `DelayedSave()` (next-frame save) after new-mod creation, template finishing and `OnCreateItem`. | PummelMod.cs:552, 1181; EditorSystem.cs:812, 889; WorkshopModInstaller.cs:1042 |
| Playtest / enter play mode | Negative. `SetEditorState(EnteringPlayMode)` calls `CurrentMod.Save()` and `AddModInBackground`. `ExitingPlayMode` queues `AddModToReinstallOnExit(id)` for id != 256; the reinstall is an in-memory reload of the Steam-installed copy, not an upload. | EditorSystem.cs:1838-1897, 1556-1570; WorkshopModInstaller.cs:934-943 |
| Close mod / exit editor / Esc | Negative. `ExitModEditor` / `RunExitModEditorLogic` start a thread running `ReInstallUnloadedMods`, then load the MainMenu scene. | EditorSystem.cs:1530-1570, 1662-1671 |
| Rename (menu, Open Mod popup, `AT_RenameMod`) | Negative for Steam. Moves the folder, sets `workshopItem.title`, saves `Meta.json` and `WorkshopItem.json`. Does not change `publishedFileId`. | EditorSystem.cs:1172-1220; PummelMod.cs:254-309 |
| New mod / template creation | Negative for Steam. Copies a template folder; on load-finished (regardless of success) forces id 256; renames if load succeeded. | EditorSystem.cs:823-846 |
| Validate Mod / validators | Negative. `ModValidationPopup`, `ModValidationSystem`, `ModMinigameValidator`, `ModBoardValidator` have no Workshop or Steam reference; only a help link via `Process.Start`. | ModValidationPopup.cs:191; grep |
| Keyboard shortcuts | Negative. `EditorSystem.Update` handles Esc, Ctrl+Z, Ctrl+S, F10 (screenshot), Ctrl +/- (UI zoom), gizmo keys. `new WorkshopPopup()` exists in exactly one place. | EditorSystem.cs:1572-1700; MainMenuWidget.cs:134 |
| Automated tests `AT_*` | Negative. The 9 classes (`AT_AddGameObjects`, `AT_AddRandomProp`, `AT_CloseModEditor`, `AT_NewMod`, `AT_OpenMod`, `AT_OpenModEditor`, `AT_RenameMod`, `AT_SaveMod`, `AT_SelectAsset`) have no Workshop/Steam/`WorkshopPopup` reference. Side note: `AT_OpenMod` and `AT_RenameMod` act on random mods in the mod folder (a local-mod-safety concern, not publish). The menu entry shows only when `AutomatedTestSystem.IsEnabled` (editor or debug build). | AT_*.cs; MainMenuWidget.cs:126; AutomatedTestSystem.cs:21-31 |
| Background callbacks / loops | Negative. `WorkshopModInstaller.Update` and the install thread only query, download, install subscribed items. `OnCreateItem`, `OnSteamUpdateUGCQuery`, `OnSubmitItemUpdateResult` run only as continuations of an already-started publish. | WorkshopModInstaller.cs:307-351, 528-, 1032-1201 |
| Open / load mod | Negative. `LoadModPopup`, `OpenMod`, `LoadModIntoEditor` do not publish. | LoadModPopup.cs; EditorSystem.cs:863-895 |

Caveat on the headline: "starts without a click in a dedicated publish dialog" is false for the first call, but see 3.4 for the automatic CreateItem retry and automatic DeleteItem after that first click.

---

## 5. Target item selection, the 256 constant, duplicate ids (question 3)

* `publishedFileId` is a `ulong` field of `ModWorkshopItem` (ModWorkshopItem.cs:29), serialised in `<mod folder>/Data/WorkshopItem.json` (PummelMod.cs:600 save, 625 load) and loaded into `PummelMod.workshopItem`. [C]
* **256 = `ModWorkshopItem.DefaultWorkshopID`** (ModWorkshopItem.cs:11): sentinel meaning "never published / no Workshop item"; it is the field initialiser, so new mods get it. Literal 256 comparisons: EditorSystem.cs:834, 1853; WorkshopPopup.cs:135, 461; LoadModPopup.cs:187 (enables the "Steam Workshop Page" button); `ReinstallMod` uses `> 256`; vote popup uses `MinigameID > 256`. [C]
* Create vs update (WorkshopModInstaller.PublishItem, lines 1020-1028): id == 256 -> `CreateItem`, then update; id != 256 -> update that exact remote item. No ownership or uniqueness check at publish time other than the soft one in the dialog (owner mismatch resets to 256 in memory, only after the details query has returned). [C]
* Template creation (`EditorSystem.CreateNewMod`, EditorSystem.cs:823-846) copies the template folder including its `WorkshopItem.json` (and so its id), and forces 256 only in the load-finished callback. A folder copied by other means (the bridge, the user, a file manager) keeps the source id. [C]
* **Duplicate-id consequences** (two local folders with the same id):
  * Publishing from either folder targets the same remote item and replaces its content, title, description, tags, visibility, metadata and (if "Update Videos/Images" is on) all previews. Last upload wins. [C]
  * A `PB_TEST_` copy of the real published mod that keeps the real id is one click from overwriting the live item. Any bridge-created or cloned mod must have its id forced to 256 before it is opened in the editor, in addition to blocking the publish chain. [C/I]
  * `PublishedModsPopup.LinkWorkshopAndLocalData` (PublishedModsPopup.cs:120-134) uses first-match lookup, so only one duplicate is shown as installed. [C]
  * In-game, `WorkshopController.RemoveMod(ulong)` (WorkshopController.cs:731) removes every loaded mod with that id; `GetModWithWorkshopID` (768) returns the first match; `PrepareMods` (350) filters play-mode mods by id equality with the current mod. All unpublished mods share 256, so they also collide in-memory. [C]
  * `ModCacheSystem` keys texture caches by id (ModCacheSystem.cs:18); `RemoveActiveItem` deletes that cache folder (WorkshopModInstaller.cs:836). Local cache only. [C]
* Only Minigame and Board mod types can be published (MainMenuWidget.cs:35; WorkshopPopup.cs:457). [C]

---

## 6. Other remote / irreversible Workshop actions (questions 4 and 5)

### 6.1 Published Mods window (`PublishedModsPopup`; File > Published Mods, MainMenuWidget.cs:136-139; also WelcomeScreenPopup.cs:100)

[C] unless noted:
* Lists the logged-in user's published items (read query, line 167), five per page.
* Per entry: if a local mod with the same `publishedFileId` exists, "Open in mod editor" -> `LoadModIntoEditor(path)` (line 395) -> `OpenModWithPopupPrompt` (just opens it). Else "Install to mod editor" -> `SteamUGC.DownloadItem` (line 319), and on `DownloadItemResult_t` `CopyWorkshopModToLocalModFolder` (line 223). Plus "View in Workshop" (overlay).
* It cannot delete a remote item, change visibility, publish, subscribe/unsubscribe or vote. No method in the class reaches a write API from 2.1. The wiki agrees (Published_Mods_Window.md: to replace a local copy with the published one the user must first delete the local copy).
* **Local write on Install:** copies into `<EditorSystem.ModFolder>/<Meta.json Name>` using non-overwriting file copies. If that folder exists, existing files raise `IOException` (a dialog tells the user to empty the folder) but missing files are still added, so an existing folder can be partially merged into. It never deletes or overwrites existing files. The button is hidden when a local mod with the matching id exists. The folder name comes from the downloaded item's metadata name. `EditorSystem.ModFolder` is `<persistentDataPath>/WorkshopMods` (EditorSystem.cs:1140).

### 6.2 In-game Workshop browser (`WorkshopWindowManager`, `WorkshopBrowserItem`; main-menu UGUI, referenced by `UIController.workshopWindowManager`)

* `WorkshopBrowserItem.OnClick()` toggles subscribe/unsubscribe via `WorkshopModInstaller.Subscribe` / `UnSubscribe` (lines 327-338); `Upvote(bool)` votes (356); `OnFavorite()` (392); `Delete()` deletes the remote item (386-390); `OpenModInSteamOverlay()` navigates.
* **Reachability [I]:** none of these methods has a code caller; they are public methods meant to be bound to Unity prefab button events. Whether a delete button exists in the shipped prefab is not determinable from C#. This browser lives in the main-menu scene, not the editor scene, but a bridge that leaves the editor or calls game methods reflectively could reach it.
* `UnSubscribe` also calls `WorkshopController.RemoveMod(id)` and `RemoveActiveItem` (local texture cache delete).

### 6.3 Vote popup (`WorkshopVotePopup`)

* After a Workshop (non-internal) map/minigame ends or on return to the main menu, if the user has not voted, a 10-second popup shows. `HandleButtonPress(MainMenuButtonEventType)` (line 221) calls `SetUserItemVote` (line 96) only on a button event. [C] Whether a playtest of a local mod with a real id can trigger the popup (via `OnEnterMainMenu`) was not verified. [I]

### 6.4 Steam Cloud / RemoteStorage

No writes. `SteamRemoteStorage` is used only for `UGCDownload` / `UGCRead` of preview images. [C]

### 6.5 Local deletions / moves (in scope as "deletes or overwrites a local mod folder")

| API | file:line | What |
|---|---|---|
| `Directory.Delete(<ModFolder>/<name>, recursive)` | LoadModPopup.cs:226 (`DeleteModResult`, after Yes/No popup) | Open Mod popup trash button: deletes a whole local mod |
| `Directory.Delete(<ModFolder>/<folder>, recursive)` | MainMenuWidget.cs:297 (File > Delete Mod, after Yes/No popup) | same |
| `Directory.Move` | EditorSystem.cs:1197 (`RenameMod`) | rename; refuses when target exists |
| `File.Move` / `File.Delete` | ModAssetExternal.cs:79-80; PummelMod.cs:847, 858, 945, 949 | asset rename / removal inside a mod |
| `Directory.Delete` of cache folders | ModCacheSystem.cs:107, 133 (`DeleteAllModCache`, `DeleteCacheFolderForMod`, `SweepAndDeleteUnusedCacheFiles` called from installer `Awake`, line 315) | texture cache only |

None of these touch Steam. They matter for hard rules 1 and 4; listed for completeness.

---

## 7. WorkshopItem.json writes (question 6)

All writes go through `PummelMod.SaveWorkshopItemDetails()` (PummelMod.cs:594-601, serialises the whole `workshopItem`) or `RenameUnloadedMod` (PummelMod.cs:295). [C]

* `PummelMod.Save()` (line 567) calls `SaveMetaData()` then `SaveWorkshopItemDetails()` first. So **every plain Save Mod** (menu, Ctrl+S), `AT_SaveMod`, entering play mode (EditorSystem.cs:1875), `PublishMod`, and every `DelayedSave()` rewrites the file in full from the in-memory object.
* `RenameMod` (PummelMod.cs:300-309) sets `title` to the new name and saves; `RenameUnloadedMod` (254-298) does the same for a not-loaded folder.
* `FinishTemplateSetup` (PummelMod.cs:547-553): title = mod name, description cleared, `DelayedSave()`.
* `OnCreateItem` (WorkshopModInstaller.cs:1040-1042) writes the new id via `DelayedSave`.
* **Merely opening the publish dialog does not write to disk**, but it mutates the in-memory `workshopItem` at once: `ValidateModTags` rewrites the tag list; the details-query callback overwrites title, description and visibility from the remote item and may reset the id to 256; the form writes edits straight back and applies `SetEditLock`. These reach disk on the next Save (Ctrl+S or the save before play mode). So opening the dialog and then saving can silently replace local title/description/visibility with remote values. [C for the mutation; persistence follows from `Save()` serialising the in-memory object]
* Loading (`LoadWorkshopItemDetails`, PummelMod.cs:623-631), `LoadModPopup.LoadModData` and `PublishedModsPopup.GetLocalModPublishedFileId` only read. (The last dereferences the load result without a null check.)

---

## 8. Blocklist (question 7)

Harmony **prefix** patches returning `false`, with a safe default `__result`. Install before any editor UI is usable and keep them active while the bridge is attached. Block layer (i) AND (ii).

### 8.1 Layer (i): game-level chokepoints and UI entries

| Method | Why | Layer | Evidence | Break read-only features? |
|---|---|---|---|---|
| `WorkshopModInstaller.PublishItem()` | Single entry to `CreateItem` and the update chain; single caller | **chokepoint (narrowest game-level)** | WorkshopModInstaller.cs:1016; WorkshopPopup.cs:466 | No. Also record a failure result so the dialog does not wait forever |
| `WorkshopModInstaller.SubmitItemUpdate(UGCQueryHandle_t)` | Starts the real update handle and commits; public; single caller | chokepoint | WorkshopModInstaller.cs:1073, 1065 | No |
| `WorkshopModInstaller.BeginUGCItemContentUpdate(PublishedFileId_t)` (private) | Entry to the update chain for existing items | chokepoint | line 1051 | No |
| `WorkshopModInstaller.OnCreateItem(CreateItemResult_t, bool)`, `OnSteamUpdateUGCQuery(SteamUGCQueryCompleted_t, bool)`, `SetUGCPreviewFiles(UGCQueryHandle_t, PummelMod)` (private) | Defence in depth for in-flight continuations | chokepoint | lines 1032, 1060, 1098 | No |
| `WorkshopPopup.PublishMod(PummelMod)` (private) | The only button handler; also does `pm.Save()` | UI entry | WorkshopPopup.cs:455, 429 | No |
| `WorkshopPopup.ResetPublishFileIdAndRetryPublish(PummelMod)` (private) | Automatic CreateItem retry | UI entry | WorkshopPopup.cs:471 | No |
| `WorkshopPopup.DeleteBlankWorkshopItemOnFailedPublish(PummelMod)` (private) | Automatic `DeleteItem` that may hit a real item | UI entry | WorkshopPopup.cs:479-486 | No |
| `WorkshopPopup..ctor()` or `EditorPopupManager.AddPopup(...)` dropping `WorkshopPopup` instances | Keeps the dialog from ever opening, which also avoids its in-memory mutations (section 7). The only creation site is the File menu item | UI entry | MainMenuWidget.cs:131-135 | No |
| `WorkshopBrowserItem.Delete()` | Remote delete | UI entry | WorkshopBrowserItem.cs:386 | No |
| `WorkshopBrowserItem.Upvote(bool)`; `WorkshopVotePopup.VoteOnItem(ulong, bool)` (private) | Remote vote | UI entry | WorkshopBrowserItem.cs:356; WorkshopVotePopup.cs:94 | No |
| `WorkshopBrowserItem.OnClick()`, `OnFavorite()`; `WorkshopModInstaller.Subscribe(PublishedFileId_t)`, `UnSubscribe(PublishedFileId_t)`; `ActiveUGCItem.Subscribe(...)`, `UnSubscribe(...)` | Subscription / favourites changes. `Subscribe` also fires automatically after a successful publish | UI entry / wrapper | WorkshopBrowserItem.cs:327, 392; WorkshopModInstaller.cs:93, 100, 742, 747, 1191 | No. Loading already-subscribed mods reads `GetSubscribedItems`; lobby auto-install uses `AddTempActiveItem`, which does not call these |
| Companion safeguard (not a Workshop call): force `publishedFileId = 256` for every bridge-created or cloned mod, in memory and in JSON, before the editor opens it; consider refusing `PummelMod.SaveWorkshopItemDetails()` for non-`PB_TEST_` mods | Prevents the duplicate-id overwrite scenario | n/a | EditorSystem.cs:823-846; PummelMod.cs:594 | n/a |

### 8.2 Layer (ii): Steamworks.NET static methods (fail closed)

Class `Steamworks.SteamUGC` (managed wrapper in `com.rlabrecque.steamworks.net.dll`). Suggested `__result`: `SteamAPICall_t.Invalid` for methods returning it, `UGCUpdateHandle_t.Invalid` for `StartItemUpdate`, `false` for bool returns. [I: exact overloads vary by Steamworks.NET version, so enumerate targets by name with `TargetMethods()`; the effect of `CallResult.Set(SteamAPICall_t.Invalid)` was not verified]

| Method (as used by the game) | Why | Evidence | Safe to block? |
|---|---|---|---|
| `SteamUGC.SubmitItemUpdate(UGCUpdateHandle_t, string)` | Final commit; **narrowest chokepoint overall** | WorkshopModInstaller.cs:1094 | Yes |
| `SteamUGC.StartItemUpdate(AppId_t, PublishedFileId_t)` | Without a handle no update can happen | :1078 | Yes |
| `SteamUGC.CreateItem(AppId_t, EWorkshopFileType)` | Creates a new remote item | :1023 | Yes |
| `SteamUGC.DeleteItem(PublishedFileId_t)` | Remote delete | WorkshopPopup.cs:483; WorkshopBrowserItem.cs:388 | Yes |
| `SteamUGC.SetItemTitle`, `SetItemDescription`, `SetItemUpdateLanguage`, `SetItemMetadata`, `SetItemVisibility`, `SetItemTags`, `SetItemContent`, `SetItemPreview`, `AddItemPreviewFile`, `AddItemPreviewVideo`, `UpdateItemPreviewFile`, `UpdateItemPreviewVideo`, `RemoveItemPreview` | Update-handle staging | :1079-1183 | Yes. Rule: block any `SteamUGC` method whose first parameter is `UGCUpdateHandle_t`; this separates them from the same-named query setters (first parameter `UGCQueryHandle_t`) which must stay allowed |
| `SteamUGC.SubscribeItem`, `UnsubscribeItem` | Subscription changes | WorkshopModInstaller.cs:96, 103 | Yes |
| `SteamUGC.SetUserItemVote` | Vote | WorkshopBrowserItem.cs:378; WorkshopVotePopup.cs:96 | Yes |
| `SteamUGC.AddItemToFavorites`, `RemoveItemFromFavorites` | Favourites | WorkshopBrowserItem.cs:396, 400 | Yes |
| `SteamUGC.StartPlaytimeTracking`, `StopPlaytimeTracking`, `StopPlaytimeTrackingForAllItems` | Item playtime stats (game call sites are skipped while the editor exists) | GameBoardController.cs:1262, 2948; MinigameController.cs:394; GameManager.cs:413 | Yes |
| Unused mutators: `AddDependency`, `RemoveDependency`, `AddAppDependency`, `RemoveAppDependency`, `AddItemKeyValueTag`, `RemoveItemKeyValueTags`, `SetAllowLegacyUpload`, `AddContentDescriptor`, `RemoveContentDescriptor`, `SetItemsDisabledLocally`, `SuspendDownloads` and any other mutator in the shipped version | Unknown-path fail-closed | none (negative search) | Yes |

Also block every public `Steamworks.SteamRemoteStorage` method except `UGCDownload` and `UGCRead` (and read-only getters). The game uses nothing else; this covers `PublishWorkshopFile`, `UpdatePublishedFile*`, `FileWrite`, `FileShare`, `DeletePublishedFile`, `SetUserPublishedFileAction`, `SubscribePublishedFile`, `UnsubscribePublishedFile`.

**Must stay allowed** (read-only features the editor and mod loading rely on): `CreateQueryUGCDetailsRequest`, `CreateQueryUserUGCRequest`, `CreateQueryAllUGCRequest`, `SendQueryUGCRequest`, `ReleaseQueryUGCRequest`, all `GetQueryUGC*`, query-handle setters (`SetReturnMetadata`, `SetReturnAdditionalPreviews`, `SetReturnOnlyIDs`, `SetAllowCachedResponse`, `SetSearchText`, `SetRankedByTrendDays`, `AddRequiredTagGroup`), `GetNumSubscribedItems`, `GetSubscribedItems`, `GetItemInstallInfo`, `GetItemState`, `GetItemDownloadInfo`, `GetItemUpdateProgress`, `GetUserItemVote`, `DownloadItem` (local download; used by the installer for subscribed/lobby mods and by Published Mods "Install"), `SteamRemoteStorage.UGCDownload` / `UGCRead`.

Caveats [I]:
* Mono may inline tiny managed wrappers into already-JITed callers; patch early and keep layer (i) as well.
* Blocking `CreateItem` / `SubmitItemUpdate` alone leaves the dialog waiting; layer (i) (never open the dialog) avoids that.

---

## 9. Open questions / unverified, residual risk (question 8)

1. **Prefab / scene-wired UI is invisible to this analysis.** Unity button events (`WorkshopBrowserItem.Delete`, `OnClick`, `Upvote`, `OnFavorite`, `WorkshopVotePopup.HandleButtonPress`) are bound in assets, not C#. Whether a delete button exists in the shipped main-menu prefab is unverified. Mitigated by blocking the methods themselves.
2. **Delete-on-failed-publish hazard (3.4)** is inferred, not executed. Exact timing (query race, query failure, zero-size remote item) untested. Treat `DeleteBlankWorkshopItemOnFailedPublish` as live.
3. **`ModVisibilityWarningUI`** (read-only query) has no instantiating code; probably prefab-hosted; not traced.
4. **Steamworks.NET internals** are not decompiled. Overloads and `__result` defaults must be confirmed against the DLL in the game folder when writing the Harmony patches.
5. **`GameManager.cs:413`** (`StopPlaytimeTrackingForAllItems`) call context was not read.
6. **Importers / I2 / PlayFab** matched the HTTP grep; `PummelImageImporter`, `PummelAudioImporter` and I2.Loc were identified by file list only, not read. No Workshop code references them.
7. **Other assemblies**: only Assembly-CSharp was searched. Workshop writes in other game assemblies were not examined.
8. **Mod runtime / visual scripting** (`ModSystem.Logic`, `PummelVisualScript`): grep found no Steam call outside the files listed, so a mod script cannot reach Steam through visible code; the script node library was not audited for indirect paths.
9. **Vote popup after playtest** (6.3) not verified.
10. **Install-into-existing-folder behaviour** (6.1) reasoned from code, not tested.
