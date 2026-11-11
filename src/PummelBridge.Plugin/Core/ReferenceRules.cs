using System;
using System.Collections.Generic;
using System.Linq;

namespace PummelBridge.Core
{
    /// <summary>What the property (or action target) expects, as far as the reference rules care.</summary>
    public enum ExpectedRefKind
    {
        GameObject,
        Transform,
        /// <summary>Any other type (a component type, an asset type, Component, UnityEngine.Object ...).</summary>
        Other,
    }

    /// <summary>One mod component of a referenced object (Unity-free view for <see cref="ReferenceRules"/>).</summary>
    public sealed class RefComponent
    {
        public string TypeName;
        public string Guid;
        /// <summary>The component's class or its wrapped Unity component is an instance of the expected type.</summary>
        public bool Matches;
    }

    public enum RefSource
    {
        /// <summary>Use the registered asset with the requested guid (component guid, asset guid, or the object itself).</summary>
        Asset,
        /// <summary>Use the object's component at <see cref="RefDecision.ComponentIndex"/>.</summary>
        ObjectComponent,
    }

    public struct RefDecision
    {
        public RefSource Source;
        public int ComponentIndex;
    }

    /// <summary>
    /// How a reference guid is resolved (game side: Game/ValueBuilder.cs ReferenceResolver). The editor registers every
    /// ModBehaviour as a runtime asset, including each object's ModTransform (asset guid = the object guid, value = its
    /// Transform). So an object guid finds an asset; if the expected type is neither GameObject nor Transform and the
    /// Transform does not fit it, the object's components are searched instead, like dropping a hierarchy object on the
    /// field: exactly one matching component is used; none or several are refused with the candidates listed.
    /// </summary>
    public static class ReferenceRules
    {
        /// <param name="guid">Requested guid (normalised).</param>
        /// <param name="registeredAsset">An asset with this guid is registered.</param>
        /// <param name="assetObjectGuid">If the asset's value is the Transform of a mod object: that object's ModTransform guid; else null.</param>
        /// <param name="foundObjectName">Name of the object with this ModTransform guid (asset or hierarchy lookup), or null when there is none.</param>
        /// <param name="expected">Kind of the expected type.</param>
        /// <param name="transformFits">The object's Transform is an instance of the expected type.</param>
        /// <param name="components">The object's mod components (only called for an object guid).</param>
        /// <param name="expectedName">Expected type name for messages.</param>
        /// <param name="path">Property path for messages.</param>
        public static RefDecision Decide(string guid, bool registeredAsset, string assetObjectGuid, string foundObjectName,
            ExpectedRefKind expected, bool transformFits, Func<IList<RefComponent>> components, string expectedName, string path)
        {
            bool isObjectGuid = foundObjectName != null && (!registeredAsset || string.Equals(assetObjectGuid, guid, StringComparison.OrdinalIgnoreCase));
            if (registeredAsset && !isObjectGuid)
            {
                return new RefDecision { Source = RefSource.Asset }; // component guid or a real asset: type-checked by the caller
            }
            if (!isObjectGuid)
            {
                throw RpcException.InvalidParams($"'{path}': no asset or object with guid {guid} (see list_assets / get_hierarchy)");
            }
            if (registeredAsset && (expected != ExpectedRefKind.Other || transformFits))
            {
                return new RefDecision { Source = RefSource.Asset }; // the object itself (Transform, or its GameObject)
            }
            IList<RefComponent> comps = components?.Invoke() ?? new RefComponent[0];
            List<int> hits = new List<int>();
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i].Matches)
                {
                    hits.Add(i);
                }
            }
            if (hits.Count == 1)
            {
                return new RefDecision { Source = RefSource.ObjectComponent, ComponentIndex = hits[0] };
            }
            if (hits.Count == 0)
            {
                string list = comps.Count == 0 ? "none" : string.Join(", ", comps.Select(c => $"{c.TypeName} {c.Guid}").ToArray());
                throw RpcException.InvalidParams($"'{path}': object '{foundObjectName}' has no component of type {expectedName} (its components: {list})");
            }
            string matching = string.Join(", ", hits.Select(i => $"{comps[i].TypeName} {comps[i].Guid}").ToArray());
            throw RpcException.InvalidParams(
                $"'{path}': object '{foundObjectName}' has {hits.Count} components of type {expectedName} ({matching}); pass the guid of the component you mean");
        }
    }
}
