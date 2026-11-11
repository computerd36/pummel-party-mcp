using System;
using System.Collections.Concurrent;
using System.Reflection;
using PummelBridge.Core;

namespace PummelBridge.Game
{
    /// <summary>
    /// Cached reflection over game internals. A missing member (game update) produces a one-time warning and,
    /// where the datum is required, an INTERNAL error naming the member, so only the affected tool fails.
    /// </summary>
    internal static class GameReflection
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Cache key without string concatenation (this is looked up every frame by the undo watcher).</summary>
        private struct FieldKey : IEquatable<FieldKey>
        {
            private readonly Type m_type;
            private readonly string m_name;

            public FieldKey(Type type, string name)
            {
                m_type = type;
                m_name = name;
            }

            public bool Equals(FieldKey other) => ReferenceEquals(m_type, other.m_type) && string.Equals(m_name, other.m_name, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is FieldKey k && Equals(k);

            public override int GetHashCode() => (m_type == null ? 0 : m_type.GetHashCode() * 397) ^ (m_name == null ? 0 : m_name.GetHashCode());
        }

        private static readonly ConcurrentDictionary<FieldKey, FieldInfo> s_fields = new ConcurrentDictionary<FieldKey, FieldInfo>();
        private static readonly ConcurrentDictionary<string, bool> s_warned = new ConcurrentDictionary<string, bool>();

        public static IBridgeLog Log = NullBridgeLog.Instance;

        public static string MissingMessage(Type type, string member) =>
            $"member {type?.Name ?? "?"}.{member} not found — game version changed?";

        /// <summary>Instance or static field declared on <paramref name="type"/> or a base type; null if missing.</summary>
        public static FieldInfo FindField(Type type, string name)
        {
            FieldKey key = new FieldKey(type, name);
            if (!s_fields.TryGetValue(key, out FieldInfo cached))
            {
                cached = LookupField(type, name);
                s_fields[key] = cached;
            }
            if (cached == null)
            {
                WarnOnce(type.FullName + "::" + name, MissingMessage(type, name)); // only on the (rare) failure path
            }
            return cached;
        }

        private static FieldInfo LookupField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, AnyInstance | AnyStatic | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    return f;
                }
            }
            return null;
        }

        public static FieldInfo RequireField(Type type, string name)
        {
            FieldInfo f = FindField(type, name);
            if (f == null)
            {
                throw RpcException.Internal(MissingMessage(type, name));
            }
            return f;
        }

        public static object GetField(object instance, string name)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }
            return RequireField(instance.GetType(), name).GetValue(instance);
        }

        public static object GetStaticField(Type type, string name) => RequireField(type, name).GetValue(null);

        /// <summary>Looks up a type in Assembly-CSharp by full name; null (and a warning) if missing.</summary>
        public static Type FindGameType(string fullName)
        {
            Type t = typeof(EditorSystem).Assembly.GetType(fullName, false);
            if (t == null)
            {
                WarnOnce("type::" + fullName, $"type {fullName} not found — game version changed?");
            }
            return t;
        }

        public static void WarnOnce(string key, string message)
        {
            if (s_warned.TryAdd(key, true))
            {
                Log.Warning(message);
            }
        }
    }
}
