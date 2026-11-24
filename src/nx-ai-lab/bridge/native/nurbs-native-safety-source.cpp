// nurbs-native-safety-source.cpp -- SOURCE ONLY, runtime deliberately blocked.
// Installed NX12 declarations only. No managed structs or arrays cross the ABI.
// Host owns UF initialization, main-thread execution, exact face/part/hash/PID,
// NX version, units, inventory, dirty flags, Fatal detection and process exit.
// Do not load in the accepted fixture process. No SEH recovery or retry.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstdarg>
#include <cstdio>
#include <uf.h>
#include <uf_obj.h>
#include <uf_modl_bsurf.h>
static_assert(sizeof(tag_t)==4, "NX12 tag ABI mismatch");
static_assert(sizeof(void*)==8, "x64 required");
static_assert(sizeof(UF_MODL_bsurface_t)==48, "Re-review installed ABI");
#ifdef NURBS_ENABLE_RUNTIME
#error Runtime blocked: failure-output ownership and bounded traversal require review.
#endif
namespace {
constexpr double tolerance = 0.001; // explicit conversion tolerance; later mm fixture
constexpr int maxLoops = 64, maxCurves = 4096;
constexpr int blocked = 100, ioError = 101, quarantine = 102;
struct Evidence {
    HANDLE h;
    bool good;
    explicit Evidence(const char* path) : h(INVALID_HANDLE_VALUE), good(false) {
        if(path && *path) h=CreateFileA(path, GENERIC_WRITE, 0, nullptr,
            CREATE_NEW, FILE_ATTRIBUTE_NORMAL|FILE_FLAG_WRITE_THROUGH, nullptr);
        good=(h!=INVALID_HANDLE_VALUE);
    }
    bool log(const char* format, ...) {
        if(!good) return false;
        char line[2048];
        va_list args; va_start(args,format);
        int n=vsnprintf(line,sizeof(line),format,args); va_end(args);
        if(n<0 || static_cast<size_t>(n)>=sizeof(line)) return good=false;
        DWORD written=0;
        good=WriteFile(h,line,static_cast<DWORD>(n),&written,nullptr)!=0
             && written==static_cast<DWORD>(n);
        if(good) good=FlushFileBuffers(h)!=0;
        return good;
    }
    ~Evidence() { if(h!=INVALID_HANDLE_VALUE) CloseHandle(h); }
};
// Type-checked proposal only: no exported caller can reach this function.
// On any uncertain output/lifecycle state, retain allocations and quarantine
// the isolated process; never guess lengths or continue modeling/save/retry.
// This is NOT a claim of safe cleanup on UF failure.
[[maybe_unused]] int proposed_once(tag_t face, Evidence& out) {
    UF_MODL_bsurface_t b{};
    int loops=0, *counts=nullptr, *senses=nullptr;
    tag_t* curves=nullptr;
    int opts[4]={0,0,0,0}; // reserved N/A slots zero; no forced nonperiodic/normalize
    if(!out.log("before Ask2dtrim face=%u tolerance=%.17g opts=0,0,0,0\n",
                static_cast<unsigned>(face), tolerance)) return ioError;
    const int ask=UF_MODL_ask_2dtrim_bsurf(face,opts,tolerance,
                                         &b,&loops,&counts,&senses,&curves);
    if(!out.log("after Ask2dtrim rc=%d loops=%d counts=%p senses=%p curves=%p\n",
        ask,loops,static_cast<void*>(counts),static_cast<void*>(senses),
        static_cast<void*>(curves))) return quarantine;
    if(ask!=0) {
        out.log("STOP unknown partial-output ownership; no dereference/free/retry\n");
        return quarantine;
    }
    if(loops<0 || loops>maxLoops || (loops && !counts)) return quarantine;
    int total=0;
    for(int l=0;l<loops;++l) {
        if(counts[l]<0 || counts[l]>maxCurves-total) return quarantine;
        total+=counts[l];
    }
    if(total && (!senses || !curves)) return quarantine;
    // Validate complete bounded tag list before any deletion; no invented
    // meaning for senses or inferred topological closure.
    for(int k=0;k<total;++k) {
        if(curves[k]==0 || curves[k]==face) return quarantine;
        for(int j=0;j<k;++j) if(curves[k]==curves[j]) return quarantine;
    }
    if(!out.log("temporary_curve_count=%d surface_poles=%d,%d order=%d,%d rational=%d\n",
        total,b.num_poles_u,b.num_poles_v,b.order_u,b.order_v,b.is_rational))
        return quarantine;
    int k=0;
    for(int l=0;l<loops;++l) {
        if(!out.log("returned_loop_index=%d edge_count=%d\n",l,counts[l]))
            return quarantine;
        for(int e=0;e<counts[l];++e,++k)
            if(!out.log("returned_loop=%d slot=%d flat_index=%d tag=%u raw_sense=%d\n",
                l,e,k,static_cast<unsigned>(curves[k]),senses[k])) return quarantine;
    }
    // Header uf_modl_bsurf.h:592-593: ALL temporary curves must be
    // deleted BEFORE freeing their tag array. UF_OBJ deletion rc alone
    // is insufficient (uf_obj.h:765-766).
    for(k=0;k<total;++k) {
        if(!out.log("before delete index=%d tag=%u\n",k,
                    static_cast<unsigned>(curves[k]))) return quarantine;
        int rc=UF_OBJ_delete_object(curves[k]);
        if(!out.log("after delete index=%d rc=%d\n",k,rc)) return quarantine;
        if(rc!=0) return quarantine;
        int status=UF_OBJ_ask_status(curves[k]); // returns STATUS, not error code
        if(!out.log("delete verification index=%d status=%d\n",k,status))
            return quarantine;
        if(status!=UF_OBJ_DELETED) return quarantine;
    }
    if(!out.log("all_temporary_tags_verified_deleted=%d\n",total)) return quarantine;
    if(curves) {
        if(!out.log("before UF_free edge_bcurves\n")) return quarantine;
        UF_free(curves); curves=nullptr;
        if(!out.log("after UF_free edge_bcurves return=void\n")) return quarantine;
    }
    if(!out.log("before UF_MODL_free_bsurf_data\n")) return quarantine;
    int release=UF_MODL_free_bsurf_data(&b);
    if(!out.log("after UF_MODL_free_bsurf_data rc=%d\n",release)) return quarantine;
    if(release!=0) return quarantine;
    if(senses) {
        if(!out.log("before UF_free edge_sense\n")) return quarantine;
        UF_free(senses); senses=nullptr;
        if(!out.log("after UF_free edge_sense return=void\n")) return quarantine;
    }
    if(counts) {
        if(!out.log("before UF_free edge_count\n")) return quarantine;
        UF_free(counts); counts=nullptr;
        if(!out.log("after UF_free edge_count return=void\n")) return quarantine;
    }
    return out.log("success_path_cleanup_complete=1 runtime_acceptance=0\n")
        ? 0 : quarantine;
}
}
extern "C" __declspec(dllexport)
int __cdecl nx12_ask2dtrim_once(std::uint32_t face, const char* output_path) {
    Evidence out(output_path); // exclusive NEW ANSI path; never overwrite
    if(!out.good) return ioError;
    if(!out.log("stage=source_only pid=%lu interface=C_cdecl_uint32_const_char_ptr\n"
                "face=%u fixed_tolerance=%.17g expected_NX=12.0.2.9\n"
                "actual_NX_version=not_queried inventory=coordinator_required\n"
                "dirty=not_queried part_hash=coordinator_required\n"
                "Ask2dtrim_calls=0 temporary_count=not_queried\n"
                "result=COMPILE_TIME_BLOCKED return_code=100 process_exit=host_owned\n",
                GetCurrentProcessId(),static_cast<unsigned>(face),tolerance))
        return ioError;
    return blocked;
}

