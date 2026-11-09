/* SPDX-License-Identifier: LGPL-2.1-or-later
 * Asynchronous primary-display Desktop Duplication source feeding the unchanged dev.9 queue.
 */
#include "config.h"
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#define COBJMACROS
#include <windows.h>
#include <d3d11.h>
#ifdef REWIND_PRODUCTION
__attribute__((constructor)) static void rewind_local_dll_search(void){SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);SetDllDirectoryW(L"");}
#endif
#include <inttypes.h>
#include <stdatomic.h>
#include "libavutil/avassert.h"
#include "libavutil/hwcontext.h"
#include "libavutil/hwcontext_d3d11va.h"
#include "libavutil/opt.h"
#include "libavutil/mem.h"
#include "libavutil/time.h"
#include "avfilter.h"
#include "filters.h"
#include "video.h"
#include "rewind_queue.h"
#include "rewind_desktop_bridge_api.h"

typedef struct RewindDesktopContext {
    const AVClass *class;
    int width, height, fps, spin_us;
    AVBufferRef *device, *frames;
    AVFrame *pool[RWQ_CAPACITY], *messages[RWQ_CAPACITY];
    uint64_t sequences[RWQ_CAPACITY];
    unsigned held;
    RewindQueue queue;
    HMODULE library;
    RwDesktopBridge *bridge;
    RwDesktopNext next;
    RwDesktopRelease release;
    RwDesktopWake wake;
    RwDesktopStop stop;
    RwDesktopClose close;
    HANDLE producer, notification;
    int64_t spin_frequency;
    uint64_t spin_hits, event_waits;
    atomic_int stopping, error, ended;
    int64_t epoch, frequency, previous_qpc, previous_pts;
    uint64_t previous_sequence, published, replaced, stale, source_checks, invalid;
    uint64_t delivered, last_delivered_sequence, output_invalid;
} RewindDesktopContext;
#define OFFSET(name) offsetof(RewindDesktopContext, name)
#define FLAGS AV_OPT_FLAG_VIDEO_PARAM | AV_OPT_FLAG_FILTERING_PARAM
static const AVOption rewinddesktop_options[] = {
    { "spin_us", "bounded consumer spin budget before event parking (0 disables)", OFFSET(spin_us), AV_OPT_TYPE_INT, {.i64=50}, 0, 200, FLAGS },
    { "width", "fixed output width", OFFSET(width), AV_OPT_TYPE_INT, {.i64=1920}, 320, 3840, FLAGS },
    { "height", "fixed output height", OFFSET(height), AV_OPT_TYPE_INT, {.i64=1080}, 180, 2160, FLAGS },
    { "fps", "maximum Desktop Duplication capture rate", OFFSET(fps), AV_OPT_TYPE_INT, {.i64=60}, 1, 120, FLAGS },
    { NULL }
};
AVFILTER_DEFINE_CLASS(rewinddesktop);

static av_cold int desktop_init(AVFilterContext *avctx)
{
    RewindDesktopContext *s = avctx->priv;
    rwq_init(&s->queue);
    atomic_init(&s->stopping, 0);
    atomic_init(&s->error, 0);
    atomic_init(&s->ended, 0);
    s->previous_pts = AV_NOPTS_VALUE;
    return rwq_lock_free(&s->queue) ? 0 : AVERROR(ENOSYS);
}

// Formatting and log I/O run only on this optional integrity-test thread.
static DWORD WINAPI desktop_producer(void *opaque)
{
    AVFilterContext *avctx = opaque;
    RewindDesktopContext *s = avctx->priv;
    int i, ret = 0, slot;
    uint64_t previous;
    RwDesktopFrame captured;
    while (!atomic_load_explicit(&s->stopping, memory_order_acquire)) {
        for (i = 0; i < RWQ_CAPACITY; ++i) {
            if ((s->held & (1u << i)) && av_buffer_get_ref_count(s->pool[i]->buf[0]) == 1) {
                if (s->release(s->bridge, i) < 0) { ret = AVERROR_EXTERNAL; goto finished; }
                s->held &= ~(1u << i);
            }
        }
        ret = s->next(s->bridge, &captured);
        if (ret < 0) { ret = AVERROR_EXTERNAL; break; }
        if (ret == 2) { ret = 0; atomic_store_explicit(&s->ended, 1, memory_order_release); break; }
        if (!ret) continue;
        if (captured.surface >= RWQ_CAPACITY || (s->held & (1u << captured.surface))) {
            ret = AVERROR_BUG; break;
        }
        ++s->source_checks;
        if (captured.qpc <= s->previous_qpc || captured.sequence <= s->previous_sequence) {
            ++s->invalid; ret = AVERROR_INVALIDDATA; break;
        }
        if (!s->epoch) s->epoch = captured.qpc;
        s->previous_qpc = captured.qpc;
        s->previous_sequence = captured.sequence;
        s->held |= 1u << captured.surface;
        slot = rwq_write_claim(&s->queue, 0, &previous);
        if (slot < 0) continue; // The unqueued lease is returned on the next iteration.
        if ((previous & 3) == RWQ_READY) ++s->replaced;
        av_frame_unref(s->messages[slot]);
        ret = av_frame_ref(s->messages[slot], s->pool[captured.surface]);
        if (ret < 0) {
            rwq_write_cancel(&s->queue, slot, previous & ~UINT64_C(3));
            break;
        }
        s->messages[slot]->pts = av_rescale(captured.qpc - s->epoch, AV_TIME_BASE, s->frequency);
        s->sequences[slot] = captured.sequence;
        rwq_publish(&s->queue, slot);
        ++s->published;
        SetEvent(s->notification);
    }
finished:
    if (ret < 0 && !atomic_load_explicit(&s->stopping, memory_order_acquire))
        atomic_store_explicit(&s->error, ret, memory_order_release);
    SetEvent(s->notification);
    return 0;
}

static int desktop_config(AVFilterLink *outlink)
{
    AVFilterContext *avctx = outlink->src;
    RewindDesktopContext *s = avctx->priv;
    FilterLink *link = ff_filter_link(outlink);
    AVHWFramesContext *frames;
    AVD3D11VAFramesContext *hardware;
    AVHWDeviceContext *device;
    AVD3D11VADeviceContext *d3d;
    ID3D11Texture2D *textures[RWQ_CAPACITY];
    RwDesktopStart start;
    LARGE_INTEGER spin_frequency;
    wchar_t library_path[32768], *last;
    
    int ret, i;
    DWORD length;
    if (avctx->hw_device_ctx) {
        device = (AVHWDeviceContext *)avctx->hw_device_ctx->data;
        if (device->type != AV_HWDEVICE_TYPE_D3D11VA) return AVERROR(EINVAL);
        s->device = av_buffer_ref(avctx->hw_device_ctx);
        if (!s->device) return AVERROR(ENOMEM);
    } else {
        ret = av_hwdevice_ctx_create(&s->device, AV_HWDEVICE_TYPE_D3D11VA, NULL, NULL, 0);
        if (ret < 0) return ret;
    }
    device = (AVHWDeviceContext *)s->device->data;
    d3d = device->hwctx;
    s->frames = av_hwframe_ctx_alloc(s->device);
    if (!s->frames) return AVERROR(ENOMEM);
    frames = (AVHWFramesContext *)s->frames->data;
    frames->format = AV_PIX_FMT_D3D11;
    frames->sw_format = AV_PIX_FMT_BGRA;
    frames->width = s->width;
    frames->height = s->height;
    frames->initial_pool_size = 0; // Independent NT handles, not one array-wide resource.
    hardware = frames->hwctx;
    hardware->BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    hardware->MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    ret = av_hwframe_ctx_init(s->frames);
    if (ret < 0) return ret;
    for (i = 0; i < RWQ_CAPACITY; ++i) {
        s->pool[i] = av_frame_alloc();
        s->messages[i] = av_frame_alloc();
        if (!s->pool[i] || !s->messages[i]) return AVERROR(ENOMEM);
        ret = av_hwframe_get_buffer(s->frames, s->pool[i], 0);
        if (ret < 0) return ret;
        if ((intptr_t)s->pool[i]->data[1] != 0) return AVERROR_BUG;
        textures[i] = (ID3D11Texture2D *)s->pool[i]->data[0];
        s->pool[i]->sample_aspect_ratio = (AVRational){1, 1};
        s->pool[i]->color_range = AVCOL_RANGE_JPEG;
        s->pool[i]->colorspace = AVCOL_SPC_RGB;
        s->pool[i]->color_primaries = AVCOL_PRI_BT709;
        s->pool[i]->color_trc = AVCOL_TRC_IEC61966_2_1;
    }
    length = GetModuleFileNameW(NULL, library_path, FF_ARRAY_ELEMS(library_path));
    if (!length || length >= FF_ARRAY_ELEMS(library_path)) return AVERROR_EXTERNAL;
    last = wcsrchr(library_path, L'\\');
    if (!last || (last - library_path) + 26 >= FF_ARRAY_ELEMS(library_path)) return AVERROR_EXTERNAL;
    wcscpy(last + 1, L"rewind_desktop_bridge.dll");
    s->library = LoadLibraryExW(library_path, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!s->library) {
        av_log(avctx, AV_LOG_ERROR, "Missing Desktop Duplication bridge DLL beside this worker.\n");
        return AVERROR_EXTERNAL;
    }
    start = (RwDesktopStart)GetProcAddress(s->library, "RwDesktopBridgeStart");
    s->next = (RwDesktopNext)GetProcAddress(s->library, "RwDesktopBridgeNext");
    s->release = (RwDesktopRelease)GetProcAddress(s->library, "RwDesktopBridgeRelease");
    s->wake = (RwDesktopWake)GetProcAddress(s->library, "RwDesktopBridgeWake");
    s->stop = (RwDesktopStop)GetProcAddress(s->library, "RwDesktopBridgeStop");
    s->close = (RwDesktopClose)GetProcAddress(s->library, "RwDesktopBridgeClose");
    if (!start || !s->next || !s->release || !s->wake || !s->stop || !s->close) return AVERROR_EXTERNAL;
    if (!QueryPerformanceFrequency(&spin_frequency)) return AVERROR_EXTERNAL;
    s->spin_frequency = spin_frequency.QuadPart;
    s->frequency = 10000000; // DXGI QPC timestamps normalized to 100ns by acquisition.
    s->notification = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!s->notification) return AVERROR_EXTERNAL;
    ret = start(d3d->device, textures, s->width, s->height, s->fps, &s->bridge);
    if (ret < 0) return AVERROR_EXTERNAL;
    link->hw_frames_ctx = av_buffer_ref(s->frames);
    if (!link->hw_frames_ctx) return AVERROR(ENOMEM);
    link->frame_rate = (AVRational){s->fps, 1};
    outlink->w = s->width;
    outlink->h = s->height;
    outlink->sample_aspect_ratio = (AVRational){1, 1};
    outlink->time_base = AV_TIME_BASE_Q;
    s->producer = CreateThread(NULL, 0, desktop_producer, avctx, 0, NULL);
    return s->producer ? 0 : AVERROR_EXTERNAL;
}

static int desktop_request(AVFilterLink *outlink)
{
    AVFilterContext *avctx = outlink->src;
    RewindDesktopContext *s = avctx->priv;
    int slot, error, ret;
    uint64_t claimed, sequence;
    AVFrame *frame;
    for (;;) {
        error = atomic_load_explicit(&s->error, memory_order_acquire);
        if (error < 0) return error;
        slot = rwq_read_claim(&s->queue, &claimed, &s->stale);
        if (slot >= 0) break;
        if (atomic_load_explicit(&s->ended, memory_order_acquire)) {
            ff_outlink_set_status(outlink, AVERROR_EOF, s->previous_pts == AV_NOPTS_VALUE ? 0 : s->previous_pts + AV_TIME_BASE / s->fps);
            return AVERROR_EOF;
        }
        // Bounded PAUSE spin, never a permanent busy loop competing with the game.
        if (s->spin_us) {
            LARGE_INTEGER begin, now;
            unsigned iterations = 0;
            QueryPerformanceCounter(&begin);
            do {
                YieldProcessor();
                slot = rwq_read_claim(&s->queue, &claimed, &s->stale);
                if (slot >= 0) { ++s->spin_hits; break; }
                if ((++iterations & 15) == 0) {
                    QueryPerformanceCounter(&now);
                    if (now.QuadPart - begin.QuadPart >= s->spin_frequency * s->spin_us / 1000000) break;
                    if (atomic_load_explicit(&s->ended, memory_order_acquire) || atomic_load_explicit(&s->error, memory_order_acquire)) break;
                }
            } while (1);
            if (slot >= 0) break;
        }
        // Auto-reset publication event wakes immediately; no periodic timeout polling.
        // End/error/shutdown also signal this event, including a minimized target.
        ++s->event_waits;
        if (WaitForSingleObject(s->notification, INFINITE) != WAIT_OBJECT_0) return AVERROR_EXTERNAL;
    }
    frame = av_frame_clone(s->messages[slot]);
    sequence = s->sequences[slot];
    av_frame_unref(s->messages[slot]);
    rwq_read_release(&s->queue, slot, claimed);
    s->wake(s->bridge); // Prompt a lease sweep; NEVER force an NVENC-held surface free.
    if (!frame) return AVERROR(ENOMEM);
    // Hold the real AVFrame reference under pressure. The producer must not
    // grant its shared texture while NVENC/downstream or this caller owns it.
    if ((s->previous_pts != AV_NOPTS_VALUE && frame->pts <= s->previous_pts)
        || sequence <= s->last_delivered_sequence) {
        ++s->output_invalid;
        av_frame_free(&frame);
        return AVERROR_INVALIDDATA;
    }
    s->previous_pts = frame->pts;
    s->last_delivered_sequence = sequence;
    ++s->delivered;
    ret = ff_filter_frame(outlink, frame);
    s->wake(s->bridge); // Downstream may have synchronously released the last reference.
    return ret;
}
static av_cold void desktop_uninit(AVFilterContext *avctx)
{
    RewindDesktopContext *s = avctx->priv;
    RwDesktopStats stats = {0};
    int i, clean = -1;
    atomic_store_explicit(&s->stopping, 1, memory_order_release);
    if (s->bridge && s->stop) s->stop(s->bridge);
    if (s->notification) SetEvent(s->notification);
    if (s->producer) {
        WaitForSingleObject(s->producer, INFINITE);
        CloseHandle(s->producer);
    }
    if (s->bridge && s->close) clean = s->close(s->bridge, &stats);
#ifndef REWIND_RELEASE_QUIET
    if (s->bridge) {
        av_log(avctx, AV_LOG_WARNING, "rewind_desktop_stats={\"Api\":\"DXGI Desktop Duplication\",\"AcquisitionThread\":true,\"FramePoolCapacity\":0,\"BackendPoolCapacity\":8,\"ArrivedFrames\":%"PRIu64",\"GpuCopySubmissions\":%"PRIu64",\"PoolBusySkips\":%"PRIu64",\"Reinitializations\":%"PRIu64",\"IdleDrops\":%"PRIu64",\"AcquisitionErrors\":%"PRIu64",\"OpenedNtTextures\":%u,\"OpenedHardwareFence\":%u,\"SourceTimestampChecks\":%"PRIu64",\"TimestampInvalidations\":%"PRIu64",\"DeliveredFrames\":%"PRIu64",\"ConsumerSpinUs\":%d,\"ConsumerSpinHits\":%"PRIu64",\"ConsumerEventWaits\":%"PRIu64",\"ReleaseFrames\":%"PRIu64",\"AcquireTimeouts\":%"PRIu64",\"AccessRecoveries\":%"PRIu64",\"MaxFrameHoldUs\":%"PRIu64",\"ProtectedFramesSkipped\":%"PRIu64",\"AccumulatedBatches\":%"PRIu64",\"CoalescedUpdates\":%"PRIu64",\"MaxAccumulatedFrames\":%"PRIu64",\"HoldIncludesAcquire\":true,\"CleanShutdown\":%s}\n",
            stats.arrived, stats.copies, stats.pool_busy, stats.resized, stats.idle_drops, stats.errors,
            stats.opened_textures, stats.opened_fence, s->source_checks, s->invalid+s->output_invalid,
            s->delivered, s->spin_us, s->spin_hits, s->event_waits, stats.releases, stats.timeouts, stats.recoveries, stats.max_hold_us, stats.protected_frames, stats.accumulated_batches, stats.coalesced_updates, stats.max_accumulated, clean==0?"true":"false");
        av_log(avctx, AV_LOG_WARNING, "rewind_desktop_commands={\"ImmediateContext\":%s,\"FullCopies\":%"PRIu64",\"RegionCopies\":%"PRIu64",\"Clears\":%"PRIu64",\"Signals\":%"PRIu64",\"Flushes\":%"PRIu64",\"DescriptorQueries\":%"PRIu64"}\n",
            stats.immediate_context?"true":"false",stats.full_copies,stats.region_copies,stats.clears,stats.signals,stats.flushes,stats.descriptor_queries);
    av_log(avctx, AV_LOG_WARNING, "rewind_queue_stats={\"AsyncProducer\":true,\"SyntheticSource\":false,\"PublishedFrames\":%"PRIu64",\"ReplacedUnreadFrames\":%"PRIu64",\"ConsumerStaleDrops\":%"PRIu64",\"PoolBusySkips\":%"PRIu64"}\n",
        s->published, s->replaced, s->stale, stats.pool_busy);
    }
#else
    (void)clean;
#endif
    for (i = 0; i < RWQ_CAPACITY; ++i) {
        av_frame_free(&s->messages[i]);
        av_frame_free(&s->pool[i]);
    }
    if (s->notification) CloseHandle(s->notification);
    if (s->library) FreeLibrary(s->library);
    av_buffer_unref(&s->frames);
    av_buffer_unref(&s->device);
}
static const AVFilterPad desktop_outputs[] = {
    { .name="default", .type=AVMEDIA_TYPE_VIDEO, .config_props=desktop_config, .request_frame=desktop_request },
};
const FFFilter ff_vsrc_rewinddesktop = {
    .p.name="rewinddesktop",
    .p.description=NULL_IF_CONFIG_SMALL("Asynchronous primary-display Desktop Duplication into the unchanged frame queue"),
    .p.priv_class=&rewinddesktop_class,
    .p.flags=AVFILTER_FLAG_HWDEVICE,
    .priv_size=sizeof(RewindDesktopContext),
    .init=desktop_init,
    .uninit=desktop_uninit,
    FILTER_OUTPUTS(desktop_outputs),
    FILTER_SINGLE_PIXFMT(AV_PIX_FMT_D3D11),
    .flags_internal=FF_FILTER_FLAG_HWFRAME_AWARE,
};
