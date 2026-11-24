    // P13 v1: independent v2 queue. This does not certify the NX thread contract.
    private const int WmReadonlyContext = 0x8000 + 2;
    private static readonly NxAiLab.Diagnostics.ContextStartupGate ContextStartup = new NxAiLab.Diagnostics.ContextStartupGate();
    private static readonly NxAiLab.Diagnostics.ContextRegistry ContextRequests = new NxAiLab.Diagnostics.ContextRegistry();
    private static readonly ConcurrentQueue<NxAiLab.Diagnostics.ContextRequest> ContextQueue = new ConcurrentQueue<NxAiLab.Diagnostics.ContextRequest>();

    private static byte[] ReadContextAwareLineBytes(Stream stream)
    {
        return NxAiLab.Diagnostics.ContextProtocol.ReadFrame(stream);
    }

    private static void HandleReadonlyContextFrame(Stream stream, byte[] frame)
    {
        NxAiLab.Diagnostics.ContextRequest request;
        try
        {
            string text = new UTF8Encoding(false, true).GetString(frame);
            request = ContextRequests.Accept(text, 30000);
        }
        catch (Exception e)
        {
            // No validated owned UUID: no response fabricated and no dispatch.
            Log("context v2 frame rejected: " + e.GetType().FullName + ": " + e.Message);
            return;
        }
        ContextQueue.Enqueue(request);
        try
        {
            // Existing dispatcher path retained; no claim that thread legality is proven.
            // This synchronous call has no hard execution timeout. The slot rejects late results.
            SendMessage(_timerWindow.Handle, WmReadonlyContext, IntPtr.Zero, IntPtr.Zero);
            string response = request.TakeResponse();
            if (response == null)
            {
                request.Expire();
                response = NxAiLab.Diagnostics.ContextProtocol.ErrorResponse(request.RequestUuid, "UNKNOWN_EXECUTION_OUTCOME", "No timely owned completion; no retry or cancellation");
            }
            byte[] wire = Encoding.UTF8.GetBytes(response + "\n");
            stream.Write(wire, 0, wire.Length);
            stream.Flush();
            Log("context v2 response sent: request_uuid=" + request.RequestUuid);
        }
        catch (Exception e)
        {
            request.Expire();
            Log("context v2 delivery UNKNOWN: request_uuid=" + request.RequestUuid + " " + e.GetType().FullName + ": " + e.Message);
            // Never retry, dequeue v1 response, reset UUID, or execute again.
        }
    }

    private static void DrainReadonlyContextQueue()
    {
        NxAiLab.Diagnostics.ContextRequest request;
        while (ContextQueue.TryDequeue(out request))
        {
            if (!request.TryBegin()) continue;
            try
            {
                var capture = NxAiLab.Diagnostics.ContextCapture.Capture(new ReadonlyContextBackend());
                bool complete = (bool)capture["capture_complete"];
                bool accepted = request.Complete(request.RequestUuid, capture, complete ? "CAPTURE_COMPLETED" : "CAPTURE_PARTIAL", complete);
                if (!accepted) Log("context completion isolated: request_uuid=" + request.RequestUuid + " reason=" + request.LastRejectedCompletion);
            }
            catch (Exception e)
            {
                bool accepted = request.CompleteFailure(request.RequestUuid, e);
                if (!accepted) Log("context failure isolated: request_uuid=" + request.RequestUuid + " reason=" + request.LastRejectedCompletion);
            }
        }
    }

    private sealed class ReadonlyContextBackend : NxAiLab.Diagnostics.IContextBackend
    {
        public int GetProcessId() { using (var process = System.Diagnostics.Process.GetCurrentProcess()) { return process.Id; } }
        public string GetAssemblyLocation() { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        public string GetAssemblyMvid() { return System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("D"); }
        public string GetAssemblyVersion() { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        public string GetAssemblyDiskSha256()
        {
            string path = GetAssemblyLocation();
            if (String.IsNullOrEmpty(path)) throw new IOException("Executing assembly has no disk location");
            var before = new FileInfo(path); long size = before.Length; DateTime written = before.LastWriteTimeUtc;
            byte[] digest;
            using (var hash = System.Security.Cryptography.SHA256.Create())
            using (var file = File.OpenRead(path)) { digest = hash.ComputeHash(file); }
            var after = new FileInfo(path);
            if (after.Length != size || after.LastWriteTimeUtc != written) throw new IOException("Assembly disk file changed during hash observation");
            return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }
        public object GetCachedSession() { return _session; } // Read only; never initialize this field.
        public object GetFreshSession() { return Session.GetSession(); }
        public object GetWorkPart(object session) { return ((Session)session).Parts.Work; }
        public object GetDisplayPart(object session) { return ((Session)session).Parts.Display; }
        public object[] GetLoadedParts(object session) { return ((Session)session).Parts.ToArray(); }
        public string GetPartPath(object part) { return ((BasePart)part).FullPath; }
        public string GetPartTag(object part) { return ContextTag(((BasePart)part).Tag); }
        public bool GetPartModified(object part) { return NXOpen.UF.UFSession.GetUFSession().Part.IsModified(((BasePart)part).Tag); }
        public bool GetPartReadOnly(object part) { return ((BasePart)part).IsReadOnly; }
        public bool GetPartFullyLoaded(object part) { return ((BasePart)part).IsFullyLoaded; }
        public int GetUfLoadedCount() { return NXOpen.UF.UFSession.GetUFSession().Part.AskNumParts(); }
        public string GetUfNthTag(int index) { return ContextTag(NXOpen.UF.UFSession.GetUFSession().Part.AskNthPart(index)); }
        public string GetUfWorkTag() { return ContextTag(NXOpen.UF.UFSession.GetUFSession().Assem.AskWorkPart()); }
        public string GetUfDisplayTag() { return ContextTag(NXOpen.UF.UFSession.GetUFSession().Part.AskDisplayPart()); }
        private static string ContextTag(Tag tag) { return tag == Tag.Null ? null : ((int)tag).ToString(CultureInfo.InvariantCulture); }
    }
