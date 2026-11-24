# NX 12 MCP runtime

The stdio server entry point is `bridge/mcp_server.py`. It uses the local Windows named pipe `nx12_ai_lab`. The C# startup loader schedules NXOpen operations on the NX thread. The Python mechanism agent also supplies host-side calculations.

## Prepare a checkout

Use Windows x64 and Python 3.12. Run the following in the directory containing this README. The runtime uses whitespace-separated native command arguments. Choose a checkout path without spaces when passing model paths to commands.

```powershell
py -3.12 -m venv .venv
& ./.venv/Scripts/python.exe -m pip install -r requirements-lock.txt
New-Item -ItemType Directory -Force -Path tmp,logs,models,work/phase4 | Out-Null
& ./.venv/Scripts/python.exe -B -m unittest discover -s mechanism_agent -t . -q
```

## Compile the resident loader

Set `UGII_BASE_DIR` to the user's licensed Siemens NX 12 installation directory containing `NXBIN/managed`. The build verifies the SHA-256 values of four SDK assemblies and the .NET Framework compiler against `config/loader-build-lock.json`. A different compiler or maintenance release requires a separate review of these pins. Use the original pinned combination to reproduce this build.

```powershell
& ./bridge/build.ps1
```

The command writes a DLL and generation receipt under `tmp/verified-build-*`. It checks the normalized source inputs, replacement counts and generated loader digest. Then it expands the two build path markers in local copies of the compile inputs. Rebuild after moving the checkout or the NX installation. The SDK assemblies, compiler, private logs, model fixtures, and historical native probe DLLs must be obtained separately where appropriate. Historical commands that require those artifacts cannot run from the source bundle alone.

The loader expects a dedicated NX user directory at `nx-user` under the checkout, and its DLL at `nx-user/startup/NX12_Loader.dll`. Configure the dedicated NX process to use that user directory through `UGII_USER_DIR`, and set `NX_AI_LAB_ROOT` to the checkout root. The historical code uses a fixed named pipe. Use one dedicated NX process serving that pipe at a time. The build script only compiles.

## Connect an MCP client

Configure a stdio server whose executable is the absolute path to `.venv/Scripts/python.exe` and whose argument is the absolute path to `bridge/mcp_server.py`. The server lists its tools before a native session connects. A native model operation requires a compatible running NX session with the compiled loader. Host-side catalog paths refer to historical evidence, which can be missing from a source-only checkout.

## Build input provenance

The upstream input `src/NX_MCP-Enhanced/loader/NX_MCP_Loader.cs` is from [kongbaic/NX_MCP-Enhanced](https://github.com/kongbaic/NX_MCP-Enhanced/tree/4e9b8fe163a3ea794b004d06e1d7d653d2c4c6e5). Its MIT notice is included alongside the upstream source. The local generator applies the checked modifications in `bridge` to this input. The bundled snapshot `bridge/NX12_Loader.cs` equals the regenerated portable source after decoding the UTF-8 BOM and normalizing newlines.

## Verification scope

The publication copy compiled against the locally installed NX 12 SDK. Compiler warnings identified deprecated NX APIs. The 39 mechanism-agent unittest cases passed, and MCP initialization and tool discovery over stdio returned 41 tools. This verification used existing local Python dependencies and performed no NX startup, native CAD command, model change or live geometry acceptance.

## Diagnostic source coverage

The portable source includes the status-only state machine, exclusive attempt store, injected pipe-acquisition and peer-PID checks, Win32 status adapter, phase-journal reader, process evidence formatting and discovery. The four historically consumed or retired identifiers remain blocked by digest comparison. Offline replay checks rejected each original value, accepted a new synthetic value and imported all ten diagnostic modules.

The peer-gate preparation model has a synthetic process identity. Its native counterparts `peer_pid_native.py`, `peer_pid_process.py`, `peer_pid_runner.py`, `peer_gated_context.py`, `peer_gated_session_parts.py` and `peer_gated_status.py` were specific to historical live process and ticket bindings. Those source paths and their associated authorization proposals, tickets, private launch references and probe part are outside this distribution. The exported status protocol retains its pinned executable and model digests. Native status validation requires a user-supplied installation and matching private probe.
