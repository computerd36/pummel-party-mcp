using System.Globalization;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>The wire shape of get_playtest_events (pure, unit-tested).</summary>
    public static class PlaytestEventJson
    {
        public static JObject Result(PlaytestFeed feed, int max, long? sinceId, bool logicEvents, string sessionId)
        {
            PlaytestEventQueryResult q = feed.Query(max, sinceId);
            JArray entries = new JArray();
            foreach (PlaytestEvent e in q.Entries)
            {
                entries.Add(Entry(e));
            }
            return new JObject
            {
                ["entries"] = entries,
                ["nextId"] = q.NextId,
                ["dropped"] = q.Dropped,
                ["running"] = feed.Collecting,
                ["playtest"] = feed.PlaytestSeq,
                ["logicEvents"] = logicEvents,
                ["suppressedLogicEvents"] = feed.LogicSuppressed,
                ["sessionId"] = sessionId,
            };
        }

        public static JObject Entry(PlaytestEvent e)
        {
            JObject j = new JObject
            {
                ["id"] = e.Id,
                ["time"] = e.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["playSeconds"] = e.PlaySeconds,
                ["type"] = e.Type,
            };
            if (e.Player >= 0)
            {
                j["player"] = e.Player;
                if (e.PlayerName != null)
                {
                    j["name"] = e.PlayerName;
                }
            }
            if (e.Data != null)
            {
                foreach (JProperty p in e.Data.Properties())
                {
                    j[p.Name] = p.Value.DeepClone();
                }
            }
            return j;
        }
    }
}
