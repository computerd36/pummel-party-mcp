using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PummelBridge.Safety;

namespace PummelBridge.PatchTests
{
    /// <summary>
    /// Offline drift test: loads the real Assembly-CSharp.dll and com.rlabrecque.steamworks.net.dll from the local game
    /// install for REFLECTION ONLY (no code of the game runs, no static constructors, no Steam, no Unity, no patch applied)
    /// and checks that <see cref="PublishBlockTargets.Build(Assembly, Assembly, string)"/> resolves every required Workshop
    /// target and yields the expected number of targets. Catches a game update that renames or re-signs a blocked method
    /// before the plugin fails closed in the field. Nothing is copied: the assemblies are read from the game folder.
    /// </summary>
    internal static class GameDriftTest
    {
        /// <summary>Methods the block patches for the game build the plugin was written against (docs/bridge-api.md, patchedCount).</summary>
        private const int ExpectedTargetCount = 59;

        public static void Run(Action<string, bool> check)
        {
            Console.WriteLine("-- offline drift test: PublishBlockTargets against the real game assemblies");
            string gameDir = ReadGameDir();
            string managed = gameDir == null ? null : Path.Combine(gameDir, "PummelParty_Data", "Managed");
            string gameDll = managed == null ? null : Path.Combine(managed, "Assembly-CSharp.dll");
            string steamDll = managed == null ? null : Path.Combine(managed, "com.rlabrecque.steamworks.net.dll");
            if (gameDll == null || !File.Exists(gameDll) || !File.Exists(steamDll))
            {
                Console.WriteLine("  SKIP  game assemblies not found (GameDir = " + (gameDir ?? "<not set>") + "); drift test not run");
                return;
            }

            ResolveEventHandler resolver = (sender, args) => ReflectionOnlyResolve(args, managed);
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolver;
            try
            {
                Assembly game = Assembly.ReflectionOnlyLoadFrom(gameDll);
                Assembly steam = Assembly.ReflectionOnlyLoadFrom(steamDll);
                List<BlockTarget> targets = PublishBlockTargets.Build(game, steam, null);

                List<BlockTarget> unresolved = targets.Where(t => t.Required && t.Method == null).ToList();
                foreach (BlockTarget t in unresolved)
                {
                    Console.WriteLine("    unresolved required target: " + t.Name + " (" + t.Problem + ")");
                }
                check("every required target resolves in the real assemblies (" + targets.Count(t => t.Required) + " required)", unresolved.Count == 0 && targets.Any(t => t.Required));
                // The same method can be listed twice (a required SteamUGC target that also takes an update handle); the block
                // patches each method once, so the expected number counts distinct methods, like PublishBlockStatus.PatchedCount.
                int distinct = targets.Where(t => t.Method != null).Select(t => t.Method).Distinct().Count();
                check($"distinct patched methods = {ExpectedTargetCount} (got {distinct}; {targets.Count} list entries, {targets.Count(t => t.Method == null)} unresolved)",
                    distinct == ExpectedTargetCount && targets.All(t => t.Method != null));
                check("every target belongs to the game or Steamworks.NET assembly", targets.Where(t => t.Method != null)
                    .All(t => t.Method.DeclaringType.Assembly == game || t.Method.DeclaringType.Assembly == steam));
                ReflectedMembers(game, check);
            }
            catch (Exception e)
            {
                check("drift test could not run: " + e.GetType().Name + ": " + e.Message, false);
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolver;
            }
        }

        /// <summary>
        /// Private game fields the plugin reads/writes by name (playtest and validator tools, increment 4). Public members
        /// are checked by the compiler; these are not, so a game update renaming one would only show up at runtime.
        /// </summary>
        private static void ReflectedMembers(Assembly game, Action<string, bool> check)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            (string type, string field, string fieldType, bool isStatic)[] fields =
            {
                ("EditorSystem", "m_toolbarWidget", "ToolbarWidget", false),
                ("ToolbarWidget", "m_playerCountChoice", "System.Int32", false),
                ("ToolbarWidget", "m_aiOnly", "System.Boolean", false),
                ("ToolbarWidget", "m_backgroundModLoadTimer", "System.Single", false),
                ("ToolbarWidget", "m_playerCount", "System.String[]", false),
                ("ModValidationSystem", "m_validators", "System.Collections.Generic.List`1[ModValidationCallbackReceiver]", true),
                ("MinigameController", "round", "System.Int32", false),
                // playtest visibility (get_playtest_state / get_playtest_events, plugin 0.6.0)
                ("MinigameController", "round_begin_time", "System.Single", false),
                ("SimpleModPlayer", "m_lastDeathTime", "System.Single", false),
                ("SimpleModPlayer", "m_filoHeldItemStack", "System.Collections.Generic.List`1[ModItem]", false),
                ("ModSystem.Logic.ModLogicTrigger", "m_owner", "ModSystem.Logic.IEventReceiver", false),
            };
            foreach (var f in fields)
            {
                Type t = game.GetType(f.type, false);
                FieldInfo fi = t?.GetField(f.field, any);
                check($"reflected field {f.type}.{f.field} exists ({f.fieldType}{(f.isStatic ? ", static" : "")})",
                    fi != null && fi.FieldType.ToString() == f.fieldType && fi.IsStatic == f.isStatic);
            }
            PlaytestHookTargets(game, check);
            Type modWorkshopController = game.GetType("WorkshopController", false);
            check("WorkshopController.CurrentBackgroundLoadMod / CurrentActiveMod are static properties",
                modWorkshopController?.GetProperty("CurrentBackgroundLoadMod", BindingFlags.Public | BindingFlags.Static) != null
                && modWorkshopController.GetProperty("CurrentActiveMod", BindingFlags.Public | BindingFlags.Static) != null);
        }

        /// <summary>
        /// The recording hooks of the playtest event feed (Hooks/PlaytestHooks.cs): ModLogicTrigger.RunActionsLocal(ModEvent) and the
        /// declared ModAction.Execute(ModEvent) overrides (resolved the same way the hook installer does). A game update that renames
        /// or re-signs them would silently reduce the feed to the diff-based events, so it is caught here.
        /// </summary>
        private static void PlaytestHookTargets(Assembly game, Action<string, bool> check)
        {
            Type trigger = game.GetType("ModSystem.Logic.ModLogicTrigger", false);
            Type action = game.GetType("ModSystem.Logic.ModAction", false);
            Type ev = game.GetType("ModSystem.Logic.ModEvent", false);
            MethodInfo run = trigger?.GetMethod("RunActionsLocal", BindingFlags.Instance | BindingFlags.Public, null, new[] { ev }, null);
            check("hook target ModLogicTrigger.RunActionsLocal(ModEvent) exists", run != null && run.ReturnType == typeof(void));
            string[] expected = { "GiveItemAction", "ModAction", "MoveSpaceAction", "SetConnectionActiveAction", "SetGravityAction", "SpawnPrefabAction", "TargetAction", "WaitAction" };
            string[] found = action == null || ev == null
                ? new string[0]
                : SafeTypes(game)
                    .Where(t => t == action || t.IsSubclassOf(action))
                    .Where(t => t.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { ev }, null) != null)
                    .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            check("hook targets: ModAction.Execute(ModEvent) is declared by exactly " + string.Join(", ", expected) + " (found: " + string.Join(", ", found) + ")",
                found.SequenceEqual(expected));
            check("hook target ModAction.Execute returns YieldInstruction",
                action?.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { ev }, null)?.ReturnType.Name == "YieldInstruction");
        }

        private static Type[] SafeTypes(Assembly a)
        {
            try
            {
                return a.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null).ToArray();
            }
        }

        /// <summary>GameDir as the project file saw it at build time (same property the plugin csproj uses).</summary>
        private static string ReadGameDir()
        {
            foreach (AssemblyMetadataAttribute a in typeof(GameDriftTest).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false).Cast<AssemblyMetadataAttribute>())
            {
                if (a.Key == "GameDir" && !string.IsNullOrWhiteSpace(a.Value))
                {
                    return a.Value;
                }
            }
            return null;
        }

        /// <summary>Dependencies of the game assemblies: the game's Managed folder first, then the runtime's own (framework) assemblies.</summary>
        private static Assembly ReflectionOnlyResolve(ResolveEventArgs args, string managed)
        {
            string simple = new AssemblyName(args.Name).Name;
            string candidate = Path.Combine(managed, simple + ".dll");
            if (File.Exists(candidate))
            {
                return Assembly.ReflectionOnlyLoadFrom(candidate);
            }
            try
            {
                return Assembly.ReflectionOnlyLoad(args.Name);
            }
            catch (Exception)
            {
                return Assembly.ReflectionOnlyLoad(simple);
            }
        }
    }
}
