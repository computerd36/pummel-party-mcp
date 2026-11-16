using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// Reads a mod component's reflected properties (ModReflectionSystem's list: public instance properties,
    /// [ModIgnore] excluded — exactly what the inspector, undo snapshot and scene file use) without triggering
    /// getter side effects found in the decompiled source:
    /// <list type="bullet">
    /// <item>Wrapper getters go through ModBehaviour.Component, which runs SetupComponents() (adds Unity
    /// components) when m_component is null. Such components are reported as unavailable instead.</item>
    /// <item>Getters that write caches/guids (GetWrappedAsset/SetWrappedAsset, GetOrAddComponent, material
    /// caches, binary script serialization) are replaced by reads of their backing fields (table below).</item>
    /// </list>
    /// Main thread only.
    /// </summary>
    internal static class ComponentReader
    {
        private delegate JToken Override(ModBehaviour component, ValueConverter converter);

        private static readonly Dictionary<string, Override> s_overrides = new Dictionary<string, Override>(StringComparer.Ordinal)
        {
            // GetWrappedAsset(...) rewrites the cached asset field and, for ModItem, clears the guid when unresolved.
            ["ModItem.ItemPrefab"] = (c, conv) => AssetFieldOrGuid(c, conv, "m_itemPummelPrefab", "m_itemPummelPrefabGuid"),
            ["ModItem.ItemPrefabGUID"] = (c, conv) => conv.Convert(Field(c, "m_itemPummelPrefabGuid")),
            ["ModItem.DroppedItemPrefab"] = (c, conv) => AssetFieldOrGuid(c, conv, "m_droppedItemPummelPrefab", "m_droppedItemPummelPrefabGuid"),
            ["ModItem.DroppedItemPrefabGUID"] = (c, conv) => conv.Convert(Field(c, "m_droppedItemPummelPrefabGuid")),
            ["ModPrefabInstance.PrefabReference"] = (c, conv) => AssetFieldOrGuid(c, conv, "m_prefabReference", "m_prefabReferenceGuid"),
            ["ModPrefabInstance.PrefabGUID"] = (c, conv) => conv.Convert(Field(c, "m_prefabReferenceGuid")),
            ["ModSpawner.Prefabs"] = (c, conv) => GuidArrayRefs(c, conv, "m_prefabGUIDs"),
            ["ModSpawner.PrefabGUIDs"] = (c, conv) => conv.Convert(Field(c, "m_prefabGUIDs")),
            // Getter caches renderer.sharedMaterials into a private field on first read.
            ["ModMeshRenderer.materials"] = (c, conv) => MaterialsFieldOrRenderer(c, conv, "m_tempMats"),
            ["ModLineRenderer.Materials"] = (c, conv) => MaterialsFieldOrRenderer(c, conv, "tempMats"),
            ["ModSkinnedMeshRenderer.materials"] = (c, conv) => MaterialsFieldOrRenderer(c, conv, "tempMats"),
            // Getter serializes the script object into a byte[] through the game's binary writer.
            ["ModScriptReference.scriptValues"] = (c, conv) => new JObject { ["unavailable"] = "binary script values are not decoded by the plugin" },
        };

        /// <summary>ModParticleSystem renderer getters call GetOrAddComponent&lt;ParticleSystemRenderer&gt; when the cached field is null.</summary>
        private static readonly HashSet<string> s_particleRendererProps = new HashSet<string>(StringComparer.Ordinal)
        {
            "Alignment", "rendererEnabled", "renderMode", "cameraVelocityScale", "velocityScale", "lengthScale",
            "freeformStretching", "rotateWithStretchDirection", "normalDirection", "material", "sortMode",
            "sortingFudge", "minParticleSize", "maxParticleSize", "flip", "allowRoll", "pivot", "maskInteraction",
            "shadowCastingMode", "shadowBias", "motionVectorGenerationMode",
        };

        private static readonly ConcurrentDictionary<Type, bool> s_usesComponentWrapper = new ConcurrentDictionary<Type, bool>();

        /// <summary>
        /// ModParticleSystem properties whose setters dereference the renderer field without initialising it (the getters
        /// initialise it; "Prop" has a plain getter but its setter dereferences the field too).
        /// </summary>
        internal static bool ParticleSetterNeedsRenderer(string property) => s_particleRendererProps.Contains(property) || property == "Prop";

        /// <summary>True while a getter of the component would run SetupComponents() (the wrapper pattern with no underlying Unity component yet).</summary>
        internal static bool WrapperNotReady(ModBehaviour component) => UsesComponentWrapper(component.GetType()) && component.m_component == null;

        /// <summary>ModParticleSystem: the private renderer field is still null (its renderer getters would add a ParticleSystemRenderer).</summary>
        internal static bool ParticleRendererMissing(ModBehaviour component) => IsUnityNull(Field(component, "m_particleSystemRenderer"));

        public static JObject Read(ModBehaviour component, ModReflectedType reflected, ValueConverter converter)
        {
            JObject entry = new JObject
            {
                ["type"] = reflected.name,
                ["uiName"] = reflected.uiName,
                ["guid"] = EditorAccess.GuidOf(component),
                ["enabled"] = component.enabled,
            };

            JObject props = new JObject();
            string unavailableReason = null;
            if (UsesComponentWrapper(component.GetType()) && component.m_component == null)
            {
                unavailableReason = "underlying Unity component not initialised; reading would run SetupComponents()";
            }

            string typeName = component.GetType().Name;
            foreach (ReflectedProperty p in reflected.properties ?? new ReflectedProperty[0])
            {
                JObject pj = new JObject { ["type"] = ValueConverter.FriendlyTypeName(p.systemType) };
                if (p.hideInInspector)
                {
                    pj["hiddenInInspector"] = true;
                }
                string inspectorHint = InspectorRuleHost.Hint(reflected, p, out bool neverShown);
                if (inspectorHint != null)
                {
                    // What the editor's inspector does with this property (set_property enforces it): shown=false when it is never drawn.
                    if (neverShown)
                    {
                        pj["shown"] = false;
                    }
                    pj["inspector"] = inspectorHint;
                }
                try
                {
                    if (p.name == "guid")
                    {
                        pj["value"] = converter.Convert(component.guid);
                    }
                    else if (unavailableReason != null)
                    {
                        pj["unavailable"] = unavailableReason;
                    }
                    else if (s_overrides.TryGetValue(typeName + "." + p.name, out Override ov))
                    {
                        pj["value"] = ov(component, converter);
                    }
                    else if (typeName == "ModParticleSystem" && s_particleRendererProps.Contains(p.name) &&
                             IsUnityNull(Field(component, "m_particleSystemRenderer")))
                    {
                        pj["unavailable"] = "particle system renderer not initialised; reading would add one";
                    }
                    else
                    {
                        pj["value"] = converter.Convert(p.GetValue(component));
                    }
                }
                catch (Exception raw)
                {
                    Exception e = raw;
                    while (e is TargetInvocationException && e.InnerException != null)
                    {
                        e = e.InnerException;
                    }
                    pj.Remove("value");
                    pj["error"] = e.Message;
                }
                props[p.name] = pj;
            }
            entry["properties"] = props;
            return entry;
        }

        /// <summary>
        /// True if the class (or a base below ModBehaviour) declares a non-public settable property of a Component
        /// type, i.e. the "private X m_x { get => (X)base.Component; ... }" wrapper pattern used by the editor.
        /// </summary>
        private static bool UsesComponentWrapper(Type type)
        {
            return s_usesComponentWrapper.GetOrAdd(type, t =>
            {
                for (Type cur = t; cur != null && cur != typeof(ModBehaviour); cur = cur.BaseType)
                {
                    foreach (PropertyInfo p in cur.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (typeof(Component).IsAssignableFrom(p.PropertyType) && p.CanWrite)
                        {
                            return true;
                        }
                    }
                }
                return false;
            });
        }

        private static object Field(ModBehaviour c, string name) => GameReflection.GetField(c, name);

        private static bool IsUnityNull(object o) => o == null || (o is UnityEngine.Object uo && uo == null);

        private static JToken AssetFieldOrGuid(ModBehaviour c, ValueConverter conv, string assetField, string guidField)
        {
            // the cached asset counts only when it is the registered asset (a default instance from the serializer is "none")
            object asset = Field(c, assetField);
            object g = Field(c, guidField);
            return conv.AssetOrGuid(asset as ModAsset, hasGuidFields: true, g is SerializableGuid guid ? guid : SerializableGuid.Empty);
        }

        private static JToken GuidArrayRefs(ModBehaviour c, ValueConverter conv, string guidsField)
        {
            if (!(Field(c, guidsField) is SerializableGuid[] guids))
            {
                return JValue.CreateNull();
            }
            JArray arr = new JArray();
            foreach (SerializableGuid g in guids)
            {
                arr.Add(conv.GuidRef(g));
            }
            return arr;
        }

        private static JToken MaterialsFieldOrRenderer(ModBehaviour c, ValueConverter conv, string cacheField)
        {
            object cached = Field(c, cacheField);
            if (cached is Material[] mats)
            {
                return conv.Convert(mats);
            }
            if (c.m_component is Renderer renderer && renderer != null)
            {
                return conv.Convert(renderer.sharedMaterials); // sharedMaterials does not instantiate materials
            }
            return new JObject { ["unavailable"] = "renderer not initialised" };
        }
    }
}
