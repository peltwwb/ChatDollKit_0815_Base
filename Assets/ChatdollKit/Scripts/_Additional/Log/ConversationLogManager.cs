using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ChatdollKit.Additional
{
    /// <summary>
    /// Handles persistent storage of conversation logs capped to 1 MB.
    /// Stores each turn (user input + character response) grouped by date.
    /// </summary>
    [DisallowMultipleComponent]
    public class ConversationLogManager : MonoBehaviour
    {
        private const int MaxLogBytes = 1024 * 1024; // 1 MB
        private const string DefaultFileName = "conversation-log.json";

        [Tooltip("Filename used under Application.persistentDataPath.")]
        [SerializeField] private string fileName = DefaultFileName;
        [Tooltip("When enabled the day grouping uses local time. Disable to group by UTC.")]
        [SerializeField] private bool useLocalTimeForGrouping = true;

        private readonly object storageLock = new object();
        private ConversationLogStorage storage = new ConversationLogStorage();
        private string filePath;

        public event Action LogsUpdated;

        private void Awake()
        {
            filePath = Path.Combine(Application.persistentDataPath, fileName);
            LoadFromDisk();
        }

        /// <summary>
        /// Adds a new turn to the log (user text + character response).
        /// </summary>
        public void LogTurn(string userText, string characterText)
        {
            var sanitizedUser = Sanitize(userText);
            var sanitizedCharacter = Sanitize(characterText);

            if (string.IsNullOrWhiteSpace(sanitizedUser) && string.IsNullOrWhiteSpace(sanitizedCharacter))
            {
                return;
            }

            lock (storageLock)
            {
                var timestamp = DateTimeOffset.Now;
                var dayKey = useLocalTimeForGrouping
                    ? timestamp.LocalDateTime.ToString("yyyy-MM-dd")
                    : timestamp.UtcDateTime.ToString("yyyy-MM-dd");

                var day = storage.Days.FirstOrDefault(d => d.Date == dayKey);
                if (day == null)
                {
                    day = new ConversationLogDayRecord { Date = dayKey };
                    storage.Days.Add(day);
                    storage.Days = storage.Days
                        .OrderBy(d => d.Date, StringComparer.Ordinal)
                        .ToList();
                }

                day.Turns.Add(new ConversationLogTurnRecord
                {
                    Timestamp = timestamp.ToString("o"),
                    UserText = sanitizedUser,
                    CharacterText = sanitizedCharacter
                });

                TrimToSizeLimit();
                SaveToDisk();
            }

            LogsUpdated?.Invoke();
        }

        /// <summary>
        /// Provides a snapshot copy for UI rendering without exposing internal storage.
        /// </summary>
        public IReadOnlyList<ConversationLogDaySnapshot> GetSnapshot(bool newestFirst = true)
        {
            lock (storageLock)
            {
                var days = newestFirst
                    ? storage.Days.OrderByDescending(d => d.Date, StringComparer.Ordinal)
                    : storage.Days.OrderBy(d => d.Date, StringComparer.Ordinal);

                return days
                    .Select(day => new ConversationLogDaySnapshot(
                        day.Date,
                        day.Turns.Select(t => new ConversationLogTurnSnapshot(t.Timestamp, t.UserText, t.CharacterText)).ToList()))
                    .ToList();
            }
        }

        /// <summary>
        /// Deletes the log file and clears memory. Useful for debug UI.
        /// </summary>
        public void ClearLogs()
        {
            lock (storageLock)
            {
                storage = new ConversationLogStorage();
                SaveToDisk();
            }
            LogsUpdated?.Invoke();
        }

        private void LoadFromDisk()
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(json))
                    {
                        storage = JsonUtility.FromJson<ConversationLogStorage>(json) ?? new ConversationLogStorage();
                        EnsureCollections();
                    }
                    else
                    {
                        storage = new ConversationLogStorage();
                    }
                }
                else
                {
                    storage = new ConversationLogStorage();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ConversationLogManager] Failed to load logs: {ex.Message}");
                storage = new ConversationLogStorage();
            }

            EnsureCollections();
        }

        private void SaveToDisk()
        {
            try
            {
                EnsureCollections();

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonUtility.ToJson(storage, false);
                File.WriteAllText(filePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ConversationLogManager] Failed to save logs: {ex.Message}");
            }
        }

        private void TrimToSizeLimit()
        {
            // Quick exit when storage fits comfortably within the limit.
            var currentBytes = EstimateSize();
            if (currentBytes <= MaxLogBytes)
            {
                return;
            }

            // Remove the oldest turns until we fit in 1 MB.
            while (storage.Days.Count > 0 && currentBytes > MaxLogBytes)
            {
                var oldestDay = storage.Days[0];
                if (oldestDay.Turns.Count > 0)
                {
                    oldestDay.Turns.RemoveAt(0);
                }
                if (oldestDay.Turns.Count == 0)
                {
                    storage.Days.RemoveAt(0);
                }
                currentBytes = EstimateSize();
            }
        }

        private int EstimateSize()
        {
            EnsureCollections();
            var json = JsonUtility.ToJson(storage, false);
            return Encoding.UTF8.GetByteCount(json);
        }

        private void EnsureCollections()
        {
            if (storage == null)
            {
                storage = new ConversationLogStorage();
            }

            if (storage.Days == null)
            {
                storage.Days = new List<ConversationLogDayRecord>();
            }

            foreach (var day in storage.Days)
            {
                if (day.Turns == null)
                {
                    day.Turns = new List<ConversationLogTurnRecord>();
                }
            }
        }

        private static string Sanitize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Remove animation / expression tags like [face:Joy] or [anim:waving].
            var withoutSquareTags = Regex.Replace(text, @"\[[^\]]+\]", string.Empty);
            // Remove simple <think> or other markup tags.
            var withoutAngleTags = Regex.Replace(withoutSquareTags, @"<[^>]+>", string.Empty);
            var normalized = withoutAngleTags.Replace("\r", string.Empty);
            var collapsedSpaces = Regex.Replace(normalized, @"[ \t]{2,}", " ");
            var cleaned = Regex.Replace(collapsedSpaces, @"\n{3,}", "\n\n");
            return cleaned.Trim();
        }
    }

    [Serializable]
    internal class ConversationLogStorage
    {
        public List<ConversationLogDayRecord> Days = new List<ConversationLogDayRecord>();
    }

    [Serializable]
    internal class ConversationLogDayRecord
    {
        public string Date;
        public List<ConversationLogTurnRecord> Turns = new List<ConversationLogTurnRecord>();
    }

    [Serializable]
    internal class ConversationLogTurnRecord
    {
        public string Timestamp;
        public string UserText;
        public string CharacterText;
    }

    public readonly struct ConversationLogDaySnapshot
    {
        public readonly string Date;
        public readonly IReadOnlyList<ConversationLogTurnSnapshot> Turns;

        public ConversationLogDaySnapshot(string date, IReadOnlyList<ConversationLogTurnSnapshot> turns)
        {
            Date = date;
            Turns = turns;
        }
    }

    public readonly struct ConversationLogTurnSnapshot
    {
        public readonly string TimestampIso;
        public readonly string UserText;
        public readonly string CharacterText;

        public ConversationLogTurnSnapshot(string timestampIso, string userText, string characterText)
        {
            TimestampIso = timestampIso;
            UserText = userText;
            CharacterText = characterText;
        }

        public DateTimeOffset GetTimestamp()
        {
            if (DateTimeOffset.TryParse(TimestampIso, out var value))
            {
                return value;
            }
            return DateTimeOffset.Now;
        }
    }
}
