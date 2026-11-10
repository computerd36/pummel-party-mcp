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

        private static readonly ConcurrentDictionary<string, FieldInfo> s_fields = new ConcurrentDictionary<string, FieldInfo>();
        private static readonly ConcurrentDictionary<string, bool> s_warned = new ConcurrentDictionary<string, bool>();

        public static IBridgeLog Log = NullBridgeLog.Instance;

        public static string MissingMessage(Type type, string member) =>
            $"member {type?.Name ?? "?"}.{member} not found — game version changed?";

        /// <summary>Instance or static field declared on <paramref name="type"/> or a base type; null if missing.</summary>
        public static FieldInfo FindField(Type type, string name)
        {
            string key = type.FullName + "::" + name;
            FieldInfo cached = s_fields.GetOrAdd(key, _ =>
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
            });
            if (cached == null)
            {
                WarnOnce(key, MissingMessage(type, name));
            }
            return cached;
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
