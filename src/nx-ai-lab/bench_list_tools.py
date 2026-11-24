"""Measure MCP stdio discovery for this checkout without calling native tools."""
import asyncio
import json
import os
import sys
from pathlib import Path
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def main():
    root = Path(__file__).resolve().parent
    for folder in ("logs", "tmp"):
        (root / folder).mkdir(exist_ok=True)
    server = StdioServerParameters(
        command=sys.executable,
        args=["-B", str(root / "bridge" / "mcp_server.py")],
        cwd=str(root),
        env={**os.environ, "NX_AI_LAB_ROOT": str(root)},
    )
    async with stdio_client(server) as (reader, writer):
        async with ClientSession(reader, writer) as client:
            initialized = await client.initialize()
            names = []
            cursor = None
            while True:
                page = await client.list_tools(cursor=cursor)
                names.extend(tool.name for tool in page.tools)
                cursor = page.nextCursor
                if not cursor:
                    break
    if len(names) != len(set(names)) or "nx_inspect" not in names:
        raise RuntimeError("Unexpected MCP tool inventory")
    print(json.dumps({"server": initialized.serverInfo.name,
                      "protocol": initialized.protocolVersion,
                      "tool_count": len(names), "tools": sorted(names),
                      "native_tool_calls": 0}, indent=2))


if __name__ == "__main__":
    asyncio.run(main())
