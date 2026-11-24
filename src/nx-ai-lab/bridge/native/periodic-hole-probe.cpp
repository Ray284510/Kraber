#include <array>
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
#include <uf_curve.h>
#include <cmath>
static_assert(sizeof(tag_t)==4, "NX12 tag ABI mismatch");
static_assert(sizeof(void*)==8, "x64 required");
static_assert(sizeof(UF_MODL_bsurface_t)==48, "Re-review installed ABI");
namespace {
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
// Isolated trial compiled with the installed NX12 UGOPEN declarations.
static_assert(sizeof(UF_CURVE_spline_t)==48 && alignof(UF_CURVE_spline_t)==8, "NX12 x64 spline ABI");
constexpr int quarantine=102;
constexpr double coordinates[4][2]={{.05,.05},{.95,.05},{.95,.95},{.05,.95}};

bool create_curve(const double a[2], const double b[2], tag_t& tag, Evidence& out, int index) {
    double knots[]={0,0,1,1};
    double poles[2][4]={{a[0],a[1],0,1},{b[0],b[1],0,1}};
    UF_CURVE_spline_t input{};
    input.num_poles=2;
    input.order=2;
    input.is_rational=0;
    input.knots=knots;
    input.poles=poles;
    input.start_param=0.0;
    input.end_param=1.0;
    int states=0;
    UF_CURVE_state_p_t issues=nullptr;
    if(!out.log("before UF_CURVE_create_spline index=%d poles=2 order=2 rational=0 a=%.17g,%.17g b=%.17g,%.17g\n",
                index,a[0],a[1],b[0],b[1])) return false;
    const int rc=UF_CURVE_create_spline(&input,&tag,&states,&issues);
    if(!out.log("after UF_CURVE_create_spline index=%d rc=%d\n",index,rc)||rc!=0) return false;
    if(!out.log("creation_result index=%d tag=%u states=%d pointer=%d\n",index,tag,states,issues!=nullptr)
       || tag==NULL_TAG || states<0 || states>64 || (states>0&&!issues)) return false;
    for(int i=0;i<states;++i)
        if(!out.log("state index=%d slot=%d code=%d flag=%d\n",index,i,issues[i].state_code,issues[i].flag))return false;
    if(issues) {
        if(!out.log("before UF_free spline_states\n"))return false;
        UF_free(issues);
        if(!out.log("after UF_free spline_states\n"))return false;
    }
    return states==0;
}

bool verify_curve(tag_t curve,tag_t part,Evidence& out) {
    if(!out.log("before UF_OBJ_ask_status tag=%u\n",curve))return false;
    const int status=UF_OBJ_ask_status(curve);
    if(!out.log("after UF_OBJ_ask_status status=%d\n",status)||status!=UF_OBJ_ALIVE)return false;
    tag_t owner=NULL_TAG;
    if(!out.log("before UF_OBJ_ask_owning_part tag=%u\n",curve))return false;
    int rc=UF_OBJ_ask_owning_part(curve,&owner);
    if(!out.log("after UF_OBJ_ask_owning_part rc=%d owner=%u\n",rc,owner)||rc||owner!=part)return false;
    int type=-1,subtype=-1;
    if(!out.log("before UF_OBJ_ask_type_and_subtype tag=%u\n",curve))return false;
    rc=UF_OBJ_ask_type_and_subtype(curve,&type,&subtype);
    if(!out.log("after UF_OBJ_ask_type_and_subtype rc=%d type=%d subtype=%d\n",rc,type,subtype)
       ||rc||type!=UF_spline_type)return false;
    double range[2]{};int periodic=-1;
    if(!out.log("before UF_CURVE_ask_parameterization tag=%u\n",curve))return false;
    rc=UF_CURVE_ask_parameterization(curve,range,&periodic);
    return out.log("after UF_CURVE_ask_parameterization rc=%d range=%.17g,%.17g periodicity=%d\n",
                   rc,range[0],range[1],periodic)&&!rc&&periodic==UF_CURVE_OPEN_CURVE
        &&std::abs(range[0])<1e-12&&std::abs(range[1]-1.)<1e-12;
}

#include "periodic-surface-net.h"

constexpr double boundary[12][2]={
    {0,0},{1,0},{1,.4},{.92,.4},{.92,.6},{1,.6},
    {1,1},{0,1},{0,.6},{.08,.6},{.08,.4},{0,.4}
};
constexpr double secondHole[4][2]={{.40,.48},{.40,.66},{.56,.66},{.56,.48}};
struct Trial {
    tag_t part=NULL_TAG;
    tag_t curves[16]{};
    int count=0;
    Inventory before;
    bool prepared=false,attempted=false;
} seamTrial;

bool check_end(tag_t tag,int end,const double* uv,Evidence& out) {
    double xyz[3]{};
    if(!out.log("before UF_CURVE_evaluate_curve tag=%u end=%d\n",tag,end))return false;
    const int rc=UF_CURVE_evaluate_curve(tag,static_cast<double>(end),UF_CURVE_LOC,xyz);
    return out.log("after UF_CURVE_evaluate_curve rc=%d xyz=%.17g,%.17g,%.17g\n",rc,xyz[0],xyz[1],xyz[2])
        &&rc==0&&std::hypot(xyz[0]-uv[0],xyz[1]-uv[1])<1e-10&&std::abs(xyz[2])<1e-10;
}
}
extern "C" __declspec(dllexport)
int __cdecl nx12_periodic_hole_prepare(unsigned part,int count,const char* path) {
    Evidence out(path);
    if(!out.good||!out.log("pid=%lu part=%u before UF_is_initialized\n",GetCurrentProcessId(),part))return 101;
    const bool initialized=UF_is_initialized();
    if(!out.log("after UF_is_initialized initialized=%d\n",initialized)||!initialized||!part||seamTrial.attempted||(count!=12&&count!=16))
        return quarantine;
    seamTrial.attempted=true;seamTrial.part=part;seamTrial.count=count;
    if(!census(part,seamTrial.before,out,"before"))return quarantine;
    Inventory expected=seamTrial.before,after;
    for(int i=0;i<count;++i) {
        auto& curve=seamTrial.curves[i];
        const double* start=i<12?boundary[i]:secondHole[i-12];
        const double* end=i<12?boundary[(i+1)%12]:secondHole[(i-12+1)%4];
        if(!create_curve(start,end,curve,out,i)||!expected.insert(curve).second||
            !verify_curve(curve,part,out)||!check_end(curve,0,start,out)||!check_end(curve,1,end,out))
            return quarantine;
    }
    if(!census(part,after,out,"after_prepare")||after!=expected)return quarantine;
    seamTrial.prepared=true;
    return out.log("prepared=%d chart_closed=1 surface_invoked=0\n",count)?0:quarantine;
}
extern "C" __declspec(dllexport)
int __cdecl nx12_periodic_hole_surface(unsigned part,const char* path) {
    Evidence out(path);
    if(!out.good||!seamTrial.prepared||seamTrial.part!=part)return quarantine;
    seamTrial.prepared=false;
    auto u=std::to_array(seamU);auto v=std::to_array(seamV);
    auto poles=std::to_array(seamPoles);
    int counts[2]={12,4},senses[16];
    std::fill_n(senses,seamTrial.count,1);
    const int loops=seamTrial.count==12?1:2;
    tag_t sheet=NULL_TAG;
    int knot=-1,pole=-1;
    if(!out.log("before UF_MODL_cre_2dtrim_bsurf nu=14 nv=4 ku=5 kv=4 loops=%d count=%d tol_mm=.001 candidate=seam_realized_periphery\n",loops,seamTrial.count))
        return quarantine;
    int rc=UF_MODL_cre_2dtrim_bsurf(14,4,5,4,u.data(),v.data(),poles.data(),loops,counts,senses,seamTrial.curves,.001,&sheet,&knot,&pole);
    if(!out.log("after UF_MODL_cre_2dtrim_bsurf rc=%d\n",rc)||rc!=0)return quarantine;
    if(!out.log("surface_result tag=%u knot_fix=%d pole_fix=%d\n",sheet,knot,pole)||!sheet)return quarantine;
    for(int i=0;i<seamTrial.count;++i) {
        const auto tag=seamTrial.curves[i];
        if(!out.log("before UF_OBJ_ask_status tag=%u\n",tag))return quarantine;
        const int status=UF_OBJ_ask_status(tag);
        if(!out.log("after UF_OBJ_ask_status status=%d\n",status))return quarantine;
        if(status==UF_OBJ_DELETED)continue;
        if(status!=UF_OBJ_ALIVE&&status!=UF_OBJ_TEMPORARY)return quarantine;
        if(!out.log("before UF_OBJ_delete_object tag=%u\n",tag))return quarantine;
        rc=UF_OBJ_delete_object(tag);
        if(!out.log("after UF_OBJ_delete_object rc=%d\n",rc)||rc!=0)return quarantine;
        if(!out.log("before UF_OBJ_ask_status deletion tag=%u\n",tag))return quarantine;
        const int deleted=UF_OBJ_ask_status(tag);
        if(!out.log("after UF_OBJ_ask_status deletion status=%d\n",deleted)||deleted!=UF_OBJ_DELETED)
            return quarantine;
    }
    Inventory after;
    if(!census(part,after,out,"after_cleanup"))return quarantine;
    for(int i=0;i<seamTrial.count;++i)if(after.count(seamTrial.curves[i]))return quarantine;
    return out.log("construction_curves_absent=1 host_acceptance_pending=1\n")?0:quarantine;
}
