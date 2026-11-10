using System;
using System.Collections;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;
using UnityEngine;

namespace PummelBridge.Tools
{
    /// <summary>get_log: the bridge's own ring buffer (fed by Application.logMessageReceivedThreaded).</summary>
    internal static class LogTools
    {
        public static void Register(ToolRegistry registry, LogRingBuffer ring, BridgeRuntimeInfo info)
        {
            registry.Register(ToolDefinition.Read("get_log", ctx =>
            {
                int max = ctx.GetInt("max", 200, 1, ring.Capacity);
                long? sinceId = ctx.GetLong("sinceId", 0, long.MaxValue);
                bool includeEditor = ctx.GetBool("includeEditor", false);

                // The ring buffer is thread-safe, so this part runs on the HTTP thread (works while the game is busy).
                LogQueryResult q = ring.Query(max, sinceId);
                JArray entries = new JArray();
                foreach (LogEntry e in q.Entries)
                {
                    JObject j = new JObject
                    {
                        ["id"] = e.Id,
                        ["time"] = e.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                        ["level"] = e.Level,
                        ["message"] = e.Message,
                        ["source"] = "unity",
                    };
                    if (e.Stack != null)
                    {
                        j["stack"] = e.Stack;
                    }
                    entries.Add(j);
                }
                JObject result = new JObject
                {
                    ["entries"] = entries,
                    ["nextId"] = q.NextId,
                    ["dropped"] = q.Dropped,
                    ["sessionId"] = info.SessionId,
                };
                if (includeEditor)
                {
                    result["editorEntries"] = ctx.OnMainThread(ReadEditorConsole);
                }
                return result;
            }));
        }

        public static string LevelName(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "error";
                case LogType.Assert: return "assert";
                case LogType.Warning: return "warning";
                case LogType.Exception: return "exception";
                default: return "info";
            }
        }

        /// <summary>
        /// The editor's Log window reads the static lists of the game's global "Console" class (last ~100 Unity
        /// log messages since the window's Clear button). Those lists are mutated on the main thread only.
        /// </summary>
        private static JArray ReadEditorConsole()
        {
            Type console = GameReflection.FindGameType("Console");
            if (console == null)
            {
                throw RpcException.Internal(GameReflection.MissingMessage(null, "Console"));
            }
            IList messages = GameReflection.GetStaticField(console, "entriesClean") as IList;
            IList types = GameReflection.GetStaticField(console, "entryTypes") as IList;
            IList stacks = GameReflection.GetStaticField(console, "stackTraces") as IList;
            JArray arr = new JArray();
            if (messages == null)
            {
                return arr;
            }
            for (int i = 0; i < messages.Count; i++)
            {
                LogType lt = types != null && i < types.Count && types[i] is LogType t ? t : LogType.Log;
                JObject j = new JObject
                {
                    ["index"] = i,
                    ["level"] = LevelName(lt),
                    ["message"] = messages[i] as string,
                    ["source"] = "editor",
                };
                string stack = stacks != null && i < stacks.Count ? stacks[i] as string : null;
                if (!string.IsNullOrEmpty(stack) && lt != LogType.Log && lt != LogType.Warning)
                {
                    j["stack"] = stack.Length > LogRingBuffer.MaxStackLength ? stack.Substring(0, LogRingBuffer.MaxStackLength) : stack;
                }
                arr.Add(j);
            }
            return arr;
        }
    }
}
