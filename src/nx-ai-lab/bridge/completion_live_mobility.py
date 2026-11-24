"""Call the new read-only native-query MCP tool on the exact current test SIM."""
import asyncio,json,sys
from pathlib import Path
from phase4_common import Client,LOG

async def main(family,expected=1):
    run=json.loads((LOG/f'current-{family}.json').read_text(encoding='utf-8'))
    async with Client() as c:
        assert (await c.call('nx_status'))['part']==run['simulation']
        result=await c.tool('nx_mechanism_native_mobility',{})
        (Path(run['logs'])/'live-native-independent-mobility.json').write_text(json.dumps(result,indent=2))
        assert result['ok'] and result['independent_result']['functional_dof']==int(expected)
        print({'family':family,'actual_native_query':True,'independent_dof':result['independent_result']['functional_dof']})

if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
