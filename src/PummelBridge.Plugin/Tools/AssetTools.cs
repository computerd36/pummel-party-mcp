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
                string search = ctx.GetString("search");
                int limit = ctx.GetInt("limit", AssetListing.DefaultLimit, 1, AssetListing.MaxLimit);
                int offset = ctx.GetInt("offset", 0, 0, int.MaxValue);
                return ctx.OnMainThread(() => ListAssets(type, includeBuiltIn, search, limit, offset));
            }));
        }

        private static JObject ListAssets(string typeFilter, bool includeBuiltIn, string search, int limit, int offset)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);

            List<AssetEntry> rows = new List<AssetEntry>();
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
                    AddRow(rows, a, modRoot);
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
                    AddRow(rows, a, null);
                }
            }

            return AssetListing.Query(rows, typeFilter, search, limit, offset);
        }

        private static void AddRow(List<AssetEntry> rows, ModAsset a, string modRoot)
        {
            string type = ValueConverter.AssetTypeName(a);
            string className = a.GetType().Name;
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
            rows.Add(new AssetEntry
            {
                Guid = a.guid.ToString(),
                Name = a.name,
                Type = type,
                TypeAliases = new[] { className, a.assetTypeString ?? string.Empty },
                Row = row,
            });
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
