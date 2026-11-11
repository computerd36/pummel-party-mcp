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
