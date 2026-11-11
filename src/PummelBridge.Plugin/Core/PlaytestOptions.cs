using System;
using Newtonsoft.Json.Linq;

namespace PummelBridge.Core
{
    /// <summary>
    /// The editor toolbar's player-count dropdown (ToolbarWidget.m_playerCount, index = m_playerCountChoice) and what its
    /// Draw() writes to GameManager every frame for each index (transcribed; pure, unit-tested).
    /// </summary>
    public static class PlaytestOptions
    {
        /// <summary>Dropdown labels in index order (ToolbarWidget.m_playerCount).</summary>
        public static readonly string[] Labels =
        {
            "2 Players", "3 Players", "4 Players", "5 Players", "6 Players", "7 Players", "8 Players",
            "2v2 Teams", "2v2v2 Teams", "2v2v2v2 Teams", "3v3 Teams", "4v4 Teams",
        };

        /// <summary>Short team names accepted besides the labels (index 7..11).</summary>
        private static readonly string[] TeamShort = { "2v2", "2v2v2", "2v2v2v2", "3v3", "4v4" };

        /// <summary>The toolbar's default (PlayerPrefs "ModEditorPlayerCountChoice" default 2 = "4 Players").</summary>
        public const int DefaultChoice = 2;

        public sealed class Choice
        {
            public int Index;
            public string Label;
            /// <summary>GameManager.TeamMode: 0 solo, 1 teams.</summary>
            public int TeamMode;
            /// <summary>GameManager.NumTestPlayers.</summary>
            public int NumTestPlayers;
            /// <summary>GameManager.TestPlayersTeamComposition for team choices; null for solo choices (the toolbar leaves it unchanged).</summary>
            public int[] TeamComposition;
        }

        /// <summary>Exactly the assignments of ToolbarWidget.Draw for a choice index (0..11).</summary>
        public static Choice Describe(int index)
        {
            if (index < 0 || index >= Labels.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            Choice c = new Choice { Index = index, Label = Labels[index] };
            if (index <= 6)
            {
                c.TeamMode = 0;
                c.NumTestPlayers = index + 2;
                return c;
            }
            c.TeamMode = 1;
            switch (index)
            {
                case 7: c.NumTestPlayers = 4; c.TeamComposition = new[] { 0, 0, 1, 1 }; break;
                case 8: c.NumTestPlayers = 6; c.TeamComposition = new[] { 0, 0, 1, 1, 2, 2 }; break;
                case 9: c.NumTestPlayers = 8; c.TeamComposition = new[] { 0, 0, 1, 1, 2, 2, 3, 3 }; break;
                case 10: c.NumTestPlayers = 6; c.TeamComposition = new[] { 0, 0, 0, 1, 1, 1 }; break;
                default: c.NumTestPlayers = 8; c.TeamComposition = new[] { 0, 0, 0, 0, 1, 1, 1, 1 }; break;
            }
            return c;
        }

        /// <summary>
        /// Parses the <c>players</c> param: an integer 2..8 (solo), a dropdown label ("4 Players", "2v2 Teams"; case-insensitive),
        /// a number as string ("4"), or a team short name ("2v2", "2v2v2", "2v2v2v2", "3v3", "4v4"). Null/absent = null (keep
        /// the toolbar's current choice). Throws INVALID_PARAMS listing the valid values.
        /// </summary>
        public static int? ParsePlayers(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }
            if (t.Type == JTokenType.Integer || (t.Type == JTokenType.Float && Math.Abs((double)t - Math.Round((double)t)) < 1e-9))
            {
                double v = (double)t;
                if (v >= 2 && v <= 8)
                {
                    return (int)v - 2;
                }
                throw Invalid(t.ToString());
            }
            if (t.Type != JTokenType.String)
            {
                throw Invalid(t.ToString());
            }
            string s = ((string)t).Trim();
            for (int i = 0; i < Labels.Length; i++)
            {
                if (string.Equals(s, Labels[i], StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            for (int i = 0; i < TeamShort.Length; i++)
            {
                if (string.Equals(s, TeamShort[i], StringComparison.OrdinalIgnoreCase))
                {
                    return 7 + i;
                }
            }
            if (s.Length == 1 && s[0] >= '2' && s[0] <= '8')
            {
                return s[0] - '2';
            }
            throw Invalid("\"" + BatchRules.Truncate(s, 40) + "\"");
        }

        private static RpcException Invalid(string got) => RpcException.InvalidParams(
            $"'players' must be 2-8 or one of the toolbar's choices: {string.Join(", ", Labels)} (also \"2v2\", \"2v2v2\", \"2v2v2v2\", \"3v3\", \"4v4\"); got {got}");

        public static JObject ToJson(Choice c, bool aiOnly)
        {
            return new JObject
            {
                ["players"] = c.Label,
                ["playerCount"] = c.NumTestPlayers,
                ["teams"] = c.TeamComposition == null ? JValue.CreateNull() : new JArray(c.TeamComposition),
                ["aiOnly"] = aiOnly,
            };
        }
    }
}
