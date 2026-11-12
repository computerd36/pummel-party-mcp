using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>One asset row prepared for filtering/paging. Unity-free so it can be unit tested.</summary>
    public sealed class AssetEntry
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }

        /// <summary>Extra strings the type filter also matches (class name, internal type string).</summary>
        public string[] TypeAliases { get; set; }

        /// <summary>The JSON item returned to the caller.</summary>
        public JObject Row { get; set; }
    }

    public static class AssetListing
    {
        public const int DefaultLimit = 100;
        public const int MaxLimit = 500;

        /// <summary>Splits a search string into case-insensitive terms (whitespace separated); null if no terms.</summary>
        public static string[] ParseSearch(string search)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return null;
            }
            string[] terms = search.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return terms.Length == 0 ? null : terms;
        }

        public static bool MatchesSearch(string name, string[] terms)
        {
            if (terms == null)
            {
                return true;
            }
            string n = name ?? string.Empty;
            foreach (string t in terms)
            {
                if (n.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        public static bool MatchesType(AssetEntry e, string typeFilter)
        {
            if (typeFilter == null)
            {
                return true;
            }
            if (string.Equals(typeFilter, e.Type, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (e.TypeAliases != null)
            {
                foreach (string a in e.TypeAliases)
                {
                    if (string.Equals(typeFilter, a ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Applies search, then counts types (ignoring the type filter), then the type filter, stable sort
        /// (type, name, guid; ordinal ignore-case) and paging. limit/offset must already be validated.
        /// </summary>
        public static JObject Query(IEnumerable<AssetEntry> entries, string typeFilter, string search, int limit, int offset)
        {
            if (limit < 1 || limit > MaxLimit)
            {
                throw RpcException.InvalidParams($"'limit' must be between 1 and {MaxLimit}");
            }
            if (offset < 0)
            {
                throw RpcException.InvalidParams("'offset' must be >= 0");
            }

            string[] terms = ParseSearch(search);
            List<AssetEntry> searched = entries.Where(e => MatchesSearch(e.Name, terms)).ToList();

            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetEntry e in searched)
            {
                string t = e.Type ?? string.Empty;
                counts.TryGetValue(t, out int c);
                counts[t] = c + 1;
            }
            JObject typeCounts = new JObject();
            foreach (KeyValuePair<string, int> kv in counts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                typeCounts[kv.Key] = kv.Value;
            }

            List<AssetEntry> filtered = searched
                .Where(e => MatchesType(e, typeFilter))
                .OrderBy(e => e.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Guid ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int total = filtered.Count;
            List<JObject> page = filtered.Skip(offset).Take(limit).Select(e => e.Row).ToList();
            return new JObject
            {
                ["total"] = total,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["hasMore"] = (long)offset + page.Count < total,
                ["typeCounts"] = typeCounts,
                ["assets"] = new JArray(page),
            };
        }
    }
}
