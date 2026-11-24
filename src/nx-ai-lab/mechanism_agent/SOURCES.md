# Mechanical and API references

- CMU, Zhang/Finger/Behrens, Introduction to Mechanisms, planar linkages: https://www.cs.cmu.edu/~rapidproto/mechanisms/chpt5.html
- Middle East Technical University, mechanisms course, four-bar classification, transmission angles and dead centres: https://ocw.metu.edu.tr/pluginfile.php/3961/mod_resource/content/12/ch7/7-1.htm
- Installed NX 12.0.2.9 primary API documentation: `@NX_INSTALL_ROOT@\NXBIN\managed\NXOpen.xml`, `NXOpen.UF.xml`, `@NX_INSTALL_ROOT@\UGOPEN\uf_motion.h`, `uf_motion_types.h`, `uf_object_types.h`.
- CMU, cam classification, follower laws and profile construction: https://www.cs.cmu.edu/~rapidproto/mechanisms/chpt6.html
- KHK, internal gear catalog, planetary compatibility and example 40/20/80 teeth with three planets (PDF page 2): https://khkgears.net/pdf/2025/internal-gears.pdf

NX12-specific gear API checks used the installed `Motion.CouplerBuilder` radius expressions (introduced NX12), `GeneralCouplerBuilder` rotational joint scales (available before NX12), `MotionSolution.GetIsSuppressed` and `AddSuppressedObject` (NX12). API presence was followed by real bridge compilation, object creation, native solution and marker checks. No newer Transmission Builder API was assumed available.

Source formulas inform the independent calculation. A formula, available API symbol or catalog entry is not an NX runtime acceptance result. The four-bar calculation uses loop closure and configuration-dependent Jacobian rank in addition to the nominal mobility formula.

- Lynch and Park, Modern Robotics, closed-chain mobility and parallel manipulators: https://hades.mech.northwestern.edu/images/2/25/MR-v2.pdf . The textbook Delta example has a different joint architecture; its local-spin count is not copied into this model. Our 3R/6U/6S model uses its own full constraint Jacobian, native joint queries and native solver mobility.
- NX12 Universal joint implementation was checked against the installed `Motion.JointDefine` CrossPin axis type and axis vectors, then compiled and exercised on the actual Delta model. Spherical joints and saved point occurrence references were independently queried after reopening.
