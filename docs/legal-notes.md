# Legal notes (Phase 3: "Read Rebuilt Games' EULA")

Checked 2026-11-12. These are notes by the project, not legal advice.

## What applies

- **No separate Rebuilt Games EULA was found.** The Steam store page for Pummel Party (app 880940) shows no
  third-party EULA; its only agreement link is Valve's Steam Subscriber Agreement (SSA). No EULA, licence or
  terms file ships in the game folder, and a web search found no terms of service on a Rebuilt Games site.
  The only legal text in the game's own UI is in the Workshop publish dialog, which points to the Steam
  Workshop legal agreement.
- So the governing text is the **Steam Subscriber Agreement** (revision date shown: September 10, 2026),
  plus the Steam Workshop terms for anything published.
- The store page lists "Steam Workshop" and "Includes level editor" as features: making mods with the built-in
  editor is an intended use of the game.

## SSA passages that matter for this project

| Section | Gist | Relevance |
|---|---|---|
| 2.G | Except as permitted by the agreement, Subscription Terms or applicable law, no reverse engineering, decompiling, modifying or derivative works of Content and Services without Valve's written consent. | The plugin was written by reading decompiled game code and runs inside the game process. The repo contains no game code and no decompiled code, only names and descriptions. Whether decompiling for interoperability is permitted depends on the user's jurisdiction. |
| 4.B | No Cheats; no "mods, hacks, or any other unauthorized third-party software" to modify Content and Services; no tampering with their execution. | The clause is aimed at cheating and unfair advantage, but its wording is broad and names mods generally. BepInEx plugins are third-party software loaded into the game. |
| 4.C | No scripts, bots or other non-human-controlled systems. | The plugin automates the Mod Editor, not gameplay, matchmaking, trading or the Steam client. AI-only playtests use the game's own bots. |
| 4.D | Valve may restrict or terminate an account or subscription, without notice. | The consequence if Valve or the developer objects. |
| 6 | Workshop contributions: must be originally created by the contributor; licence granted to Valve; App-Specific Terms may add rules. | The plugin never publishes, and blocks publishing while enabled. Content an agent builds is published, if at all, by the human by hand, who is responsible for it. |

## Assessment

- **Lower-risk properties of this project:** editor-only; local; single-player context; no gameplay
  advantage; no network traffic beyond 127.0.0.1; no Workshop, matchmaking or Steam API writes (they are
  blocked); no redistribution of game files; the game is not patched on disk (BepInEx loads at runtime).
- **Open risks:**
  1. The SSA wording on third-party software and reverse engineering is broad. There is no developer
     statement permitting BepInEx-style plugins for this game.
  2. The plugin stays loaded during normal play, including online games, because BepInEx loads it at every
     start. It does nothing there except keep the publish block and nine record-only hooks installed, but
     "a third-party plugin was loaded during online play" is the situation clause 4.B is written for.
     The game has no known anti-cheat that reacts to BepInEx; this is an observation, not a guarantee.
  3. AI-assisted content published to the Workshop: clause 6.D requires that the contributor created it.
     That is the publishing human's question, not the plugin's, but the README should say so.

## Recommendations before a public release

1. **Contact Rebuilt Games** (already planned): describe the tool as editor-only, with the
   publish block and sandbox rule, and ask whether they object. Keep the answer with these notes.
2. **Say it plainly in the README**: use at your own risk; governed by the Steam Subscriber Agreement; not
   endorsed by Rebuilt Games or Valve; the user is responsible for what they publish.
3. **Recommend disabling the plugin for normal play**, especially online games: set `[General] PluginEnabled
   = false` (or use a separate BepInEx profile) when not modding. Consider making this easier later (e.g. a
   launch option or loading only when the Mod Editor is opened).
4. Keep the rule that the repo and the packages never contain game DLLs or decompiled code.

## Sources

- Steam store page: https://store.steampowered.com/app/880940/Pummel_Party/
- Steam Subscriber Agreement: https://store.steampowered.com/subscriber_agreement/
