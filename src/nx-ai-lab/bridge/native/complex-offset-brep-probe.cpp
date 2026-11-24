// One read-only UF_BREP graph traversal. Compiled against the installed NX 12 headers.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <cmath>
#include <uf.h>
#include <uf_brep.h>
#include <uf_obj.h>
static_assert(sizeof(tag_t)==4 && sizeof(void*)==8 && sizeof(UF_BREP_topology_t)==40, "NX12 x64 layout mismatch");
namespace {
struct Log {
    HANDLE file;
    explicit Log(const char* path):file(CreateFileA(path,GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_WRITE_THROUGH,nullptr)){}
    ~Log(){if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);}
    bool write(const char* fmt,...) {
        if(file==INVALID_HANDLE_VALUE)return false;
        char buf[512];va_list ap;va_start(ap,fmt);int n=vsnprintf(buf,sizeof(buf),fmt,ap);va_end(ap);
        DWORD written=0;
        return n>0&&n<sizeof(buf)&&WriteFile(file,buf,static_cast<DWORD>(n),&written,nullptr)&&written==static_cast<DWORD>(n)&&FlushFileBuffers(file);
    }
};
bool knots(Log& log,const char* axis,const double* ptr,int n) {
    if(!ptr)return false;
    for(int i=0;i<n;++i)
        if(!std::isfinite(ptr[i])||!log.write("knot axis=%s i=%d value=%.17g\n",axis,i,ptr[i]))return false;
    return true;
}
bool poles(Log& log,const double (*p)[4],int n) {
    if(!p)return false;
    for(int i=0;i<n;++i) {
        for(double x:p[i])if(!std::isfinite(x))return false;
        if(!log.write("pole i=%d data=%.17g,%.17g,%.17g,%.17g\n",i,p[i][0],p[i][1],p[i][2],p[i][3]))return false;
    }
    return true;
}
bool extent(int n,int k) {return n>0&&n<=4096&&k>0&&k<=n;}
bool dump(const UF_BREP_geometry_t& g,Log& log,int depth) {
    if(depth>2||!log.write("geometry depth=%d type=%d\n",depth,static_cast<int>(g.type)))return false;
    switch(g.type) {
    case UF_BREP_OFFSET_GEOM:
        return std::isfinite(g.geom.offset.distance)&&g.geom.offset.base&&
            log.write("offset distance_mm=%.17g\n",g.geom.offset.distance)&&dump(*g.geom.offset.base,log,depth+1);
    case UF_BREP_BSURFACE_GEOM: {
        const auto& b=g.geom.bsurface;
        if(!extent(b.num_poles_u,b.order_u)||!extent(b.num_poles_v,b.order_v)||b.num_poles_u*b.num_poles_v>65536)
            return false;
        return log.write("surface nu=%d nv=%d ou=%d ov=%d rational=%d\n",
            b.num_poles_u,b.num_poles_v,b.order_u,b.order_v,b.is_rational)&&
            knots(log,"u",b.knots_u,b.num_poles_u+b.order_u)&&knots(log,"v",b.knots_v,b.num_poles_v+b.order_v)&&
            poles(log,b.poles,b.num_poles_u*b.num_poles_v);
    }
    case UF_BREP_PLANE_GEOM: {
        const auto& p=g.geom.plane;
        return log.write("plane origin=%.17g,%.17g,%.17g normal=%.17g,%.17g,%.17g u_ref=%.17g,%.17g,%.17g\n",
            p.pt_on_plane[0],p.pt_on_plane[1],p.pt_on_plane[2],
            p.normal[0],p.normal[1],p.normal[2],p.u0_ref_direc[0],p.u0_ref_direc[1],p.u0_ref_direc[2]);
    }
    case UF_BREP_SPLINE_GEOM: {
        const auto& s=g.geom.spline;
        return extent(s.num_poles,s.order)&&std::isfinite(s.start_param)&&std::isfinite(s.end_param)&&
            log.write("spline poles=%d order=%d rational=%d start=%.17g end=%.17g\n",
                s.num_poles,s.order,s.is_rational,s.start_param,s.end_param)&&
            knots(log,"t",s.knots,s.num_poles+s.order)&&poles(log,s.poles,s.num_poles);
    }
    default: return log.write("unsupported_geometry_payload=1\n");
    }
}
struct Extraction {
    double curve;
    double surface;
    int topology_option;
    UF_BREP_orientation_t requested_sense=UF_BREP_ORIENTATION_FORWARD;
    unsigned first_edge=0;
    unsigned second_edge=0;
};
bool geometry(const UF_BREP_topology_t* node,int id,Log& log,const Extraction& extraction) {
    int option[]={UF_BREP_BSURF_DIST_OPTION,UF_BREP_CURVE_DIST_OPTION};
    double values[]={extraction.surface,extraction.curve};
    UF_BREP_options_t options{2,option,values};
    UF_BREP_geom_type_t from[]={UF_BREP_BLEND_GEOM,UF_BREP_INTERSECTION_GEOM};
    UF_BREP_geom_type_t to[]={UF_BREP_BSURFACE_GEOM,UF_BREP_SPLINE_GEOM};
    UF_BREP_mapping_t mapping{2,from,to};
    UF_BREP_geometry_t out{};
    auto sense=extraction.requested_sense;
    int count=0;UF_BREP_state_t* states=nullptr;
    if(!log.write("before ask_geometry id=%d topology_type=%d\n",id,static_cast<int>(node->type)))return false;
    int rc=UF_BREP_ask_geometry(node,&options,&mapping,&out,&sense,&count,&states);
    const bool reported=log.write("after ask_geometry id=%d rc=%d\n",id,rc);
    // The NX12 contract does not specify usable output on a failed query.
    if(rc)return false;
    bool logged=reported&&log.write("geometry_receipt id=%d sense=%d states=%d\n",id,static_cast<int>(sense),count)&&dump(out,log,0);
    if(states)UF_free(states);
    const int release=UF_BREP_free_geometry_data(&out,nullptr);
    return log.write("geometry_release id=%d rc=%d\n",id,release)&&logged&&!release&&count==0;
}
bool walk(const UF_BREP_topology_t* node,int depth,int& seen,Log& log,const Extraction& extraction) {
    if(!node||depth>8||++seen>128||node->num_children<0||node->num_children>32||
       (node->num_children&&!node->children))return false;
    int id=0;UF_BREP_ask_identifier(node,&id);
    if(!log.write("node depth=%d id=%d type=%d tag=%u child_count=%d\n",depth,id,static_cast<int>(node->type),node->tag,node->num_children))
        return false;
    if(node->type==UF_BREP_EDGE_TOPO) {
        int ec=0,gc=0;
        const int r=UF_BREP_ask_edge_class(node,&ec,&gc);
        if(!log.write("edge_class id=%d rc=%d topology=%d geometry=%d\n",id,r,ec,gc)||r)return false;
    }
    const bool geometry_requested=extraction.first_edge
        ? node->type==UF_BREP_FIN_TOPO&&node->num_children==1&&node->children[0].child&&
          (node->children[0].child->tag==extraction.first_edge||node->children[0].child->tag==extraction.second_edge)
        : extraction.topology_option==UF_BREP_EDGE3D_OPTION
        ? node->type==UF_BREP_EDGE_TOPO
        : node->type==UF_BREP_FACE_TOPO||node->type==UF_BREP_FIN_TOPO;
    if(geometry_requested&&!geometry(node,id,log,extraction))return false;
    for(int i=0;i<node->num_children;++i) {
        const auto& c=node->children[i];
        if(!log.write("use parent=%d index=%d orientation=%d\n",id,i,c.orientation)||!walk(c.child,depth+1,seen,log,extraction))
            return false;
    }
    return true;
}
int capture(unsigned target,const Extraction& extraction,const char* path,bool legacy) {
    Log log(path);
    if(!UF_is_initialized())return 101;
    if(legacy) {
        if(!log.write("before ask_topology pid=%lu target=%u FINSP=1 tol=%.17g\n",
                GetCurrentProcessId(),target,extraction.curve))return 101;
    } else if(!log.write("before ask_topology pid=%lu target=%u option=%d enabled=1 curve_mm=%.17g bsurf_mm=%.17g\n",
            GetCurrentProcessId(),target,extraction.topology_option,extraction.curve,extraction.surface))return 101;
    int option=extraction.topology_option;
    double value=1.;
    UF_BREP_options_t options{1,&option,&value};
    UF_BREP_topology_t* graph=nullptr;UF_BREP_state_t* states=nullptr;
    int count=0;
    int rc=UF_BREP_ask_topology(target,&options,&graph,&count,&states);
    const bool reported=log.write("after ask_topology rc=%d\n",rc);
    if(rc)return 102;
    if(states)UF_free(states);
    if(!graph)return 102;
    if(!reported||count!=0) {
        // Successful topology output is owned even when the audit cannot proceed.
        UF_BREP_release_topology(graph,nullptr);
        return 103;
    }
    if(extraction.first_edge) {
        UF_BREP_topo_source_t source=UF_BREP_APP_BODY_SOURCE;
        const int source_rc=UF_BREP_ask_topology_source(graph,&source);
        if(!log.write("topology_source rc=%d source=%d requested_sense=%d edges=%u,%u\n",
                source_rc,static_cast<int>(source),static_cast<int>(extraction.requested_sense),extraction.first_edge,extraction.second_edge)||source_rc) {
            UF_BREP_release_topology(graph,nullptr);
            return 103;
        }
    }
    int seen=0;
    if(!walk(graph,0,seen,log,extraction)) {
        log.write("quarantine after geometry or topology guard; no uncertain cleanup\n");
        return 103;
    }
    const bool logged=log.write("before release_topology visited=%d\n",seen);
    rc=UF_BREP_release_topology(graph,nullptr);
    const bool logged_release=log.write("after release rc=%d\n",rc);
    return logged&&logged_release&&!rc?0:104;
}
}
extern "C" __declspec(dllexport)
int __cdecl nx12_offset_fin_geometry(unsigned face,int refined,const char* path) {
    const double tol=refined?.00001:.001;
    return capture(face,Extraction{tol,tol,UF_BREP_FINSP_OPTION},path,true);
}
extern "C" __declspec(dllexport)
int __cdecl nx12_fin_representation_geometry(unsigned body,int variant,int edge_mode,const char* path) {
    // Only the two mixed settings use these previously accepted values.
    if((variant!=1&&variant!=2)||(edge_mode!=0&&edge_mode!=1))return 105;
    const double curve=variant==1?.00001:.001;
    const double surface=variant==1?.001:.00001;
    const int option=edge_mode?UF_BREP_EDGE3D_OPTION:UF_BREP_FINSP_OPTION;
    return capture(body,Extraction{curve,surface,option},path,false);
}

extern "C" __declspec(dllexport)
int __cdecl nx12_fin_edge3d_supplement(unsigned body,int variant,const char* path) {
    if(variant!=0&&variant!=3)return 105;
    const double distance=variant==0?.001:.00001;
    return capture(body,Extraction{distance,distance,UF_BREP_EDGE3D_OPTION},path,false);
}

extern "C" __declspec(dllexport)
int __cdecl nx12_finsp_formation(unsigned body,unsigned first_edge,unsigned second_edge,const char* path) {
    if(!body||!first_edge||!second_edge||first_edge==second_edge)return 105;
    const Extraction extraction{.00001,.001,UF_BREP_FINSP_OPTION,UF_BREP_ORIENTATION_REVERSE,first_edge,second_edge};
    return capture(body,extraction,path,false);
}
