using System;
using System.Collections.Generic;

namespace PummelBridge.Core
{
    public sealed class LogEntry
    {
        public long Id;
        public DateTime TimeUtc;
        public string Level;
        public string Message;
        public string Stack;
    }

    public sealed class LogQueryResult
    {
        public List<LogEntry> Entries;
        public long NextId;
        /// <summary>True if entries after sinceId were overwritten before they could be read.</summary>
        public bool Dropped;
    }

    /// <summary>Thread-safe fixed-size ring of log entries with monotonically increasing ids (first id = 1).</summary>
    public sealed class LogRingBuffer
    {
        public const int MaxMessageLength = 8000;
        public const int MaxStackLength = 4000;

        private readonly LogEntry[] m_buffer;
        private readonly object m_lock = new object();
        private long m_lastId;
        private int m_count;

        public LogRingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            m_buffer = new LogEntry[capacity];
        }

        public int Capacity => m_buffer.Length;

        public void Add(string level, string message, string stack)
        {
            LogEntry entry = new LogEntry
            {
                TimeUtc = DateTime.UtcNow,
                Level = level,
                Message = Truncate(message, MaxMessageLength),
                Stack = string.IsNullOrEmpty(stack) ? null : Truncate(stack, MaxStackLength),
            };
            lock (m_lock)
            {
                entry.Id = ++m_lastId;
                m_buffer[(int)((entry.Id - 1) % m_buffer.Length)] = entry;
                if (m_count < m_buffer.Length)
                {
                    m_count++;
                }
            }
        }

        /// <summary>
        /// Without sinceId: the newest <paramref name="max"/> entries. With sinceId: entries with id &gt; sinceId,
        /// oldest first, at most <paramref name="max"/>. NextId is the value to pass as sinceId next time.
        /// </summary>
        public LogQueryResult Query(int max, long? sinceId)
        {
            List<LogEntry> result = new List<LogEntry>();
            lock (m_lock)
            {
                long oldestId = m_lastId - m_count + 1;
                long from;
                bool dropped = false;
                if (sinceId.HasValue)
                {
                    from = Math.Max(sinceId.Value + 1, oldestId);
                    dropped = sinceId.Value + 1 < oldestId && m_count > 0;
                }
                else
                {
                    from = Math.Max(oldestId, m_lastId - max + 1);
                }
                for (long id = from; id <= m_lastId && result.Count < max; id++)
                {
                    result.Add(m_buffer[(int)((id - 1) % m_buffer.Length)]);
                }
                long nextId = result.Count > 0 ? result[result.Count - 1].Id : (sinceId.HasValue ? Math.Min(sinceId.Value, m_lastId) : m_lastId);
                return new LogQueryResult { Entries = result, NextId = nextId, Dropped = dropped };
            }
        }

        private static string Truncate(string s, int max)
        {
            if (s == null)
            {
                return string.Empty;
            }
            return s.Length <= max ? s : s.Substring(0, max) + "...[truncated]";
        }
    }
}
