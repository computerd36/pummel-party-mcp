using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// Game-side helpers of the prefab tools (create_prefab, open_prefab, close_prefab, the save_mod protection, get_asset on prefabs).
    /// Main thread only. The dry run is ModHierarchySerializer.SaveTransform into a MemoryStream: the same call PummelPrefab.Save
    /// makes into the file (and the editor's undo snapshot makes every frame), so it writes no file and exercises every component's
    /// OnBeforeSerialize and writer exactly as the real save will.
    /// </summary>
    internal static class PrefabAccess
    {
        /// <summary>Serialises the hierarchy below <paramref name="t"/> to memory (the exact bytes PummelPrefab.Save would write).</summary>
        public static byte[] DryRun(Transform t)
        {
            if (t == null)
            {
                throw new InvalidOperationException("the prefab object is missing (destroyed)");
            }
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    ModHierarchySerializer.SaveTransform(w, t);
                    w.Flush();
                    return ms.ToArray();
                }
            }
        }

        public static byte[] DryRun(PummelPrefab p)
        {
            GameObject go = p.PrefabObject;
            return DryRun(go == null ? null : go.transform);
        }

        /// <summary>The prefabs PummelMod.Save rewrites (every ModAssetExternal that is not HideAndDontSave, filtered to prefabs).</summary>
        public static List<PummelPrefab> SavedPrefabs(PummelMod mod)
        {
            List<PummelPrefab> list = new List<PummelPrefab>();
            if (mod?.Assets?.assets == null)
            {
                return list;
            }
            foreach (ModAsset a in mod.Assets.assets)
            {
                if (a is PummelPrefab p && !p.HideAndDontSave)
                {
                    list.Add(p);
                }
            }
            return list;
        }

        public static PrefabFileJob Prepare(PummelPrefab p, string assetsFull) =>
            PrefabWriteGuard.Prepare(p.name, p.filePath, assetsFull, () => DryRun(p));

        /// <summary>Prefab instances in the scene that reference <paramref name="prefab"/> (the same loop the Return to Hierarchy handler runs).</summary>
        public static List<ModPrefabInstance> SceneInstances(EditorSystem ed, PummelPrefab prefab)
        {
            List<ModPrefabInstance> list = new List<ModPrefabInstance>();
            if (ed.m_hierarchyRoot == null)
            {
                return list;
            }
            foreach (ModPrefabInstance inst in ed.m_hierarchyRoot.GetComponentsInChildren<ModPrefabInstance>(true))
            {
                if (inst != null && ReferenceEquals(inst.PrefabReference, prefab))
                {
                    list.Add(inst);
                }
            }
            return list;
        }

        public static JArray InstancesJson(IEnumerable<ModPrefabInstance> instances, int max = 200)
        {
            JArray arr = new JArray();
            foreach (ModPrefabInstance inst in instances)
            {
                if (arr.Count >= max)
                {
                    break;
                }
                ModTransform mt = inst.GetComponent<ModTransform>();
                arr.Add(new JObject
                {
                    ["guid"] = mt != null ? EditorAccess.GuidOf(mt) : null,
                    ["name"] = inst.gameObject.name,
                });
            }
            return arr;
        }

        /// <summary>The object tree of the prefab (get_hierarchy node shape), or null when its object is gone.</summary>
        public static JObject TreeOf(PummelPrefab p)
        {
            GameObject go = p.PrefabObject;
            ModTransform mt = go == null ? null : go.GetComponent<ModTransform>();
            return mt == null ? null : Tools.HierarchyTools.NodeFor(mt);
        }

        /// <summary>
        /// Object and action references held by components inside <paramref name="root"/>'s subtree (read with the plugin's converter, full
        /// action detail). A reference to an object inside the subtree is flagged PointsInside.
        /// </summary>
        public static List<SubtreeReference> SubtreeReferences(PummelMod mod, Transform root, out bool incomplete)
        {
            incomplete = false;
            List<SubtreeReference> found = new List<SubtreeReference>();
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: false);
            HashSet<string> inside = new HashSet<string>();
            foreach (ModBehaviour mb in root.GetComponentsInChildren<ModBehaviour>(true))
            {
                inside.Add(EditorAccess.GuidOf(mb));
            }
            foreach (ModBehaviour mb in root.GetComponentsInChildren<ModBehaviour>(true))
            {
                ModReflectedType reflected = ModReflectionSystem.GetType(mb.GetType());
                if (reflected == null)
                {
                    continue;
                }
                try
                {
                    JObject entry = ComponentReader.Read(mb, reflected, conv);
                    string owner = mb.gameObject.name + " / " + reflected.name;
                    PrefabSourceRules.FindObjectRefs(entry["properties"], string.Empty, owner, inside, found, ref incomplete);
                }
                catch (Exception)
                {
                    incomplete = true;
                }
            }
            return found;
        }
    }
}
