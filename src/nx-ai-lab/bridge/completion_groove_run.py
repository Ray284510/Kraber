import asyncio,sys
from phase4_common import ROOT
from mechanism_agent.completion_groove import design,profile,law
from phase4_cam_groove_build import main as build
from phase4_cam_groove_motion import main as motion
if __name__=='__main__':
    if sys.argv[1]=='build':

        asyncio.run(build(design,profile,'completion_cam_groove_quintic'))
    elif sys.argv[1]=='motion':asyncio.run(motion('completion_cam_groove_quintic',law))
    else:raise ValueError('build or motion required')
