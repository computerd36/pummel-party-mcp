using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;

namespace PummelBridge.Tools
{
    /// <summary>list_assets.</summary>
    internal static class AssetTools
    {
        public static void Register(ToolRegistry registry)
        {
            registry.Register(ToolDefinition.Read("list_assets", ctx =>
            {
                string type = ctx.GetString("type");
                bool includeBuiltIn = ctx.GetBool("includeBuiltIn", false);
                return ctx.OnMainThread(() => ListAssets(type, includeBuiltIn));
            }));
        }

        private static JArray ListAssets(string typeFilter, bool includeBuiltIn)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);

            List<JObject> rows = new List<JObject>();
            string modRoot = SafeFullPath(mod.m_modRootDirectory);

            // Mod assets: external files (+ .pmeta). "runtime" entries are scene components registered by
            // ModBehaviourMonitor for guid lookups, not assets; they are skipped.
            if (mod.Assets?.assets != null)
            {
                foreach (ModAsset a in mod.Assets.assets)
                {
                    if (a == null || ValueConverter.LocationName(a) == "runtime")
                    {
                        continue;
                    }
                    AddRow(rows, a, typeFilter, modRoot);
                }
            }

            if (includeBuiltIn)
            {
                WorkshopController wc = WorkshopController.Instance;
                if (wc == null || wc.Assets == null)
                {
                    throw RpcException.Internal("WorkshopController asset list is not available");
                }
                foreach (ModAsset a in wc.Assets.assets)
                {
                    if (a == null || ValueConverter.LocationName(a) == "runtime")
                    {
                        continue;
                    }
                    AddRow(rows, a, typeFilter, null);
                }
            }

            IEnumerable<JObject> ordered = rows
                .OrderBy(r => (string)r["location"] == "external" ? 0 : 1)
                .ThenBy(r => (string)r["type"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => (string)r["name"], StringComparer.OrdinalIgnoreCase);
            return new JArray(ordered);
        }

        private static void AddRow(List<JObject> rows, ModAsset a, string typeFilter, string modRoot)
        {
            string type = ValueConverter.AssetTypeName(a);
            string className = a.GetType().Name;
            if (typeFilter != null &&
                !string.Equals(typeFilter, type, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(typeFilter, className, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(typeFilter, a.assetTypeString ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            JObject row = new JObject
            {
                ["guid"] = a.guid.ToString(),
                ["name"] = a.name,
                ["type"] = type,
                ["className"] = className,
                ["location"] = ValueConverter.LocationName(a),
            };
            if (a is ModAssetExternal ext && modRoot != null && !string.IsNullOrEmpty(ext.filePath))
            {
                string rel = RelativeTo(modRoot, ext.filePath);
                if (rel != null)
                {
                    row["relativePath"] = rel;
                }
            }
            rows.Add(row);
        }

        private static string SafeFullPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Path relative to the mod folder with '/' separators; null if outside it (never leaks absolute paths).</summary>
        private static string RelativeTo(string rootWithSep, string file)
        {
            string full;
            try
            {
                full = Path.GetFullPath(file);
            }
            catch (Exception)
            {
                return null;
            }
            if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return full.Substring(rootWithSep.Length).Replace('\\', '/');
        }
    }
}
