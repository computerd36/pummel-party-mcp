using System;
using System.Collections.Generic;
using System.Linq;

namespace PummelBridge.Core
{
    // =====================================================================================================
    // Reparent validation (pure; the game layer supplies the tree through delegates)
    // =====================================================================================================

    /// <summary>Rules for moving an object in the hierarchy (what the hierarchy widget's drag and drop allows).</summary>
    public static class ReparentRules
    {
        /// <summary>True if <paramref name="node"/> is <paramref name="ancestor"/> or lies below it.</summary>
        public static bool IsSelfOrDescendant<T>(T node, T ancestor, Func<T, T> parentOf) where T : class
        {
            int guard = 0;
            for (T cur = node; cur != null; cur = parentOf(cur))
            {
                if (ReferenceEquals(cur, ancestor))
                {
                    return true;
                }
                if (++guard > 100000)
                {
                    break; // defensive: broken parent chain
                }
            }
            return false;
        }

        /// <summary>
        /// Null if the move is allowed, otherwise the reason. <paramref name="newParent"/> is the destination object, or
        /// the context root when the caller asked for "no parent". <paramref name="contextOf"/> returns the editing context
        /// (scene root / prefab object) an object belongs to.
        /// </summary>
        public static string Check<T>(T target, T newParent, Func<T, T> parentOf, Func<T, bool> hidesChildren,
            Func<T, bool> isContextRootObject, Func<T, object> contextOf, Func<T, string> nameOf) where T : class
        {
            if (target == null)
            {
                return "no object";
            }
            if (isContextRootObject(target))
            {
                return $"'{nameOf(target)}' is the prefab's root object and cannot be moved";
            }
            if (newParent == null)
            {
                return "no destination";
            }
            if (ReferenceEquals(target, newParent))
            {
                return "an object cannot become its own parent";
            }
            if (IsSelfOrDescendant(newParent, target, parentOf))
            {
                return $"'{nameOf(newParent)}' is a descendant of '{nameOf(target)}'; moving would create a cycle";
            }
            if (!ReferenceEquals(contextOf(target), contextOf(newParent)))
            {
                return "the destination belongs to a different context (scene vs prefab, or another prefab)";
            }
            if (hidesChildren(newParent))
            {
                return $"'{nameOf(newParent)}' does not support child objects (prefab instance, board space, item or text)";
            }
            return null;
        }
    }

    // =====================================================================================================
    // Component addability (replica of ModEditorComponentAdderRemover.PerformChecksForComponentAddability and the
    // Add Component picker's filter; pure so it can be evaluated without touching the editor's static scratch state)
    // =====================================================================================================

    /// <summary>A registered mod component type ([ModSerialized]).</summary>
    public sealed class ComponentTypeInfo
    {
        /// <summary>Class name (ModReflectedType.name), e.g. "ModLight".</summary>
        public string Name;

        /// <summary>System.Type.ToString() of the class (no namespace for the game's mod components).</summary>
        public string FullName;

        public string UiName;
        public string Folder;

        /// <summary>System.Type.ToString() of the base class.</summary>
        public string BaseTypeFullName;

        public bool Addable;
        public int ModTypeFlags;
        public bool SupportsChildren = true;
        public bool DisallowMultiple;
        public bool BaseClassDisallowMultiple;
    }

    /// <summary>One component on the object (every Unity component, as GetComponents&lt;Component&gt;() returns them).</summary>
    public sealed class ComponentFact
    {
        public string FullName;
        public string BaseTypeFullName;

        /// <summary>Registry entry if the component is a registered mod component, else null.</summary>
        public ComponentTypeInfo Reflected;
    }

    public sealed class ObjectFacts
    {
        public List<ComponentFact> Components = new List<ComponentFact>();
        public bool HasChildren;

        /// <summary>The object has a ModPrefabInstance (the inspector hides Add Component and all other components).</summary>
        public bool IsPrefabInstance;
    }

    public static class ComponentRules
    {
        // PummelModTypeFlags
        public const int FlagMinigame = 1;
        public const int FlagMap = 2;
        public const int FlagItem = 4;
        public const int FlagBoardSpace = 8;

        /// <summary>EditorSystem.CanUseItem: PummelModType name -> flag test.</summary>
        public static bool CanUseItem(string modType, int flags)
        {
            switch (modType)
            {
                case "BoardSpace": return (flags & FlagBoardSpace) != 0;
                case "Item": return (flags & FlagItem) != 0;
                case "Board": return (flags & FlagMap) != 0;
                case "Minigame": return (flags & FlagMinigame) != 0;
                default: return false;
            }
        }

        public static string[] ModTypeNames(int flags)
        {
            List<string> r = new List<string>();
            if ((flags & FlagMinigame) != 0) r.Add("Minigame");
            if ((flags & FlagMap) != 0) r.Add("Board");
            if ((flags & FlagItem) != 0) r.Add("Item");
            if ((flags & FlagBoardSpace) != 0) r.Add("BoardSpace");
            return r.ToArray();
        }

        /// <summary>AddComponentPopup.DisplayComponent shows a type only if it is addable and allowed for the mod type.</summary>
        public static string WhyNotOffered(ComponentTypeInfo t, string modType)
        {
            if (!t.Addable)
            {
                return $"{t.UiName} is not offered by the editor's Add Component picker";
            }
            if (!CanUseItem(modType, t.ModTypeFlags))
            {
                string[] allowed = ModTypeNames(t.ModTypeFlags);
                return allowed.Length == 0
                    ? $"{t.UiName} is not available for any mod type in the editor"
                    : $"{t.UiName} is only available in {string.Join("/", allowed)} mods (this is a {modType} mod)";
            }
            return null;
        }

        /// <summary>
        /// Exact replica of PerformChecksForComponentAddability after SetupComponentAddabilityCheck([object]).
        /// <paramref name="obj"/> null: only the context-dependent checks (no object given).
        /// </summary>
        public static (bool blocked, string reason) PerformChecks(ComponentTypeInfo type, ObjectFacts obj, bool editingPrefab)
        {
            bool blocked = false;
            string text = "";
            if (type == null)
            {
                return (true, "");
            }
            HashSet<string> contains = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> containsBase = new HashSet<string>(StringComparer.Ordinal);
            List<ComponentTypeInfo> nonChild = new List<ComponentTypeInfo>();
            if (obj != null)
            {
                foreach (ComponentFact c in obj.Components)
                {
                    contains.Add(c.FullName);
                    containsBase.Add(c.BaseTypeFullName ?? "");
                    if (c.Reflected != null && !c.Reflected.SupportsChildren && !nonChild.Any(n => n.FullName == c.Reflected.FullName))
                    {
                        nonChild.Add(c.Reflected);
                    }
                }
            }
            bool hasChildren = obj != null && obj.HasChildren;

            if (!type.SupportsChildren && hasChildren)
            {
                blocked = true;
                text = AddReason(text, type.UiName + " component can only be added when the selected gameobject has no child gameobjects.");
            }
            if (type.FullName == "ModBoardSpaceEvent" && !editingPrefab)
            {
                blocked = true;
                text = AddReason(text, "The " + type.UiName + " component can only be used inside of a prefab to create a custom board space. It cannot be placed on a non-custom board space.");
            }
            if (editingPrefab && (type.FullName == "ModPrefabInstance" || type.FullName == "ModBoardSpace"))
            {
                blocked = true;
                text = AddReason(text, type.UiName + " component can not be used inside a prefab.");
            }
            if (obj != null)
            {
                if (type.FullName == "ModPrefabInstance")
                {
                    if ((contains.Count != 2 || !contains.Contains("UnityEngine.Transform")) &&
                        (!contains.Contains("UnityEngine.RectTransform") || !contains.Contains("ModTransform")))
                    {
                        blocked = true;
                        text = AddReason(text, type.UiName + " component can only be added when the selected gameobject has no other components.");
                    }
                }
                else
                {
                    if (type.DisallowMultiple && contains.Contains(type.FullName))
                    {
                        blocked = true;
                        text = AddReason(text, "Gameobjects can only support a single " + type.UiName + " component.");
                    }
                    else if (!type.SupportsChildren)
                    {
                        foreach (ComponentTypeInfo existing in nonChild)
                        {
                            if (existing.FullName != type.FullName)
                            {
                                blocked = true;
                                text = AddReason(text, type.UiName + " cannot be added to a gameobject containing a " + existing.UiName + " component.");
                            }
                        }
                    }
                    if (type.BaseClassDisallowMultiple && containsBase.Contains(type.BaseTypeFullName ?? ""))
                    {
                        blocked = true;
                        text = AddReason(text, $"Another component on the selected object(s) conflicts with the {type.UiName} component. Cannot add multiple components with {type.BaseTypeFullName} base.");
                    }
                }
                if (type.FullName == "ModMover" && contains.Contains("ModSimpleAnimator"))
                {
                    blocked = true;
                    text = AddReason(text, "A " + type.UiName + " component cannot be added to a gameobject that contains a Simple Animator component.");
                }
                else if (type.FullName == "ModSimpleAnimator" && contains.Contains("ModMover"))
                {
                    blocked = true;
                    text = AddReason(text, "A " + type.UiName + " component cannot be added to a gameobject that contains a Mover component.");
                }
            }
            return (blocked, text);
        }

        /// <summary>
        /// Everything the UI checks before a component can be added to <paramref name="obj"/> (null = no object, only the
        /// mod-type/context rules). Null = addable.
        /// </summary>
        public static string WhyNotAddable(ComponentTypeInfo type, ObjectFacts obj, string modType, bool editingPrefab)
        {
            string why = WhyNotOffered(type, modType);
            if (why != null)
            {
                return why;
            }
            if (obj != null && obj.IsPrefabInstance)
            {
                return "components cannot be added to a prefab instance (the inspector shows no Add Component button for it); edit the prefab instead";
            }
            (bool blocked, string reason) = PerformChecks(type, obj, editingPrefab);
            return blocked ? (string.IsNullOrEmpty(reason) ? "blocked by the editor's component rules" : reason) : null;
        }

        /// <summary>The inspector's remove button exists only for addable types; on prefab instances only Transform and Prefab Instance are shown.</summary>
        public static string WhyNotRemovable(ComponentTypeInfo type, bool objectIsPrefabInstance)
        {
            if (type == null)
            {
                return "not a registered mod component";
            }
            if (!type.Addable)
            {
                return $"{type.UiName} cannot be removed in the editor (no remove button for non-addable components)";
            }
            if (objectIsPrefabInstance && type.FullName != "ModPrefabInstance")
            {
                return $"the inspector does not show {type.UiName} on a prefab instance, so it cannot be removed there";
            }
            return null;
        }

        private static string AddReason(string reason, string newReason) =>
            string.IsNullOrEmpty(reason) ? newReason : reason + "\n" + newReason;
    }

    // =====================================================================================================
    // Inspector-only clamps (custom inspectors re-apply these on every drawn frame; the bridge applies them on write)
    // =====================================================================================================

    public static class InspectorClamps
    {
        private static readonly Dictionary<string, KeyValuePair<double?, double?>> s_table = new Dictionary<string, KeyValuePair<double?, double?>>(StringComparer.Ordinal)
        {
            ["ModAudioSource.volume"] = Range(0, 1),                 // ModAudioSourceInspector slider
            ["ModAudioSource.spatialBlend"] = Range(0, 1),
            ["ModAudioSource.stereoPanning"] = Range(-1, 1),
            ["ModBoardSpace.SpawnRotation"] = Range(0, 360),         // ModBoardSpaceInspector slider
            ["ModBoardSpace.SpawnDistance"] = Range(0, 4),
            ["ModFireWeapon.FireTime"] = Range(0.1, null),           // Mathf.Max(0.1f, ...)
            ["ModItem.SecondsBetweenHeldAction"] = Range(0.1, null),
            ["ModItem.Cooldown"] = Range(0.1, null),
            ["ModLight.spotAngle"] = Range(0, 180),                  // ModLightInspector slider
            ["ModParticleSystem.arcSpread"] = Range(0, 1),           // ModParticleSystemInspector slider
            ["ModSpawner.SpawnCount"] = Range(0, 20),                // Mathf.Clamp(..., 0, 20)
            ["ModText.OutlineThickness"] = Range(0, 1),              // ModTextInspector sliders
            ["ModText.ShadowOffsetX"] = Range(-1, 1),
            ["ModText.ShadowOffsetY"] = Range(-1, 1),
            ["ModText.ShadowSoftness"] = Range(0, 1),
            ["ModTrigger.StayTriggerInterval"] = Range(0.1, null),   // ModTriggerInspector: < 0.1 -> 0.1
            ["ModWater.CellSize"] = Range(0.1, 10),                  // Mathf.Clamp(..., 0.1f, 10f)
            ["ModWater.WaveHeight"] = Range(0, 1),
            ["ModWater.WaveSpeed"] = Range(0, 10),
        };

        private static KeyValuePair<double?, double?> Range(double? min, double? max) => new KeyValuePair<double?, double?>(min, max);

        public static IEnumerable<string> Keys => s_table.Keys;

        public static bool TryGet(string componentType, string property, out double? min, out double? max)
        {
            if (s_table.TryGetValue(componentType + "." + property, out KeyValuePair<double?, double?> r))
            {
                min = r.Key;
                max = r.Value;
                return true;
            }
            min = max = null;
            return false;
        }

        /// <summary>Clamps a parsed number/integer in place. Returns true if the value changed.</summary>
        public static bool Apply(string componentType, string property, PValue value)
        {
            if (!TryGet(componentType, property, out double? min, out double? max))
            {
                return false;
            }
            if (value is PNumber n)
            {
                double v = n.Value;
                if (min.HasValue && v < min.Value) v = min.Value;
                if (max.HasValue && v > max.Value) v = max.Value;
                bool changed = v != n.Value;
                n.Value = v;
                return changed;
            }
            if (value is PInteger i)
            {
                decimal v = i.Value;
                if (min.HasValue && v < (decimal)min.Value) v = (decimal)Math.Ceiling(min.Value);
                if (max.HasValue && v > (decimal)max.Value) v = (decimal)Math.Floor(max.Value);
                bool changed = v != i.Value;
                i.Value = v;
                return changed;
            }
            return false;
        }
    }

    // =====================================================================================================
    // Creation kinds (EditorSystem.DoGameObjectMenu entries -> ObjectType)
    // =====================================================================================================

    public sealed class ObjectKindInfo
    {
        /// <summary>API name.</summary>
        public string Kind;

        /// <summary>ObjectType enum member passed to EditorSystem.AddGameObject.</summary>
        public string ObjectType;

        /// <summary>Menu path in the editor.</summary>
        public string Menu;

        public bool MinigameOnly;
        public bool BoardOnly;
        public bool NotInPrefab;
    }

    public static class ObjectKinds
    {
        public static readonly ObjectKindInfo[] All =
        {
            K("empty", "Empty", "Create Empty"),
            new ObjectKindInfo { Kind = "prefabInstance", ObjectType = "NewPrefabInstance", Menu = "Prefab Instance", NotInPrefab = true },
            K("cube", "Cube", "3D Object > Cube"),
            K("sphere", "Sphere", "3D Object > Sphere"),
            K("plane", "Plane", "3D Object > Plane"),
            K("prop", "Prop", "3D Object > Prop"),
            K("particleSystem", "ParticleSystem", "3D Object > Particle System"),
            K("text", "Text", "3D Object > Text"),
            K("lineRenderer", "LineRenderer", "3D Object > Line Renderer"),
            M("playerSpawn", "PlayerSpawn", "Minigame Object > Player Spawn"),
            M("item", "Item", "Minigame Object > Item"),
            M("trigger", "Trigger", "Minigame Object > Trigger"),
            M("logic", "Logic", "Minigame Object > Logic"),
            M("weaponSource", "FireWeapon", "Minigame Object > Weapon Source"),
            new ObjectKindInfo { Kind = "boardSpace", ObjectType = "BoardSpace", Menu = "Board Object > Board space", BoardOnly = true, NotInPrefab = true },
            K("directionalLight", "DirectionalLight", "Light > Directional Light"),
            K("pointLight", "PointLight", "Light > Point Light"),
            K("spotLight", "SpotLight", "Light > Spot Light"),
            K("audioSource", "AudioSource", "Audio > Audio Source"),
        };

        private static ObjectKindInfo K(string kind, string type, string menu) => new ObjectKindInfo { Kind = kind, ObjectType = type, Menu = menu };

        private static ObjectKindInfo M(string kind, string type, string menu) => new ObjectKindInfo { Kind = kind, ObjectType = type, Menu = menu, MinigameOnly = true };

        public static ObjectKindInfo Find(string kind)
        {
            foreach (ObjectKindInfo k in All)
            {
                if (string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    return k;
                }
            }
            return null;
        }

        public static string KindList => string.Join(", ", All.Select(k => k.Kind).ToArray());

        /// <summary>
        /// The menu's enable rules (DoGameObjectMenu; the GameObject menu itself needs a hierarchy mod or an open prefab).
        /// Null = available.
        /// </summary>
        public static string WhyUnavailable(ObjectKindInfo k, string modType, bool usesHierarchy, bool editingPrefab, bool parentHidesChildren, string parentName)
        {
            if (!usesHierarchy && !editingPrefab)
            {
                return $"{modType} mods have no scene hierarchy; objects can only be created while a prefab is being edited";
            }
            if (parentHidesChildren)
            {
                return $"'{parentName}' does not support child objects (prefab instance, board space, item or text)";
            }
            if (k.MinigameOnly && modType != "Minigame")
            {
                return $"'{k.Kind}' ({k.Menu}) is only offered in Minigame mods";
            }
            if (k.BoardOnly && modType != "Board")
            {
                return $"'{k.Kind}' ({k.Menu}) is only offered in Board mods";
            }
            if (k.NotInPrefab && editingPrefab)
            {
                return $"'{k.Kind}' ({k.Menu}) is disabled while a prefab is being edited";
            }
            return null;
        }
    }
}
