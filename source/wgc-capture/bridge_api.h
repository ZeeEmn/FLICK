#pragma once
#include <windows.h>
#include <d3d11.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct RwWgcBridge RwWgcBridge;
typedef struct RwWgcFrame { unsigned surface; uint64_t sequence; int64_t qpc; } RwWgcFrame;
typedef struct RwPacingStats {
    unsigned requested, supported, applied;
    int32_t query_hr, set_hr, read_hr;
    int64_t requested_interval, effective_interval;
} RwPacingStats;
typedef struct RwWgcStats {
    uint64_t arrived, copies, pool_busy, errors, resized, rate_limited, idle_drops;
    unsigned opened_textures, opened_fence, target_closed;
    int64_t qpc_frequency;
    RwPacingStats pacing;
    unsigned immediate_context;
    uint64_t full_copies, region_copies, clears, signals, flushes, descriptor_queries;
} RwWgcStats;
typedef int (WINAPI *RwWgcStart)(ID3D11Device*, ID3D11Texture2D* const*, unsigned, unsigned, unsigned, HWND, RwWgcBridge**);
// 1 = GPU-ready lease, 0 = idle, 2 = end of source, negative = failure.
typedef int (WINAPI *RwWgcNext)(RwWgcBridge*, RwWgcFrame*);
typedef int (WINAPI *RwWgcRelease)(RwWgcBridge*, unsigned);
typedef void (WINAPI *RwWgcWake)(RwWgcBridge*);
typedef void (WINAPI *RwWgcStop)(RwWgcBridge*);
typedef int (WINAPI *RwWgcClose)(RwWgcBridge*, RwWgcStats*);
#ifdef __cplusplus
}
#endif
