#pragma once
#include <windows.h>
#include <d3d11.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct RwDesktopBridge RwDesktopBridge;
typedef struct RwDesktopFrame { unsigned surface; uint64_t sequence; int64_t qpc; } RwDesktopFrame;
typedef struct RwDesktopStats {
    uint64_t arrived,copies,pool_busy,errors,resized,rate_limited,idle_drops;
    unsigned opened_textures,opened_fence,target_closed;
    int64_t qpc_frequency;
    unsigned immediate_context;
    uint64_t full_copies,region_copies,clears,signals,flushes,descriptor_queries;
    uint64_t releases,timeouts,recoveries,max_hold_us,protected_frames;
    uint64_t accumulated_batches,coalesced_updates,max_accumulated;
} RwDesktopStats;
typedef int (WINAPI *RwDesktopStart)(ID3D11Device*,ID3D11Texture2D* const*,unsigned,unsigned,unsigned,RwDesktopBridge**);
typedef int (WINAPI *RwDesktopNext)(RwDesktopBridge*,RwDesktopFrame*);
typedef int (WINAPI *RwDesktopRelease)(RwDesktopBridge*,unsigned);
typedef void (WINAPI *RwDesktopWake)(RwDesktopBridge*);
typedef void (WINAPI *RwDesktopStop)(RwDesktopBridge*);
typedef int (WINAPI *RwDesktopClose)(RwDesktopBridge*,RwDesktopStats*);
#ifdef __cplusplus
}
#endif
