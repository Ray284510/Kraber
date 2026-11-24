    // This guard applies only to the disposable P13 fixture. All geometry is
    // still performed by the existing Execute/Shape/Hole handlers.
    private const string SmokeDirectory = @"@NX_LAB_ROOT@\models\phase4_p13_smoke_e80a1f33";
    private const string SmokeLock = @"@NX_LAB_ROOT@\logs\phase5\nx-execution.lock";
    private static bool smokeBetweenParts;
    private static string smokeCreateRequest;
    private static string smokeCreatedTag;
    private static bool smokeBlankInspected;

    private static void RecordSmokeRoles(Dictionary<string,object> data, PartCollection parts)
    {
        Part work = parts.Work, display = parts.Display;
        data["p13_work_path"] = work == null ? "" : work.FullPath;
        data["p13_display_path"] = display == null ? "" : display.FullPath;
        data["p13_work_tag"] = work == null ? null : work.Tag.ToString();
        data["p13_display_tag"] = display == null ? null : display.Tag.ToString();
    }
    private static readonly HashSet<string> smokeDispatches = new HashSet<string>();

    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    private static string BindSmokePeer(NamedPipeServerStream pipe, string line)
    {
        uint peer = 0;
        if (line.StartsWith("nx_p13_once ", StringComparison.Ordinal) &&
            !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out peer))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return peer.ToString(CultureInfo.InvariantCulture) + "\n" + line;
    }

    private static bool SmokePath(string path)
    {
        return !String.IsNullOrEmpty(path) && Path.IsPathRooted(path) &&
            Path.GetFullPath(path).StartsWith(SmokeDirectory + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameSmokePath(string left, string right)
    {
        return !String.IsNullOrEmpty(left) && !String.IsNullOrEmpty(right) &&
            String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SmokeRead(string cmd)
    {
        return cmd == "nx_status" || cmd == "nx_session_parts" || cmd == "nx_inspect";
    }

    private static string SmokeStartUtc()
    {
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
            return process.StartTime.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture);
    }

    private static string SmokeMvid()
    {
        return typeof(NX_MCP_Loader).Module.ModuleVersionId.ToString("D");
    }

    private static Dictionary<string,object> SmokeIntent(string lockPath)
    {
        // The Worker owns the mandatory byte-range lock on byte zero, and
        // fsyncs one transient dispatch permit starting at byte one.
        using (var file = new FileStream(lockPath, FileMode.Open, FileAccess.Read,
                                         FileShare.ReadWrite, 1, FileOptions.None))
        {
            bool locked = false;
            try { file.ReadByte(); }
            catch (IOException e) { if ((e.HResult & 65535) != 33) throw; locked = true; }
            if (!locked || file.Length < 3 || file.Length > 4096)
                throw new InvalidOperationException("No current Worker dispatch permit");
            long end = file.Length;
            file.Position = 1;
            using (var reader = new BinaryReader(file, Encoding.ASCII))
            {
                byte[] bytes = reader.ReadBytes((int)end - 1);
                if (bytes.Length != end - 1 || file.Length != end)
                    throw new IOException("Incomplete Worker dispatch permit");
                return new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string,object>>(Encoding.ASCII.GetString(bytes));
            }
        }
    }

    private static string SmokeOperationTarget(string operation, string command)
    {
        string final = Path.Combine(SmokeDirectory,"batch_validation.prt");
        switch (operation)
        {
            case "batch-validation.create.0":
                if (command == "nx_create_part " + final) return ""; break;
            case "batch-validation.solid.0":
                if (command == "nx_shape batch_outline box 0 0 0 0 0 1 20 16 4 create -") return final; break;
            case "batch-validation.hole.0":
                if (System.Text.RegularExpressions.Regex.IsMatch(command, @"\Anx_hole BODY_[1-9][0-9]* 0 0 4 4 0\z")) return final; break;
            case "batch-validation.view.0": if (command == "nx_fit_view") return final; break;
            case "batch-validation.save.0": if (command == "nx_save_part") return final; break;
            case "batch-validation.close.0":
                if (command == "nx_close_part 0") return final; break;
            case "batch-validation.reopen.0": if (command == "nx_open_part " + final) return ""; break;
        }
        throw new InvalidOperationException("Command outside fixed P13 operation");
    }

    private static string SmokeMutation(string request, string cmd, int peer, string lockPath)
    {
        var permit = SmokeIntent(lockPath);
        if ((string)permit["request"] != request || (string)permit["command"] != cmd ||
            (string)permit["start_utc"] != SmokeStartUtc() || (string)permit["loader_mvid"] != SmokeMvid() ||
            (int)permit["peer_pid"] != peer || (int)permit["nx_pid"] != System.Diagnostics.Process.GetCurrentProcess().Id)
            throw new InvalidOperationException("Stale permit, adapter, or NX process");
        string target = SmokeOperationTarget((string)permit["operation"],cmd);
        var parts = Session.GetSession().Parts;
        var work = parts.Work;
        var display = parts.Display;
        var loaded = parts.ToArray();
        if (target.Length == 0)
        {
            if (work != null || display != null || loaded.Length != 0 ||
                (string)permit["expected_path"] != "" || (string)permit["expected_tag"] != "")
                throw new InvalidOperationException("An empty NX session is required");
        }
        else
        {
            if (work == null || display == null || loaded.Length != 1 ||
                display.Tag != work.Tag || loaded[0].Tag != work.Tag ||
                !SameSmokePath(target, work.FullPath) || !SameSmokePath(target,(string)permit["expected_path"]) ||
                work.Tag.ToString() != (string)permit["expected_tag"] || !work.IsFullyLoaded || work.IsReadOnly ||
                _part == null || _part.Tag != work.Tag)
                throw new InvalidOperationException("Work/Display/Loaded Part identity changed");
            if (cmd.StartsWith("nx_hole ",StringComparison.Ordinal))
            {
                var solid = GetBody(cmd.Split(' ')[1]);
                if (solid.IsOccurrence || !solid.IsSolidBody || solid.OwningPart == null ||
                    solid.OwningPart.Tag != work.Tag || !work.Bodies.ToArray().Any(b => b.Tag == solid.Tag))
                    throw new InvalidOperationException("Body does not belong to the active solid part");
            }
        }
        if (!smokeDispatches.Add(request)) throw new InvalidOperationException("Dispatch already entered");
        if (cmd == "nx_close_part 0")
        {
            if (NXOpen.UF.UFSession.GetUFSession().Part.IsModified(work.Tag))
                throw new InvalidOperationException("Modified part cannot be closed");
            work.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
            _part = null;
            _partPath = null;
            ResetTaskState();
            smokeBetweenParts = true;
            return OkJson("closed part");
        }
        bool creating = cmd.StartsWith("nx_create_part ",StringComparison.Ordinal);
        bool opening = cmd.StartsWith("nx_open_part ",StringComparison.Ordinal);
        if (creating || opening)
        {
            smokeCreateRequest = null;
            smokeCreatedTag = null;
            smokeBlankInspected = false;
        }
        if (cmd.StartsWith("nx_shape ",StringComparison.Ordinal))
        {
            if (!smokeBlankInspected || smokeCreatedTag != work.Tag.ToString() ||
                work.Bodies.ToArray().Length != 0)
                throw new InvalidOperationException("Freshly created blank part and I0 required before block");
            smokeBlankInspected = false;
            smokeCreateRequest = null;
        }
        string result = Execute(cmd);
        if (creating || opening)
        {
            var js = new System.Web.Script.Serialization.JavaScriptSerializer();
            var data = js.Deserialize<Dictionary<string,object>>(result);
            if ((bool)data["ok"])
            {
                var current = Session.GetSession().Parts;
                var active = current.Work;
                string expected = cmd.Substring(cmd.IndexOf(' ') + 1);
                if (active == null || current.Display == null || current.Display.Tag != active.Tag ||
                    !SameSmokePath(active.FullPath, expected) || current.ToArray().Length != 1 ||
                    _part == null || _part.Tag != active.Tag)
                    throw new InvalidOperationException("Created or reopened part identity mismatch");
                RecordSmokeRoles(data, current);
                data["receipt_kind"] = "fresh_dispatch";
                if (creating)
                {
                    smokeCreatedTag = active.Tag.ToString();
                    smokeCreateRequest = request;
                    data["p13_create_request"] = request;
                }
                result = js.Serialize(data);
            }
        }
        if (target.Length == 0) smokeBetweenParts = true;
        if (cmd.StartsWith("nx_hole ",StringComparison.Ordinal) || cmd.StartsWith("nx_shape ",StringComparison.Ordinal))
        {
            var js = new System.Web.Script.Serialization.JavaScriptSerializer();
            var data = js.Deserialize<Dictionary<string,object>>(result);
            if ((bool)data["ok"])
            {
                string id = cmd.StartsWith("nx_shape ",StringComparison.Ordinal) ? (string)data["body_id"] : cmd.Split(' ')[1];
                Body owner = GetBody(id);
                if (owner.OwningPart.Tag != work.Tag || !work.Bodies.ToArray().Any(b => b.Tag == owner.Tag))
                    throw new InvalidOperationException("After modeling, body identity is unknown");
                data["body_id"] = id;
                data["body_tag"] = owner.Tag.ToString();
                data["owning_part_tag"] = work.Tag.ToString();
                result = js.Serialize(data);
            }
        }
        return result;
    }

    private static string DispatchSmokeAware(string queued)
    {
        int cut = queued.IndexOf('\n');
        int peer = Int32.Parse(queued.Substring(0,cut), CultureInfo.InvariantCulture);
        string line = queued.Substring(cut+1);
        if (!line.StartsWith("nx_p13_once ",StringComparison.Ordinal))
        {
            if (!SmokeRead(line) && line != "ping")
            {
                var parts = Session.GetSession().Parts;
                bool touches = line.Split(' ').Any(SmokePath) ||
                    parts.ToArray().Any(p => SmokePath(p.FullPath)) ||
                    (_part != null && SmokePath(_partPath));
                if (touches || (smokeBetweenParts && parts.Work == null))
                    throw new InvalidOperationException("Fixed Smoke model requires the formal Worker");
            }
            return Execute(line);
        }
        var frame = line.Split(new[]{' '}, 3);
        Guid parsed;
        if (frame.Length != 3 || peer <= 0 || !Guid.TryParseExact(frame[1],"D",out parsed) ||
            parsed.ToString("D") != frame[1]) throw new InvalidOperationException("Invalid scoped request");
        string cmd = frame[2];
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        serializer.MaxJsonLength = 4000000;
        Dictionary<string,object> response;
        try
        {
            string raw;
            if (SmokeRead(cmd))
            {
                var current = Session.GetSession().Parts;
                var work = current.Work;
                if (cmd == "nx_inspect" && (work == null || current.Display == null ||
                    current.Display.Tag != work.Tag || current.ToArray().Length != 1 ||
                    !SmokePath(work.FullPath) || _part == null || _part.Tag != work.Tag))
                    throw new InvalidOperationException("Inspection part ownership changed");
                if (work != null && SmokePath(work.FullPath)) smokeBetweenParts = true;
                raw = Execute(cmd);
            }
            else raw = SmokeMutation(frame[1],cmd,peer,SmokeLock);
            response = serializer.Deserialize<Dictionary<string,object>>(raw);
            if ((cmd == "nx_status" || cmd == "nx_inspect") && (bool)response["ok"])
            {
                var parts = Session.GetSession().Parts;
                RecordSmokeRoles(response, parts);
                if (cmd == "nx_inspect" && smokeCreateRequest != null)
                {
                    var work = parts.Work;
                    smokeBlankInspected = work != null && parts.Display != null &&
                        parts.Display.Tag == work.Tag && work.Tag.ToString() == smokeCreatedTag &&
                        work.Bodies.ToArray().Length == 0;
                    response["p13_create_request"] = smokeCreateRequest;
                    response["p13_blank_observed"] = smokeBlankInspected;
                }
            }
        }
        catch (Exception e)
        {
            response = new Dictionary<string,object>{{"ok",false},{"error",e.Message},{"error_type",e.GetType().FullName}};
        }
        response["p13_request"] = frame[1];
        response["p13_command"] = cmd;
        response["p13_start_utc"] = SmokeStartUtc();
        response["p13_loader_mvid"] = SmokeMvid();
        response["pipe_client_pid"] = peer;
        response["p13_server_pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
        return serializer.Serialize(response);
    }
