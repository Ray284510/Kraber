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

bool create_curve(const double a[2], const double b[2], tag_t& tag, Evidence& out, int index, bool nullStates) {
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
    if(!out.log("state_output_argument_null=%d\n",nullStates))return false;
    const int rc=UF_CURVE_create_spline(&input,&tag,&states,nullStates?nullptr:&issues);
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

int trial(tag_t part,int mode,Evidence& out) {
    const bool nullStates=mode==5;
    if(nullStates)mode=1;
    Inventory before,created,after,returned;
    if(!out.log("before UF_OBJ_cycle_all phase=before\n")||!census(part,before,out,"before"))return quarantine;
    tag_t curves[4]{};
    const double independentA[2]={.05,.95}, independentB[2]={.95,.95};
    for(int i=0;i<mode;++i) {
        const double* a=mode==2&&i==1?independentA:coordinates[i];
        const double* b=mode==2&&i==1?independentB:coordinates[(i+1)%4];
        if(!create_curve(a,b,curves[i],out,i,nullStates)||before.count(curves[i])||!returned.insert(curves[i]).second)
            return quarantine;
    }
    if(!out.log("before UF_OBJ_cycle_all phase=created\n")||!census(part,created,out,"created"))return quarantine;
    Inventory expected=before;
    expected.insert(returned.begin(),returned.end());
    if(expected!=created)return quarantine;
    for(int i=0;i<mode;++i) {
        if(!verify_curve(curves[i],part,out))return quarantine;
        const double* expectedA=mode==2&&i==1?independentA:coordinates[i];
        const double* expectedB=mode==2&&i==1?independentB:coordinates[(i+1)%4];
        for(int j=0;j<2;++j) {
            double xyz[3]{};
            if(!out.log("before UF_CURVE_evaluate_curve tag=%u param=%d\n",curves[i],j))return quarantine;
            int rc=UF_CURVE_evaluate_curve(curves[i],static_cast<double>(j),UF_CURVE_LOC,xyz);
            if(!out.log("after UF_CURVE_evaluate_curve rc=%d xyz=%.17g,%.17g,%.17g\n",rc,xyz[0],xyz[1],xyz[2])||rc)
                return quarantine;
            const double* target=j?expectedB:expectedA;
            if(std::hypot(xyz[0]-target[0],xyz[1]-target[1])>1e-9||std::abs(xyz[2])>1e-9)
                return quarantine;
        }
    }
    for(int i=0;i<mode;++i) {
        if(!out.log("before UF_OBJ_delete_object tag=%u\n",curves[i]))return quarantine;
        int rc=UF_OBJ_delete_object(curves[i]);
        if(!out.log("after UF_OBJ_delete_object rc=%d\n",rc)||rc)return quarantine;
        if(!out.log("before UF_OBJ_ask_status deleted_tag=%u\n",curves[i]))return quarantine;
        const int status=UF_OBJ_ask_status(curves[i]);
        if(!out.log("after UF_OBJ_ask_status deleted_status=%d\n",status)||status!=UF_OBJ_DELETED)
            return quarantine;
    }
    if(!out.log("before UF_OBJ_cycle_all phase=after\n")||!census(part,after,out,"after")||after!=before)
        return quarantine;
    return out.log("success exact_inventory_restored=1 spline_count=%d save_permitted=0\n",mode)?0:quarantine;
}
}
extern "C" __declspec(dllexport)
int __cdecl nx12_nurbs_spline_probe(unsigned part,int mode,const char* path) {
    Evidence out(path);
    if(!out.good||!out.log("pid=%lu part=%u mode=%d before UF_is_initialized\n",GetCurrentProcessId(),part,mode))return 101;
    const bool initialized=UF_is_initialized();
    if(!out.log("after UF_is_initialized initialized=%d\n",initialized)||!initialized
       ||!part||(mode!=1&&mode!=2&&mode!=4)) return quarantine;
    const int rc=trial(static_cast<tag_t>(part),mode,out);
    if(rc)out.log("quarantine no further NX calls save or replay rc=%d\n",rc);
    return rc;
}
namespace {
constexpr double innerA[4][2]={{.20,.25},{.20,.45},{.38,.45},{.38,.25}};
constexpr double innerB[4][2]={{.58,.53},{.58,.76},{.79,.76},{.79,.53}};
struct FixtureState {
    tag_t part=NULL_TAG;
    tag_t curves[12]{};
    unsigned holes=0;
    Inventory original;
    bool prepared=false;
    bool attempted=false;
} fixture;
}
extern "C" __declspec(dllexport)
int __cdecl nx12_prepare_trim_fixture(unsigned part,unsigned holes,const char* path) {
    Evidence out(path);
    if(!out.good||!out.log("pid=%lu part=%u holes=%u before UF_is_initialized\n",GetCurrentProcessId(),part,holes))
        return 101;
    const bool initialized=UF_is_initialized();
    if(!out.log("after UF_is_initialized initialized=%d\n",initialized)||!initialized||!part||holes>2||fixture.attempted)
        return quarantine;
    fixture.attempted=true;
    fixture.part=part;
    fixture.holes=holes;
    if(!out.log("before UF_OBJ_cycle_all phase=before\n")||!census(part,fixture.original,out,"before"))return quarantine;
    const double (*loops[])[2]={coordinates,innerA,innerB};
    Inventory created;
    for(unsigned l=0;l<=holes;++l)for(unsigned j=0;j<4;++j) {
        unsigned i=4*l+j;
        if(!create_curve(loops[l][j],loops[l][(j+1)%4],fixture.curves[i],out,static_cast<int>(i),false)
            ||fixture.original.count(fixture.curves[i])||!created.insert(fixture.curves[i]).second) return quarantine;
    }
    Inventory after;
    if(!out.log("before UF_OBJ_cycle_all phase=created\n")||!census(part,after,out,"created"))return quarantine;
    Inventory expected=fixture.original;
    expected.insert(created.begin(),created.end());
    if(after!=expected)return quarantine;
    for(auto tag:created)if(!verify_curve(tag,part,out))return quarantine;
    fixture.prepared=true;
    return out.log("prepared curves=%u matching_inventory=1 surface_invoked=0\n",4*(holes+1))?0:quarantine;
}
extern "C" __declspec(dllexport)
int __cdecl nx12_finish_trim_fixture(unsigned part,const char* path) {
    Evidence out(path);
    if(!out.good||!fixture.prepared||fixture.part!=part)return quarantine;
    fixture.prepared=false;
    double knots[]={0,0,0,0,1,1,1,1};
    double poles[64]{};
    for(int j=0;j<4;++j)for(int i=0;i<4;++i) {
        double* p=poles+4*(j*4+i);
        p[0]=40.*i/3.; p[1]=30.*j/3.;
        p[2]=(i==1||i==2?3.:0.)+(j==1||j==2?4.:0.)+4.*i*j/9.;
        p[3]=1.0;
        if(!out.log("input pole=%d xyzW=%.17g,%.17g,%.17g,%.17g\n",j*4+i,p[0],p[1],p[2],p[3]))
            return quarantine;
    }
    int counts[]={4,4,4};
    int senses[12]{};
    int loops=static_cast<int>(fixture.holes)+1;
    for(int i=0;i<loops*4;++i) {
        senses[i]=1;
        if(!out.log("input loop=%d slot=%d sense=1 curve_tag=%u\n",i/4,i%4,fixture.curves[i]))return quarantine;
    }
    tag_t sheet=NULL_TAG;int knotFix=-1,poleFix=-1;
    if(!out.log("before UF_MODL_cre_2dtrim_bsurf nu=4 nv=4 ku=4 kv=4 knots=0,0,0,0,1,1,1,1 loops=%d tolerance_mm=0.001\n",loops))
        return quarantine;
    int rc=UF_MODL_cre_2dtrim_bsurf(4,4,4,4,knots,knots,poles,loops,counts,senses,fixture.curves,.001,&sheet,&knotFix,&poleFix);
    if(!out.log("after UF_MODL_cre_2dtrim_bsurf rc=%d\n",rc)||rc)return quarantine;
    if(!out.log("surface_result tag=%u knot_fix=%d pole_fix=%d\n",sheet,knotFix,poleFix)||sheet==NULL_TAG)
        return quarantine;
    for(int i=0;i<loops*4;++i) {
        const tag_t tag=fixture.curves[i];
        if(!out.log("before UF_OBJ_ask_status tag=%u\n",tag))return quarantine;
        int status=UF_OBJ_ask_status(tag);
        if(!out.log("after UF_OBJ_ask_status status=%d\n",status))return quarantine;
        if(status==UF_OBJ_DELETED)continue;
        if(status!=UF_OBJ_ALIVE&&status!=UF_OBJ_TEMPORARY)return quarantine;
        if(!out.log("before UF_OBJ_delete_object tag=%u\n",tag))return quarantine;
        rc=UF_OBJ_delete_object(tag);
        if(!out.log("after UF_OBJ_delete_object rc=%d\n",rc)||rc)return quarantine;
        if(!out.log("before UF_OBJ_ask_status deleted_tag=%u\n",tag))return quarantine;
        status=UF_OBJ_ask_status(tag);
        if(!out.log("after UF_OBJ_ask_status deleted_status=%d\n",status)||status!=UF_OBJ_DELETED)
            return quarantine;
    }
    Inventory finalTags;
    if(!out.log("before UF_OBJ_cycle_all phase=after\n")||!census(part,finalTags,out,"after"))return quarantine;
    for(int i=0;i<loops*4;++i)if(finalTags.count(fixture.curves[i]))return quarantine;
    return out.log("success construction_curves_absent=1 host_validation_required=1\n")?0:quarantine;
}
