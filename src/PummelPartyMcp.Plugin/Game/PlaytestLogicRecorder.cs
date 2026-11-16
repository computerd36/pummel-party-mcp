using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ModSystem.Logic;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// Turns the recording hooks' calls (main thread, PlayMode only) into feed records: "trigger_fired" and "action_executed",
    /// rate-limited by <see cref="PlaytestFeed.TryBeginLogic"/> (data is built only for records that pass). Health/score-affecting
    /// actions are also noted as a cause hint for the diff events. Reads names and guids only; nothing is modified.
    /// </summary>
    internal static class PlaytestLogicRecorder
    {
        public static PlaytestFeed Feed;

        private static readonly FieldInfo s_owner = typeof(ModLogicTrigger).GetField("m_owner", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<int, string> s_actionNames = Names(typeof(ModActionType));
        private static readonly Dictionary<int, string> s_eventNames = Names(typeof(ModEventType));
        private static readonly Dictionary<int, string> s_triggerNames = Names(typeof(ModTriggerType));
        private static ModAction s_lastAction;
        private static ModEvent s_lastEvent;

        /// <summary>The Mod Editor closed: drop the references to the last action and event (their objects are gone).</summary>
        public static void Reset()
        {
            s_lastAction = null;
            s_lastEvent = null;
        }

        private static Dictionary<int, string> Names(Type enumType)
        {
            Dictionary<int, string> d = new Dictionary<int, string>();
            foreach (object v in Enum.GetValues(enumType))
            {
                d[(int)v] = v.ToString();
            }
            return d;
        }

        private static string Name(Dictionary<int, string> table, int value) => table.TryGetValue(value, out string n) ? n : value.ToString();

        private static int IdOf(object o)
        {
            UnityEngine.Object uo = o as UnityEngine.Object;
            return uo != null ? uo.GetInstanceID() : (o == null ? 0 : RuntimeHelpers.GetHashCode(o));
        }

        // ------------------------------------------------------------------ trigger fired

        public static void OnTrigger(ModLogicTrigger trigger, ModEvent modEvent)
        {
            PlaytestFeed feed = Feed;
            if (feed == null || trigger == null || modEvent == null)
            {
                return;
            }
            object owner = s_owner?.GetValue(trigger);
            long key = ((long)IdOf(owner) << 20) ^ ((long)trigger.LocalTriggerIndex << 4) ^ 1L;
            DateTime now = DateTime.UtcNow;
            if (!feed.TryBeginLogic(key, now, out int repeats))
            {
                return;
            }
            JObject d = new JObject();
            Component c = owner as Component;
            if (c != null)
            {
                d["object"] = c.gameObject.name;
                ModTransform mt = c.GetComponent<ModTransform>();
                if (mt != null)
                {
                    d["objectGuid"] = EditorAccess.GuidOf(mt);
                }
                d["component"] = c.GetType().Name;
            }
            d["trigger"] = TriggerSlotName(owner, trigger.LocalTriggerIndex);
            d["triggerType"] = Name(s_triggerNames, (int)trigger.TriggerType);
            d["event"] = Name(s_eventNames, (int)modEvent.EventType);
            d["actions"] = trigger.Actions == null ? 0 : trigger.Actions.Length;
            d["seed"] = modEvent.Seed;
            AddParties(d, modEvent, out SimpleModPlayer who);
            feed.RecordLogic("trigger_fired", now, who == null ? -1 : who.GamePlayer.GlobalID, who?.GamePlayer.Name, d, repeats);
        }

        /// <summary>The slot name in the vocabulary of get_action_graph where it is known from the owner's type.</summary>
        private static string TriggerSlotName(object owner, int index)
        {
            switch (owner == null ? "" : owner.GetType().Name)
            {
                case "ModLogic":
                    return "Triggers[" + index + "]";
                case "ModTrigger":
                    return index == 0 ? "OnHitActions" : index == 1 ? "OnEnterActions" : index == 2 ? "OnStayActions" : index == 3 ? "OnExitActions" : "#" + index;
                case "ModItem":
                    return index == 0 ? "OnPickupTrigger" : index == 1 ? "OnDropTrigger" : index == 2 ? "OnUseTrigger" : index == 3 ? "OnWhileHeldTrigger" : "#" + index;
                case "SimpleModPlayer":
                    return "playerSettings." + (index == 0 ? "hitTrigger" : index == 1 ? "weaponHitTrigger" : index == 2 ? "tickTrigger" : index == 4 ? "onDeathTrigger"
                        : index == 5 ? "onRespawnTrigger" : index == 6 ? "onFirstSpawnTrigger" : index == 7 ? "onRoundEndTrigger" : "#" + index);
                default:
                    return "#" + index;
            }
        }

        // ------------------------------------------------------------------ action executed

        public static void OnAction(ModAction action, ModEvent modEvent)
        {
            PlaytestFeed feed = Feed;
            if (feed == null || action == null || modEvent == null)
            {
                return;
            }
            // Execute overrides call base.Execute: record an (action, event) pair once.
            if (ReferenceEquals(action, s_lastAction) && ReferenceEquals(modEvent, s_lastEvent))
            {
                return;
            }
            s_lastAction = action;
            s_lastEvent = modEvent;
            string type = Name(s_actionNames, (int)action.Type);
            int kind = CauseKind(action.Type);
            if (kind != 0)
            {
                JObject parties = new JObject();
                AddParties(parties, modEvent, out SimpleModPlayer ignored);
                string text = type + " action";
                string recv = (string)parties["receiver"];
                string src = (string)parties["source"];
                if (recv != null || src != null)
                {
                    text += " [" + (recv != null ? "receiver " + recv : "") + (recv != null && src != null ? ", " : "") + (src != null ? "source " + src : "") + "]";
                }
                feed.NoteCauseAction(Time.frameCount, kind, text);
            }
            long key = ((long)(int)action.Type << 40) ^ ((long)IdOf(modEvent.Receiver) << 8) ^ ((long)modEvent.CurrentActionIndex << 2) ^ 2L;
            DateTime now = DateTime.UtcNow;
            if (!feed.TryBeginLogic(key, now, out int repeats))
            {
                return;
            }
            JObject d = new JObject { ["action"] = type, ["index"] = modEvent.CurrentActionIndex, ["seed"] = modEvent.Seed };
            AddParties(d, modEvent, out SimpleModPlayer who);
            feed.RecordLogic("action_executed", now, who == null ? -1 : who.GamePlayer.GlobalID, who?.GamePlayer.Name, d, repeats);
        }

        private static int CauseKind(ModActionType t)
        {
            switch (t)
            {
                case ModActionType.ChangeHealth:
                case ModActionType.Kill:
                case ModActionType.HealPlayer:
                case ModActionType.DamagePlayer:
                    return PlaytestFeed.KindHealth;
                case ModActionType.ChangeScore:
                case ModActionType.SetPlacement:
                    return PlaytestFeed.KindScore;
                default:
                    return 0;
            }
        }

        /// <summary>"source" / "receiver" of the event: "player N" for a player, the object's name otherwise. who = the player involved (source first).</summary>
        private static void AddParties(JObject d, ModEvent e, out SimpleModPlayer who)
        {
            who = null;
            string src = Describe(e.Source, ref who);
            string recv = Describe(e.Receiver, ref who);
            if (src != null)
            {
                d["source"] = src;
            }
            if (recv != null)
            {
                d["receiver"] = recv;
            }
        }

        private static string Describe(IEventReceiver r, ref SimpleModPlayer who)
        {
            SimpleModPlayer sp = r as SimpleModPlayer;
            if (sp != null)
            {
                if (who == null && sp.GamePlayer != null)
                {
                    who = sp;
                }
                return sp.GamePlayer == null ? "player" : "player " + sp.GamePlayer.GlobalID;
            }
            Component c = r as Component;
            return c != null ? c.gameObject.name : null;
        }
    }
}
