# Mechanism knowledge and verification boundaries

This is a decision reference, not an acceptance ledger. `catalog.json` identifies tested configurations; `logs/phase4/acceptance.json` and per-run native evidence own acceptance. A new size, grounding choice, axis convention or cam law needs a new NX run.

| Family | Physical decision / calculation | What the actual NX model must establish |
|---|---|---|
| Crank-rocker | Grashof, shortest member adjacent to grounded member; four-bar closure, extrema and transmission angle | One grounded input rotates fully; output rocks with measured extrema |
| Double crank | Ground the shortest Grashof member | Both ground-connected moving members rotate fully; no joint/shaft interference |
| Double rocker | Non-Grashof or shortest member opposite the ground; actuation must respect finite input intervals | Specify which relative joint is driven; do not pretend a bounded ground arm makes a full revolution |
| Slider-crank | Ground canonical frame L0 in L0-R-L1-R-L2-R-L3-P-L0 | Slider translates in a ground guide; closed-loop trajectory, stroke and timing |
| Guide bar | Ground canonical L1; long pivoting slotted guide and a translating block | Actual moving P axis follows the guide, with distinct long-guide construction |
| Oscillating block | Ground canonical L2; short pivoted sleeve and long sliding rod | The sleeve rocks about its grounded R joint; not a renamed ground slider |
| Fixed block | Ground canonical L3; fixed short sleeve and translating original frame/rod | The sleeve is the ground; rocker and connector drive the rod through it |
| Scotch yoke | Constant-speed input gives exact harmonic output, K=1; pin-slot wear is a tradeoff | Real slot and guide, independent input only, native pin-slot relation and measured harmonic trajectory |
| Translating radial cam | Choose law, rise/return/dwell, pitch radius, roller offset; pressure and curvature | Actual profile, native relation, follower trajectory, contact and swept interference |
| Oscillating cam follower | Follower pivot/length and contact normal determine pressure angle | Native oscillation about the actual pivot; same profile does not inherit translating-follower acceptance |
| Knife-tip follower | Actual theoretical contact point; sharp physical tip | Numerical precision can create tiny real intersections; no volume threshold exemption |
| Groove cam | Two offset walls plus floor; clearance and local roller spin | Nominal PointOnCurve is bilateral ideal kinematics; prove local spin does not affect useful output |
| External gear pair | Signed ratio -Zin/Zout, centre m(Z1+Z2)/2 | Real tooth geometry, actual native relation and measured opposite direction |
| Internal gear pair | Signed ratio +Zpinion/Zring, centre m(Zring-Zpinion)/2 | Direct native internal relationship must be tested separately from General coupling |
| Multistage gears | Product of signed stage ratios; compound gear rigid grouping | Gear planes, shaft groups, every mesh ratio and cross-stage interference |
| Rack and pinion | Translation = pitch radius × input radians | Native RackPinion ratio/units, teeth and actual translation; contact point may be a separate limitation |
| Planetary | Willis plus Zring=Zsun+2Zplanet; phasing and planet spacing | Correct grounded member, carrier-relative joint conventions, all planet trajectories and meshes |
| Delta / spatial branches | Spatial screw-constraint rank, actual U/S/R axes, closure and singularity scan | Native joint geometry and solver trajectory; a scalar textbook count alone is insufficient |
| Geneva | Slots/index angle, entry/exit tangency, locking arcs and dwell | Real engagement and locking must cause output; a prescribed output curve is insufficient |
| Ratchet | Driving linkage, driving pawl, holding pawl, tooth direction and preload | Unilateral engagement, reverse retention and output increment; not an always-active gear ratio |

`calculations.py` provides loop closure, extrema, pressure/transmission angles, planar mobility and spatial rank. `topology.py` reads actual native lower-pair joint records, clusters connected compound hinges and counts n-1 independent revolute pairs. It does not silently include higher pairs. `native_mobility.py` adds signed native Gear equations to the full spatial constraint matrix and rejects unsupported coupling types.

Local freedom is subtracted only with a physical output definition and a perturbation test, as in the roller-spin test. Redundant equations from planar embedding are distinguished from conflicting constraints. A matrix rank is instantaneous; scan actual configurations and inspect native solver reduction. For an oscillating input or continuously rotating output, do not assign a textbook constant-input reciprocating K without a meaningful time law.

The new synthesis path compares hard feasibility, then declared numerical objectives; footprint estimates are replaced by available physical realization envelopes before building. It cannot infer unprovided strength, payload, torque, wear or regulatory suitability. Adjustable crank stroke means a clamped pin can be relocated between runs; it does not establish safe adjustment during motion.

Gear validation has three distinct levels: real geometric teeth, ideal native motion transmission, and resolved tooth-contact dynamics. The first two do not imply the third. Presently accepted cams likewise use ideal bilateral geometric constraints; they do not establish preload, separation, friction or follower-force dynamics.

See `COMPLETION_SOURCES.md` for primary mechanism references and installed NX12 API evidence. All failed and repaired runs remain separately traceable.
