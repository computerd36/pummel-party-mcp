using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PummelBridge.Safety
{
    /// <summary>
    /// Builds the list of Workshop create/update/delete methods to block (docs/bridge-api.md "Safety").
    /// Everything is resolved by name at startup, so a game update that renames a target is reported as missing
    /// (fail closed) instead of breaking plugin load.
    /// </summary>
    internal static class PublishBlockTargets
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private const string SteamworksAssemblyName = "com.rlabrecque.steamworks.net";

        /// <summary>SteamUGC mutators of an existing item that do not take a UGCUpdateHandle_t (blocked, not required).</summary>
        private static readonly string[] s_ugcItemMutators = { "AddDependency", "RemoveDependency", "AddAppDependency", "RemoveAppDependency" };

        /// <summary>SteamRemoteStorage methods blocked by exact name (plus every name containing "FileWrite" and starting with "UpdatePublishedFile").</summary>
        private static readonly string[] s_remoteStorageWrites =
        {
            "FileDelete", "FileForget", "FileShare", "SetSyncPlatforms", "PublishWorkshopFile", "PublishVideo",
            "CreatePublishedFileUpdateRequest", "CommitPublishedFileUpdate", "DeletePublishedFile",
        };

        public static List<BlockTarget> Build()
        {
            List<BlockTarget> list = new List<BlockTarget>();
            Assembly game = typeof(EditorSystem).Assembly;
            Assembly steam = FindSteamworks(out string steamProblem);

            // ---- game layer: all required ----
            Game(list, game, steam, "WorkshopModInstaller", "PublishItem");
            Game(list, game, steam, "WorkshopModInstaller", "SubmitItemUpdate", "Steamworks.UGCQueryHandle_t");
            Game(list, game, steam, "WorkshopModInstaller", "BeginUGCItemContentUpdate", "Steamworks.PublishedFileId_t");
            Game(list, game, steam, "WorkshopModInstaller", "OnCreateItem", "Steamworks.CreateItemResult_t", "System.Boolean");
            Game(list, game, steam, "WorkshopModInstaller", "OnSteamUpdateUGCQuery", "Steamworks.SteamUGCQueryCompleted_t", "System.Boolean");
            Game(list, game, steam, "WorkshopModInstaller", "SetUGCPreviewFiles", "Steamworks.UGCQueryHandle_t", "PummelMod");
            Game(list, game, steam, "WorkshopPopup", "PublishMod", "PummelMod");
            Game(list, game, steam, "WorkshopPopup", "ResetPublishFileIdAndRetryPublish", "PummelMod");
            Game(list, game, steam, "WorkshopPopup", "DeleteBlankWorkshopItemOnFailedPublish", "PummelMod");
            Game(list, game, steam, "WorkshopBrowserItem", "Delete");

            // ---- Steamworks.SteamUGC ----
            Type ugc = steam?.GetType("Steamworks.SteamUGC", false);
            Exact(list, ugc, steam, steamProblem, "SteamUGC", true, "CreateItem", "Steamworks.AppId_t", "Steamworks.EWorkshopFileType");
            Exact(list, ugc, steam, steamProblem, "SteamUGC", true, "StartItemUpdate", "Steamworks.AppId_t", "Steamworks.PublishedFileId_t");
            Exact(list, ugc, steam, steamProblem, "SteamUGC", true, "SubmitItemUpdate", "Steamworks.UGCUpdateHandle_t", "System.String");
            Exact(list, ugc, steam, steamProblem, "SteamUGC", true, "DeleteItem", "Steamworks.PublishedFileId_t");
            if (ugc != null)
            {
                Type updateHandle = steam.GetType("Steamworks.UGCUpdateHandle_t", false);
                foreach (MethodInfo m in ugc.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    bool onUpdateHandle = updateHandle != null && ps.Length > 0 && ps[0].ParameterType == updateHandle
                        && !m.Name.StartsWith("Get", StringComparison.Ordinal); // GetItemUpdateProgress only reads
                    if (onUpdateHandle || s_ugcItemMutators.Contains(m.Name))
                    {
                        list.Add(new BlockTarget { Name = PublishBlocker.Describe(m), Layer = "SteamUGC", Required = false, Method = m });
                    }
                }
            }

            // ---- Steamworks.SteamRemoteStorage (legacy publish + Steam Cloud writes; the game uses none of them) ----
            Type rs = steam?.GetType("Steamworks.SteamRemoteStorage", false);
            if (rs == null)
            {
                list.Add(new BlockTarget { Name = "SteamRemoteStorage.*", Layer = "SteamRemoteStorage", Required = false, Problem = steamProblem ?? "type not found" });
            }
            else
            {
                foreach (MethodInfo m in rs.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (IsRemoteStorageWrite(m.Name))
                    {
                        list.Add(new BlockTarget { Name = PublishBlocker.Describe(m), Layer = "SteamRemoteStorage", Required = false, Method = m });
                    }
                }
            }
            return list;
        }

        internal static bool IsRemoteStorageWrite(string name) =>
            name.IndexOf("FileWrite", StringComparison.Ordinal) >= 0
            || name.StartsWith("UpdatePublishedFile", StringComparison.Ordinal)
            || s_remoteStorageWrites.Contains(name);

        private static void Game(List<BlockTarget> list, Assembly game, Assembly steam, string typeName, string method, params string[] paramTypes)
        {
            Type t = game.GetType(typeName, false);
            string display = $"{typeName}.{method}({string.Join(", ", paramTypes.Select(Short).ToArray())})";
            if (t == null)
            {
                list.Add(new BlockTarget { Name = display, Layer = "game", Required = true, Problem = $"type {typeName} not found" });
                return;
            }
            list.Add(Resolve(t, method, paramTypes, game, steam, display, "game", true));
        }

        private static void Exact(List<BlockTarget> list, Type t, Assembly steam, string steamProblem, string layer, bool required, string method, params string[] paramTypes)
        {
            string display = $"{layer}.{method}({string.Join(", ", paramTypes.Select(Short).ToArray())})";
            if (t == null)
            {
                list.Add(new BlockTarget { Name = display, Layer = layer, Required = required, Problem = steamProblem ?? $"type Steamworks.{layer} not found" });
                return;
            }
            list.Add(Resolve(t, method, paramTypes, typeof(EditorSystem).Assembly, steam, display, layer, required));
        }

        private static BlockTarget Resolve(Type t, string method, string[] paramTypes, Assembly game, Assembly steam, string display, string layer, bool required)
        {
            Type[] types = new Type[paramTypes.Length];
            for (int i = 0; i < paramTypes.Length; i++)
            {
                types[i] = ResolveType(paramTypes[i], game, steam);
                if (types[i] == null)
                {
                    return new BlockTarget { Name = display, Layer = layer, Required = required, Problem = $"parameter type {paramTypes[i]} not found" };
                }
            }
            MethodInfo mi = t.GetMethod(method, Any, null, types, null);
            return mi == null
                ? new BlockTarget { Name = display, Layer = layer, Required = required, Problem = "method not found (signature changed?)" }
                : new BlockTarget { Name = display, Layer = layer, Required = required, Method = mi };
        }

        private static Type ResolveType(string name, Assembly game, Assembly steam)
        {
            if (name.StartsWith("System.", StringComparison.Ordinal))
            {
                return Type.GetType(name, false);
            }
            if (name.StartsWith("Steamworks.", StringComparison.Ordinal))
            {
                return steam?.GetType(name, false);
            }
            return game.GetType(name, false);
        }

        private static string Short(string typeName)
        {
            int dot = typeName.LastIndexOf('.');
            return dot < 0 ? typeName : typeName.Substring(dot + 1);
        }

        private static Assembly FindSteamworks(out string problem)
        {
            problem = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(a.GetName().Name, SteamworksAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }
            try
            {
                return Assembly.Load(SteamworksAssemblyName);
            }
            catch (Exception e)
            {
                problem = $"assembly {SteamworksAssemblyName} not loadable: {e.Message}";
                return null;
            }
        }
    }
}
