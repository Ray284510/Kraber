// One isolated planar lifetime trial; original blocked candidate remains separate.
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
#include <uf_object_types.h>
#include <set>
#include <algorithm>
#include <iterator>
#include <uf_modl_bsurf.h>
static_assert(sizeof(tag_t)==4, "NX12 tag ABI mismatch");
static_assert(sizeof(void*)==8, "x64 required");
static_assert(sizeof(UF_MODL_bsurface_t)==48, "Re-review installed ABI");
namespace {
constexpr double tolerance = 0.001; // explicit conversion tolerance; later mm fixture
constexpr int maxLoops = 64, maxCurves = 4096;
constexpr int ioError = 101, quarantine = 102;
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
// Inventory includes temporary objects; does not include expressions.
// Host MUST ensure fully loaded, isolated, idle fixture, main-thread UF context.
// No deletion occurs while cycling. A bounded failed census is never accepted.
using Inventory = std::set<tag_t>;
bool census(tag_t part, Inventory& tags, Evidence& out, const char* phase) {
    tag_t cursor = NULL_TAG;
    for (unsigned n=0; n<100000; ++n) {
        cursor=UF_OBJ_cycle_all(part,cursor);
        if(cursor==NULL_TAG)
            return out.log("inventory phase=%s count=%llu complete=1 expressions_excluded=1\n",
                phase,static_cast<unsigned long long>(tags.size()));
        if(!tags.insert(cursor).second) return false;
        if(!out.log("inventory phase=%s tag=%u\n",phase,
                    static_cast<unsigned>(cursor))) return false;
    }
    return false;
}
// Success-only release; uncertain outputs quarantine the isolated process.
// On any uncertain output/lifecycle state, retain allocations and quarantine
// the isolated process; never guess lengths or continue modeling/save/retry.
// This is NOT a claim of safe cleanup on UF failure.
[[maybe_unused]] int proposed_once(tag_t face, Evidence& out) {
    tag_t part=NULL_TAG;
    int owner_rc=UF_OBJ_ask_owning_part(face,&part);
    if(!out.log("face_owner_rc=%d part=%u\n",owner_rc,
                static_cast<unsigned>(part))) return ioError;
    if(owner_rc!=0 || part==NULL_TAG) return quarantine;
    Inventory before, after, returned, final_tags;
    if(!census(part,before,out,"before") || !before.count(face)) return quarantine;
    UF_MODL_bsurface_t b{};
    int loops=0, *counts=nullptr, *senses=nullptr;
    tag_t* curves=nullptr;
    int opts[4]={0,0,0,0}; // reserved N/A slots zero; no forced nonperiodic/normalize
    if(!out.log("before Ask2dtrim face=%u tolerance=%.17g opts=0,0,0,0\n",
                static_cast<unsigned>(face), tolerance)) return ioError;
    const int ask=UF_MODL_ask_2dtrim_bsurf(face,opts,tolerance,
                                         &b,&loops,&counts,&senses,&curves);
    // On failure inspect ONLY rc; no output inspection, destructor or cleanup.
    if(!out.log("after Ask2dtrim rc=%d\n",ask)) return quarantine;
    if(ask!=0) {
        out.log("QUARANTINE unknown partial ownership; no outputs read/freed; host must stop isolated process without save/retry\n");
        return quarantine;
    }
    if(!out.log("success loop_count=%d\n",loops)) return quarantine;
    if(loops<=0 || loops>maxLoops || !counts) return quarantine;
    int total=0;
    for(int l=0;l<loops;++l) {
        if(counts[l]<=0 || counts[l]>maxCurves-total) return quarantine;
        total+=counts[l];
    }
    if(total && (!senses || !curves)) return quarantine;
    // Strictly scoped planar fixture, checked AFTER documented bounded counts.
    // Either loop order allowed; neither is labelled outer/inner here.
    if(loops!=2 || total!=5 ||
       !((counts[0]==4 && counts[1]==1) ||
         (counts[0]==1 && counts[1]==4))) return quarantine;
    if(!census(part,after,out,"after_ask")) return quarantine;
    if(!std::includes(after.begin(),after.end(),before.begin(),before.end()))
        return quarantine;
    Inventory delta;
    std::set_difference(after.begin(),after.end(),before.begin(),before.end(),
                        std::inserter(delta,delta.end()));
    if(!out.log("new_object_count=%llu returned_curve_count=%d\n",
                static_cast<unsigned long long>(delta.size()),total))
        return quarantine;
    if(delta.size()!=static_cast<size_t>(total)) return quarantine;
    // Validate the ENTIRE list before deleting even one tag.
    for(int k=0;k<total;++k) {
        tag_t t=curves[k];
        if(t==NULL_TAG || !delta.count(t) || !returned.insert(t).second)
            return quarantine;
        int status=UF_OBJ_ask_status(t);
        tag_t owner=NULL_TAG; int type=-1, subtype=-1;
        int own_rc=UF_OBJ_ask_owning_part(t,&owner);
        int type_rc=UF_OBJ_ask_type_and_subtype(t,&type,&subtype);
        if(!out.log("validate index=%d tag=%u status=%d owner_rc=%d owner=%u type_rc=%d type=%d subtype=%d\n",
                    k,static_cast<unsigned>(t),status,own_rc,
                    static_cast<unsigned>(owner),type_rc,type,subtype))
            return quarantine;
        // Only a documented returned B-curve in the exact new owned tag set qualifies.
        if((status!=UF_OBJ_TEMPORARY && status!=UF_OBJ_ALIVE) || own_rc!=0 || owner!=part ||
           type_rc!=0 || type!=UF_spline_type) return quarantine;
    }
    if(returned!=delta) return quarantine;
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
    if(!census(part,final_tags,out,"after_cleanup")) return quarantine;
    if(final_tags!=before) {
        out.log("QUARANTINE post_inventory_mismatch=1 no_save_no_retry=1\n");
        return quarantine;
    }
    return out.log("success_path_cleanup_complete=1 inventory_exact_match=1 runtime_acceptance=0 host_checks_pending=1\n")
        ? 0 : quarantine;
}
}
extern "C" __declspec(dllexport)
int __cdecl nx12_ask2dtrim_once(std::uint32_t face, const char* output_path) {
    Evidence out(output_path); // exclusive NEW ANSI path; never overwrite
    if(!out.good) return ioError;
    if(!out.log("stage=isolated_planar_trial pid=%lu\n",GetCurrentProcessId()))return ioError;
    if(!UF_is_initialized()) {out.log("UF not initialized; no call\n");return quarantine;}
    return proposed_once(static_cast<tag_t>(face),out);
}
