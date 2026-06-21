using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Telemetry
{
    /// <summary>
    /// An immutable telemetry record: a named event with a UTC timestamp, the session it
    /// belongs to, and an optional flat bag of properties. Created by
    /// <see cref="TelemetrySubsystem.Track(string, System.Collections.Generic.IReadOnlyDictionary{string, object})"/>
    /// and handed to each <see cref="ITelemetrySink"/>.
    /// </summary>
    public sealed class TelemetryEvent
    {
        /// <summary>Event name, e.g. <c>"sequence.step_completed"</c>.</summary>
        public string Name { get; }

        /// <summary>Identifier for the app session this event was recorded in.</summary>
        public string SessionId { get; }

        /// <summary>UTC time the event was recorded.</summary>
        public DateTime TimestampUtc { get; }

        /// <summary>Arbitrary event properties. Never null (empty when none were supplied).</summary>
        public IReadOnlyDictionary<string, object> Properties { get; }

        /// <summary>
        /// Creates a telemetry event.
        /// </summary>
        /// <param name="name">Event name. Required.</param>
        /// <param name="sessionId">Owning session id.</param>
        /// <param name="properties">Optional property bag; copied defensively.</param>
        public TelemetryEvent(string name, string sessionId, IReadOnlyDictionary<string, object> properties = null)
        {
            Name = name;
            SessionId = sessionId;
            TimestampUtc = DateTime.UtcNow;
            Properties = properties != null
                ? new Dictionary<string, object>((IDictionary<string, object>)properties)
                : EmptyProperties;
        }

        private static readonly IReadOnlyDictionary<string, object> EmptyProperties =
            new Dictionary<string, object>();

        /// <summary>
        /// Serializes this event to a single-line JSON object. Reserved keys
        /// (<c>name</c>, <c>sessionId</c>, <c>ts</c>) are written first; properties follow.
        /// A property that collides with a reserved key is namespaced under <c>prop_</c>.
        /// </summary>
        public string ToJson()
        {
            var o = new JObject
            {
                ["name"] = Name,
                ["sessionId"] = SessionId,
                ["ts"] = TimestampUtc.ToString("O"),
            };

            foreach (var kv in Properties)
            {
                var key = (kv.Key == "name" || kv.Key == "sessionId" || kv.Key == "ts")
                    ? "prop_" + kv.Key
                    : kv.Key;
                o[key] = kv.Value == null ? JValue.CreateNull() : JToken.FromObject(kv.Value);
            }

            return o.ToString(Formatting.None);
        }
    }
}
