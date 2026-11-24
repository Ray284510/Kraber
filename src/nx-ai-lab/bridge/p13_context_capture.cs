// Portable capture policy. NX getters are supplied by the narrow Loader adapter.
// This file performs no NX, process, pipe, or filesystem operations itself.
namespace NxAiLab.Diagnostics
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    public interface IContextBackend
    {
        int GetProcessId();
        string GetAssemblyLocation();
        string GetAssemblyMvid();
        string GetAssemblyVersion();
        string GetAssemblyDiskSha256();
        object GetCachedSession();
        object GetFreshSession();
        object GetWorkPart(object session);
        object GetDisplayPart(object session);
        object[] GetLoadedParts(object session);
        string GetPartPath(object part);
        string GetPartTag(object part);
        bool GetPartModified(object part);
        bool GetPartReadOnly(object part);
        bool GetPartFullyLoaded(object part);
        int GetUfLoadedCount();
        string GetUfNthTag(int index);
        string GetUfWorkTag();
        string GetUfDisplayTag();
    }

    public static class ContextCapture
    {
        public const int MaximumParts = 10000;

        private sealed class State
        {
            public bool Complete = true;
        }

        private static string Now()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        public static Dictionary<string, object> Receipt(object value, string source, string status, Exception error)
        {
            object detail = error == null ? null : (object)new Dictionary<string, object> {
                { "type", error.GetType().FullName }, { "message", error.Message }
            };
            return new Dictionary<string, object> {
                { "value", value }, { "source", source }, { "status", status },
                { "error", detail }, { "capture_timestamp", Now() }
            };
        }

        private static Dictionary<string, object> Read(State state, string source, Func<object> getter)
        {
            try { return Receipt(getter(), source, "CAPTURED", null); }
            catch (Exception ex) { state.Complete = false; return Receipt(null, source, "ERROR", ex); }
        }

        private static bool Good(Dictionary<string, object> field)
        {
            return (string)field["status"] == "CAPTURED";
        }

        private static string RequiredText(string value, string label)
        {
            if (String.IsNullOrEmpty(value)) throw new InvalidDataException(label + " returned no value");
            return value;
        }

        private static Dictionary<string, object> Part(State state, IContextBackend b, object part)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            var path = Read(state, "BasePart.FullPath", delegate { return b.GetPartPath(part); });
            if (Good(path) && String.IsNullOrEmpty(path["value"] as string))
            {
                path["status"] = "EMPTY_PATH_RETURNED";
                state.Complete = false;
            }
            row["path"] = path;
            row["tag"] = Read(state, "BasePart inherited TaggedObject.Tag", delegate { return RequiredText(b.GetPartTag(part), "Part.Tag"); });
            row["uf_modified"] = Read(state, "UF_PART_is_modified(part.Tag): raw boolean", delegate { return b.GetPartModified(part); });
            row["is_read_only"] = Read(state, "BasePart.IsReadOnly", delegate { return b.GetPartReadOnly(part); });
            row["is_fully_loaded"] = Read(state, "BasePart.IsFullyLoaded", delegate { return b.GetPartFullyLoaded(part); });
            return row;
        }

        private static Dictionary<string, object> Role(State state, IContextBackend b, object fresh, bool isWork)
        {
            string role = isWork ? "Work" : "Display";
            if (fresh == null) return Receipt(null, "fresh Session.Parts." + role, "NOT_ATTEMPTED_NO_FRESH_SESSION", null);
            var getter = Read(state, "fresh Session.Parts." + role, delegate { return isWork ? b.GetWorkPart(fresh) : b.GetDisplayPart(fresh); });
            if (!Good(getter)) return getter;
            object part = getter["value"];
            if (part == null) return Receipt(null, "fresh Session.Parts." + role, isWork ? "NO_ACTIVE_WORK_PART" : "NO_ACTIVE_DISPLAY_PART", null);
            // Each property is read once and independently. In particular, error handling
            // never reads FullPath again and never discards another successful property.
            return Receipt(Part(state, b, part), "fresh Session.Parts." + role, "CAPTURED", null);
        }

        private static Dictionary<string, object> UfTag(State state, string source, Func<string> getter)
        {
            var result = Read(state, source, delegate { return getter(); });
            if (Good(result) && result["value"] == null) result["status"] = "NULL_TAG_RETURNED";
            else if (Good(result) && (string)result["value"] == "")
            {
                state.Complete = false;
                result["status"] = "INVALID_EMPTY_TAG";
            }
            return result;
        }

        private static Dictionary<string, object> ModifiedSummary(List<Dictionary<string, object>> rows, bool complete)
        {
            if (complete && rows.Count == 0)
                return Receipt(null, "aggregate of captured loaded member UF flags", "NOT_APPLICABLE_EMPTY_COLLECTION", null);
            bool known = complete;
            bool any = false;
            foreach (var row in rows)
            {
                var tag = (Dictionary<string, object>)row["tag"];
                var modified = (Dictionary<string, object>)row["uf_modified"];
                if (!Good(tag) || !Good(modified)) known = false;
                else any |= (bool)modified["value"];
            }
            if (!known)
                return Receipt(null, "aggregate of captured loaded member UF flags", "UNKNOWN_PARTIAL_COLLECTION_OR_FLAGS", null);
            return Receipt(new Dictionary<string, object> {
                { "all_captured_flags_known", true }, { "any_captured_modified", any },
                { "scope", "enumerated members and flags only; not an atomic session snapshot" }
            }, "aggregate of captured loaded member UF flags", "CAPTURED", null);
        }

        public static Dictionary<string, object> Capture(IContextBackend b)
        {
            if (b == null) throw new ArgumentNullException("b");
            string started = Now();
            var state = new State();
            var fields = new Dictionary<string, object>();
            fields["process.pid"] = Read(state, "Process.GetCurrentProcess().Id", delegate {
                int pid = b.GetProcessId(); if (pid <= 0) throw new InvalidDataException("PID must be positive"); return pid;
            });
            fields["assembly.location"] = Read(state, "Assembly.GetExecutingAssembly().Location", delegate { return RequiredText(b.GetAssemblyLocation(), "Assembly.Location"); });
            fields["assembly.mvid"] = Read(state, "Assembly.ManifestModule.ModuleVersionId", delegate { return RequiredText(b.GetAssemblyMvid(), "MVID"); });
            fields["assembly.version"] = Read(state, "Assembly.GetName().Version", delegate { return RequiredText(b.GetAssemblyVersion(), "Assembly.Version"); });
            fields["assembly.disk_file_sha256"] = Read(state, "SHA256 of disk file at executing Assembly.Location; NOT memory bytes", delegate {
                string h = b.GetAssemblyDiskSha256();
                if (h == null || !System.Text.RegularExpressions.Regex.IsMatch(h, "^[0-9a-f]{64}$")) throw new InvalidDataException("Disk SHA256 must be lowercase 64 hex");
                return h;
            });
            fields["thread_contract"] = Receipt(null, "Selected local NX12 contract review; thread equality cannot certify legality", "THREAD_CONTRACT_UNKNOWN", null);

            var cachedRead = Read(state, "Loader cached _session read without assignment", delegate { return b.GetCachedSession(); });
            var freshRead = Read(state, "Session.GetSession() fresh getter", delegate { return b.GetFreshSession(); });
            object cached = Good(cachedRead) ? cachedRead["value"] : null;
            object fresh = Good(freshRead) ? freshRead["value"] : null;
            fields["session.cached_present"] = Good(cachedRead)
                ? Receipt(cached != null, "Loader cached _session read without assignment", cached == null ? "CACHED_SESSION_NOT_INITIALIZED" : "CAPTURED", null)
                : cachedRead;
            if (Good(freshRead) && fresh == null)
            {
                state.Complete = false;
                fields["session.fresh_available"] = Receipt(null, "Session.GetSession()", "FRESH_SESSION_UNAVAILABLE", null);
            }
            else fields["session.fresh_available"] = Good(freshRead) ? Receipt(true, "Session.GetSession()", "CAPTURED", null) : freshRead;
            fields["session.managed_reference_equals"] = cached != null && fresh != null
                ? Receipt(Object.ReferenceEquals(cached, fresh), "Object.ReferenceEquals: managed references ONLY, not NX kernel session identity", "CAPTURED", null)
                : Receipt(null, "Object.ReferenceEquals: managed references ONLY", "NOT_APPLICABLE_MISSING_REFERENCE", null);
            fields["work_part"] = Role(state, b, fresh, true);
            fields["display_part"] = Role(state, b, fresh, false);

            var rows = new List<Dictionary<string, object>>();
            bool loadedComplete = false;
            if (fresh == null)
            {
                fields["loaded_parts"] = Receipt(null, "fresh Session.Parts.ToArray(): BasePart[]", "NOT_ATTEMPTED_NO_FRESH_SESSION", null);
            }
            else
            {
                var arrayRead = Read(state, "fresh Session.Parts.ToArray(): BasePart[]", delegate {
                    object[] parts = b.GetLoadedParts(fresh); if (parts == null) throw new InvalidDataException("Loaded array was null"); return parts;
                });
                if (Good(arrayRead))
                {
                    object[] parts = (object[])arrayRead["value"];
                    loadedComplete = parts.Length <= MaximumParts;
                    if (!loadedComplete) state.Complete = false;
                    for (int i = 0; i < Math.Min(parts.Length, MaximumParts); i++)
                    {
                        if (parts[i] == null)
                        {
                            state.Complete = false; loadedComplete = false;
                            var bad = new Dictionary<string, object>();
                            foreach (string property in new [] { "path", "tag", "uf_modified", "is_read_only", "is_fully_loaded" })
                                bad[property] = Receipt(null, "loaded array item " + i, "NULL_PART_ENTRY", null);
                            rows.Add(bad);
                        }
                        else rows.Add(Part(state, b, parts[i]));
                    }
                    fields["loaded_parts"] = Receipt(rows, "fresh Session.Parts.ToArray(): BasePart[]", !loadedComplete ? "PARTIAL" : rows.Count == 0 ? "EMPTY_LOADED_COLLECTION" : "CAPTURED", null);
                }
                else fields["loaded_parts"] = arrayRead;
            }
            fields["loaded_modified_summary"] = ModifiedSummary(rows, loadedComplete);

            var ufCount = Read(state, "UF_PART_ask_num_parts: direct count, NOT return code", delegate {
                int count = b.GetUfLoadedCount(); if (count < 0) throw new InvalidDataException("Negative UF loaded count"); return count;
            });
            fields["uf.loaded_count"] = ufCount;
            var ufRows = new List<Dictionary<string, object>>();
            bool ufComplete = Good(ufCount);
            if (ufComplete)
            {
                int count = (int)ufCount["value"];
                if (count > MaximumParts) { state.Complete = false; ufComplete = false; }
                for (int i = 0; i < Math.Min(count, MaximumParts); i++)
                {
                    int index = i;
                    var tag = UfTag(state, "UF_PART_ask_nth_part(index): direct tag, NOT return code", delegate { return b.GetUfNthTag(index); });
                    if (!Good(tag)) { ufComplete = false; state.Complete = false; }
                    ufRows.Add(new Dictionary<string, object> {
                        { "index", Receipt(i, "requested zero-based UF part index", "CAPTURED", null) }, { "tag", tag }
                    });
                }
                fields["uf.loaded_tags"] = Receipt(ufRows, "UF_PART_ask_nth_part for each index 0..count-1", !ufComplete ? "PARTIAL" : count == 0 ? "EMPTY_LOADED_COLLECTION" : "CAPTURED", null);
            }
            else fields["uf.loaded_tags"] = Receipt(null, "UF_PART_ask_nth_part", "NOT_ATTEMPTED_UNKNOWN_COUNT", null);
            fields["uf.work_tag"] = UfTag(state, "UF_ASSEM_ask_work_part: direct tag", delegate { return b.GetUfWorkTag(); });
            fields["uf.display_tag"] = UfTag(state, "UF_PART_ask_display_part: direct tag", delegate { return b.GetUfDisplayTag(); });

            var managedTags = new List<string>();
            foreach (var row in rows)
            {
                var tag = (Dictionary<string, object>)row["tag"];
                if (!Good(tag)) loadedComplete = false; else managedTags.Add((string)tag["value"]);
            }
            var ufTags = new List<string>();
            foreach (var row in ufRows)
            {
                var tag = (Dictionary<string, object>)row["tag"];
                if (Good(tag)) ufTags.Add((string)tag["value"]);
            }
            if (!loadedComplete || !ufComplete)
                fields["crosscheck.loaded_tags"] = Receipt(null, "managed and UF tag observations", "UNKNOWN_PARTIAL_OBSERVATIONS", null);
            else
            {
                var m = new HashSet<string>(managedTags, StringComparer.Ordinal);
                var u = new HashSet<string>(ufTags, StringComparer.Ordinal);
                bool equal = m.SetEquals(u) && managedTags.Count == ufTags.Count && m.Count == managedTags.Count && u.Count == ufTags.Count;
                if (!equal) state.Complete = false;
                fields["crosscheck.loaded_tags"] = Receipt(new Dictionary<string, object> {
                    { "managed_tags", managedTags }, { "uf_tags", ufTags }, { "same_unique_tag_set", equal }
                }, "sequential managed/UF tag observations; not atomic", equal ? "CONSISTENT_FINITE_OBSERVATION" : "INCONSISTENT", null);
            }
            return new Dictionary<string, object> {
                { "schema_version", "p13-minimal-context-v1" }, { "fields", fields },
                { "capture_window", new Dictionary<string, object> { { "started_at", started }, { "ended_at", Now() }, { "atomic", false } } },
                { "capture_complete", state.Complete }, { "geometry_acceptance", false }
            };
        }
    }
}
