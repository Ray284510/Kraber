// Portable v2 protocol core. No NXOpen, Pipe, process observer, or v1 queue access.
// This file is appended outside NX_MCP_Loader by the checked generator.
namespace NxAiLab.Diagnostics
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Web.Script.Serialization;

    public static class ContextProtocol
    {
        public const string Command = "nx_readonly_context";
        public const string SchemaVersion = "p13-context-v1";
        public const int MaxRequestBytes = 4096;
        public const int MaxResponseBytes = 1048576;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private const string UuidPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

        // Same byte/LF framing as v1. Only a detected JSON context frame gains
        // the v2 byte limit. This does not close the caller-owned stream or retry.
        public static byte[] ReadFrame(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            using (MemoryStream bytes = new MemoryStream())
            {
                bool decided = false, context = false;
                int b;
                while ((b = stream.ReadByte()) >= 0)
                {
                    if (b == (byte)'\n') break;
                    bytes.WriteByte((byte)b);
                    if (!decided && b != 32 && b != 9 && b != 13)
                    {
                        decided = true;
                        context = b == (byte)'{';
                    }
                    if (context && bytes.Length > MaxRequestBytes)
                        throw new InvalidDataException("Context request exceeds 4096 bytes");
                }
                return bytes.ToArray();
            }
        }

        public static bool IsCanonicalUuid(string value)
        {
            Guid id;
            return value != null && Regex.IsMatch(value, "\\A" + UuidPattern + "\\z") &&
                Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && id.ToString("D") == value;
        }

        // Deliberately accepts only a tiny JSON grammar. Duplicate keys are rejected
        // before JavaScriptSerializer could collapse them. JSON escapes are not needed
        // for any accepted key/value and are rejected rather than canonicalized.
        public static string ParseRequestUuid(string frame)
        {
            if (frame == null) throw new FormatException("NULL_CONTEXT_FRAME");
            if (StrictUtf8.GetByteCount(frame) > MaxRequestBytes) throw new FormatException("CONTEXT_REQUEST_LIMIT_EXCEEDED");
            Match outer = Regex.Match(frame, "\\A[ \\t\\r\\n]*\\{(?<body>[^{}]*)\\}[ \\t\\r\\n]*\\z");
            if (!outer.Success) throw new FormatException("INVALID_CONTEXT_FRAME");
            string[] members = outer.Groups["body"].Value.Split(',');
            if (members.Length != 3) throw new FormatException("CONTEXT_REQUIRES_EXACTLY_THREE_FIELDS");
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            string pattern = "\\A[ \\t\\r\\n]*\"(?<key>wire_version|command|request_uuid)\"[ \\t\\r\\n]*:[ \\t\\r\\n]*(?<value>2|\"nx_readonly_context\"|\"" + UuidPattern + "\")[ \\t\\r\\n]*\\z";
            foreach (string member in members)
            {
                Match m = Regex.Match(member, pattern);
                if (!m.Success) throw new FormatException("INVALID_CONTEXT_FIELD");
                string key = m.Groups["key"].Value;
                if (values.ContainsKey(key)) throw new FormatException("DUPLICATE_CONTEXT_FIELD");
                values.Add(key, m.Groups["value"].Value);
            }
            if (!values.ContainsKey("wire_version") || values["wire_version"] != "2" ||
                !values.ContainsKey("command") || values["command"] != "\"" + Command + "\"" ||
                !values.ContainsKey("request_uuid")) throw new FormatException("INVALID_CONTEXT_CONTRACT");
            string raw = values["request_uuid"];
            if (raw.Length != 38 || raw[0] != '"' || raw[37] != '"') throw new FormatException("INVALID_CONTEXT_UUID");
            string uuid = raw.Substring(1, 36);
            if (!IsCanonicalUuid(uuid)) throw new FormatException("INVALID_CONTEXT_UUID");
            return uuid;
        }

        private static Dictionary<string, object> Envelope(string uuid, object context, string outcome, bool complete, object error)
        {
            if (!IsCanonicalUuid(uuid)) throw new ArgumentException("Response requires its own canonical UUID", "uuid");
            return new Dictionary<string, object> {
                {"wire_version", 2}, {"schema_version", SchemaVersion}, {"command", Command},
                {"request_uuid", uuid}, {"outcome", outcome}, {"capture_complete", complete},
                {"geometry_acceptance", false}, {"snapshot_atomic", false}, {"context", context},
                {"error", error}, {"response_created_at", DateTimeOffset.UtcNow.ToString("o")},
                {"request_uuid_bound_to_completion_slot", true},
                {"binding_semantics", "SERVER_ASSERTION_CLIENT_MUST_INDEPENDENTLY_VERIFY_UUID_VERSION_COMMAND"}
            };
        }

        private static string SerializeRaw(object value)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaxResponseBytes;
            serializer.RecursionLimit = 64;
            return serializer.Serialize(value);
        }

        public static string ErrorResponse(string uuid, string outcome, string message)
        {
            return TypedErrorResponse(uuid, outcome, "ContextProtocolError", message);
        }

        internal static string TypedErrorResponse(string uuid, string outcome, string type, string message)
        {
            if (message == null) message = "No additional detail";
            // Bound exception detail without silently truncating a successful context.
            bool truncated = message.Length > 2048;
            if (truncated) message = message.Substring(0, 2048);
            return SerializeRaw(Envelope(uuid, null, outcome, false,
                new Dictionary<string, object> { {"type", type}, {"message", message}, {"message_truncated", truncated} }));
        }

        internal static string SerializeResponse(string uuid, IDictionary<string, object> context, string outcome, bool complete)
        {
            if (outcome != "CAPTURE_COMPLETED" && outcome != "CAPTURE_PARTIAL" && outcome != "CAPTURE_FAILED" &&
                outcome != "THREAD_CONTRACT_UNKNOWN") throw new ArgumentException("Invalid capture outcome", "outcome");
            if (context == null) return ErrorResponse(uuid, "CAPTURE_FAILED", "NULL_CONTEXT_PAYLOAD");
            object contextComplete;
            complete = complete && outcome == "CAPTURE_COMPLETED" && context.Count > 0 &&
                context.TryGetValue("capture_complete", out contextComplete) && contextComplete is bool && (bool)contextComplete;
            try
            {
                string result = SerializeRaw(Envelope(uuid, context, outcome, complete, null));
                if (StrictUtf8.GetByteCount(result) + 1 > MaxResponseBytes)
                    return ErrorResponse(uuid, "LIMIT_EXCEEDED", "CONTEXT_RESPONSE_BYTE_LIMIT_EXCEEDED");
                return result;
            }
            catch (InvalidOperationException)
            {
                return ErrorResponse(uuid, "LIMIT_EXCEEDED", "CONTEXT_SERIALIZATION_LIMIT_EXCEEDED");
            }
            catch (ArgumentException e)
            {
                return ErrorResponse(uuid, "CAPTURE_FAILED", "CONTEXT_SERIALIZATION_FAILED: " + e.GetType().FullName);
            }
        }

        // Client-side/offline validation only. A server assertion is not client proof.
        private static bool HasDuplicateRootKeys(string json, JavaScriptSerializer serializer)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            int depth = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    int start = i++;
                    while (i < json.Length)
                    {
                        if (json[i] == '\\') { i += 2; continue; }
                        if (json[i] == '"') break;
                        i++;
                    }
                    if (i >= json.Length) return true; // Invalid string: fail closed.
                    int after = i + 1;
                    while (after < json.Length && Char.IsWhiteSpace(json[after])) after++;
                    if (depth == 1 && after < json.Length && json[after] == ':')
                    {
                        string key = serializer.Deserialize<string>(json.Substring(start, i - start + 1));
                        if (!keys.Add(key)) return true;
                    }
                }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return false;
        }

        public static bool ResponseMatchesRequest(string response, string expectedUuid)
        {
            if (!IsCanonicalUuid(expectedUuid) || response == null) return false;
            try
            {
                if (StrictUtf8.GetByteCount(response) + 1 > MaxResponseBytes) return false;
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaxResponseBytes;
                serializer.RecursionLimit = 64;
                if (HasDuplicateRootKeys(response, serializer)) return false;
                Dictionary<string, object> value = serializer.DeserializeObject(response) as Dictionary<string, object>;
                if (value == null) return false;
                object version, command, uuid;
                return value.TryGetValue("wire_version", out version) && version is int && (int)version == 2 &&
                    value.TryGetValue("command", out command) && command is string && (string)command == Command &&
                    value.TryGetValue("request_uuid", out uuid) && uuid is string && (string)uuid == expectedUuid;
            }
            catch (Exception) { return false; }
        }
    }

    public sealed class ContextRegistry
    {
        public const int MaxLifetimeRequests = 10000;
        private readonly object sync = new object();
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Func<long> clock;
        private readonly int capacity;
        private static readonly Stopwatch LifetimeClock = Stopwatch.StartNew();
        public ContextRegistry() : this(delegate { return LifetimeClock.ElapsedMilliseconds; }, MaxLifetimeRequests) { }
        public ContextRegistry(Func<long> monotonicMilliseconds) : this(monotonicMilliseconds, MaxLifetimeRequests) { }
        public ContextRegistry(Func<long> monotonicMilliseconds, int maxLifetimeRequests)
        {
            if (monotonicMilliseconds == null) throw new ArgumentNullException("monotonicMilliseconds");
            if (maxLifetimeRequests <= 0 || maxLifetimeRequests > MaxLifetimeRequests) throw new ArgumentOutOfRangeException("maxLifetimeRequests");
            clock = monotonicMilliseconds; capacity = maxLifetimeRequests;
        }
        public ContextRequest Accept(string frame, int timeoutMilliseconds)
        {
            string uuid = ContextProtocol.ParseRequestUuid(frame);
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            lock (sync)
            {
                if (consumed.Contains(uuid)) throw new InvalidOperationException("DUPLICATE_CONTEXT_UUID_NO_REPLAY");
                if (consumed.Count >= capacity) throw new InvalidOperationException("CONTEXT_REGISTRY_FULL_FAIL_CLOSED");
                long now = clock();
                if (now < 0 || now > long.MaxValue - timeoutMilliseconds) throw new InvalidOperationException("INVALID_MONOTONIC_CLOCK");
                ContextRequest request = new ContextRequest(uuid, now, now + timeoutMilliseconds, clock);
                consumed.Add(uuid); // Lifecycle tombstone; never removed, including on timeout.
                return request;
            }
        }
    }

    public sealed class ContextRequest
    {
        private readonly object sync = new object();
        private readonly string uuid;
        private readonly Func<long> clock;
        private readonly long deadline;
        private long lastNow;
        private string execution = "QUEUED";
        private string delivery = "OPEN";
        private string response;
        private string rejected;
        private int rejectedCount;
        internal ContextRequest(string requestUuid, long acceptedAt, long deadlineMilliseconds, Func<long> monotonicMilliseconds)
        { uuid = requestUuid; lastNow = acceptedAt; deadline = deadlineMilliseconds; clock = monotonicMilliseconds; }
        public string RequestUuid { get { return uuid; } }
        public string ExecutionState { get { lock (sync) { return execution; } } }
        public string DeliveryState { get { lock (sync) { return delivery; } } }
        public string LastRejectedCompletion { get { lock (sync) { return rejected; } } }
        public int RejectedCompletionCount { get { lock (sync) { return rejectedCount; } } }
        private bool TimedOut()
        {
            long now = clock();
            if (now < lastNow) { delivery = "UNKNOWN_CLOCK"; throw new InvalidOperationException("MONOTONIC_CLOCK_MOVED_BACKWARDS"); }
            lastNow = now;
            return now >= deadline;
        }
        private void Reject(string reason) { rejected = reason; rejectedCount++; }
        public bool TryBegin()
        {
            lock (sync)
            {
                if (execution != "QUEUED" || delivery != "OPEN") return false;
                if (TimedOut()) { execution = "SKIPPED_EXPIRED"; delivery = "EXPIRED"; return false; }
                execution = "EXECUTING"; return true;
            }
        }
        public bool Complete(string responseUuid, IDictionary<string, object> context, string outcome, bool captureComplete)
        {
            return CompleteRendered(responseUuid, delegate { return ContextProtocol.SerializeResponse(uuid, context, outcome, captureComplete); });
        }
        public bool CompleteFailure(string responseUuid, Exception error)
        {
            if (error == null) throw new ArgumentNullException("error");
            return CompleteRendered(responseUuid, delegate { return ContextProtocol.TypedErrorResponse(uuid, "CAPTURE_FAILED", error.GetType().FullName, error.Message); });
        }
        private bool CompleteRendered(string responseUuid, Func<string> render)
        {
            lock (sync)
            {
                if (!String.Equals(responseUuid, uuid, StringComparison.Ordinal)) { Reject("MISMATCHED_OR_MISSING_UUID_ISOLATED"); return false; }
                if (execution != "EXECUTING") { Reject("DUPLICATE_OR_UNSTARTED_COMPLETION_ISOLATED"); return false; }
                execution = "COMPLETED";
                if (delivery != "OPEN" || TimedOut()) { delivery = "EXPIRED"; Reject("STALE_COMPLETION_ISOLATED"); return false; }
                response = render();
                return true;
            }
        }
        public string TakeResponse()
        {
            lock (sync)
            {
                if (delivery != "OPEN" || response == null) return null;
                if (TimedOut()) { delivery = "EXPIRED"; response = null; Reject("STALE_RESPONSE_ISOLATED_BEFORE_DELIVERY"); return null; }
                string result = response; response = null; delivery = "RESPONSE_CLAIMED"; return result;
            }
        }
        public void Expire()
        {
            lock (sync)
            {
                if (delivery != "OPEN") return;
                delivery = "EXPIRED"; response = null;
                if (execution == "QUEUED") execution = "SKIPPED_EXPIRED";
                // EXECUTING is intentionally not cancelled or aborted.
            }
        }
    }

    public sealed class ContextStartupGate
    {
        private int state; // 0 never, 1 initializing, 2 published, 3 failed/unknown.
        public bool TryEnter() { return Interlocked.CompareExchange(ref state, 1, 0) == 0; }
        public void Publish()
        {
            if (Interlocked.CompareExchange(ref state, 2, 1) != 1) throw new InvalidOperationException("STARTUP_NOT_OWNED_INITIALIZING");
        }
        public bool Fail() { return Interlocked.CompareExchange(ref state, 3, 1) == 1; }
        public string State
        {
            get
            {
                int value = Thread.VolatileRead(ref state);
                return value == 0 ? "NEVER_STARTED" : value == 1 ? "INITIALIZING" : value == 2 ? "DISPATCH_CONTEXT_PUBLISHED" : "FAILED_OR_UNKNOWN";
            }
        }
    }
}
