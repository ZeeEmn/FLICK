/* SPDX-License-Identifier: LGPL-2.1-or-later
 * Out-of-process WGC HWND source feeding the unchanged dev.9 queue.
 */
#include "config.h"
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#define COBJMACROS
#include <windows.h>
#include <d3d11.h>
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
#include "rewind_wgc_bridge_api.h"

typedef struct WgcTrace { uint64_t sequence; int64_t pts; } WgcTrace;
#define WGC_TRACE_CAPACITY 4096
typedef struct RewindWgcContext {
    const AVClass *class;
    int width, height, fps, trace, test_delay_ms, spin_us, primary;
    int64_t hwnd;
    AVBufferRef *device, *frames;
    AVFrame *pool[RWQ_CAPACITY], *messages[RWQ_CAPACITY];
    uint64_t sequences[RWQ_CAPACITY];
    unsigned held;
    RewindQueue queue;
    HMODULE library;
    RwWgcBridge *bridge;
    RwWgcNext next;
    RwWgcRelease release;
    RwWgcWake wake;
    RwWgcStop stop;
    RwWgcClose close;
    HANDLE producer, notification, logger, trace_notification;
    WgcTrace *traces;
    atomic_uint trace_write, trace_read;
    int64_t spin_frequency;
    uint64_t spin_hits, event_waits;
    atomic_int stopping, error, ended;
    int64_t epoch, frequency, previous_qpc, previous_pts;
    uint64_t previous_sequence, published, replaced, stale, source_checks, invalid;
    uint64_t delivered, last_delivered_sequence, output_invalid;
} RewindWgcContext;
#define OFFSET(name) offsetof(RewindWgcContext, name)
#define FLAGS AV_OPT_FLAG_VIDEO_PARAM | AV_OPT_FLAG_FILTERING_PARAM
static const AVOption rewindwgc_options[] = {
    { "primary", "capture primary monitor instead of controlled test HWND", OFFSET(primary), AV_OPT_TYPE_BOOL, {.i64=0}, 0, 1, FLAGS },
    { "spin_us", "bounded consumer spin budget before event parking (0 disables)", OFFSET(spin_us), AV_OPT_TYPE_INT, {.i64=50}, 0, 200, FLAGS },
    { "hwnd", "target HWND (decimal, required)", OFFSET(hwnd), AV_OPT_TYPE_INT64, {.i64=0}, 0, INT64_MAX, FLAGS },
    { "width", "fixed output width", OFFSET(width), AV_OPT_TYPE_INT, {.i64=1920}, 320, 3840, FLAGS },
    { "height", "fixed output height", OFFSET(height), AV_OPT_TYPE_INT, {.i64=1080}, 180, 2160, FLAGS },
    { "fps", "maximum WGC capture rate", OFFSET(fps), AV_OPT_TYPE_INT, {.i64=60}, 1, 120, FLAGS },
    { "trace", "emit delivered source markers for integrity tests", OFFSET(trace), AV_OPT_TYPE_BOOL, {.i64=0}, 0, 1, FLAGS },
    { "test_delay_ms", "controlled consumer lease stress delay", OFFSET(test_delay_ms), AV_OPT_TYPE_INT, {.i64=0}, 0, 1000, FLAGS },
    { NULL }
};
AVFILTER_DEFINE_CLASS(rewindwgc);

static av_cold int wgc_init(AVFilterContext *avctx)
{
    RewindWgcContext *s = avctx->priv;
    rwq_init(&s->queue);
    atomic_init(&s->stopping, 0);
    atomic_init(&s->error, 0);
    atomic_init(&s->ended, 0);
    atomic_init(&s->trace_write, 0);
    atomic_init(&s->trace_read, 0);
    s->previous_pts = AV_NOPTS_VALUE;
    return rwq_lock_free(&s->queue) ? 0 : AVERROR(ENOSYS);
}

// Formatting and log I/O run only on this optional integrity-test thread.
static DWORD WINAPI wgc_logger(void *opaque)
{
    AVFilterContext *avctx = opaque;
    RewindWgcContext *s = avctx->priv;
    for (;;) {
        unsigned read = atomic_load_explicit(&s->trace_read, memory_order_relaxed);
        unsigned write = atomic_load_explicit(&s->trace_write, memory_order_acquire);
        if (read != write) {
            WgcTrace row = s->traces[read % WGC_TRACE_CAPACITY];
            atomic_store_explicit(&s->trace_read, read + 1, memory_order_release);
            av_log(avctx, AV_LOG_WARNING, "rewind_wgc_frame={\"Sequence\":%"PRIu64",\"PtsUs\":%"PRId64"}\n", row.sequence, row.pts);
            continue;
        }
        if (atomic_load_explicit(&s->stopping, memory_order_acquire)) break;
        WaitForSingleObject(s->trace_notification, INFINITE);
    }
    return 0;
}

static DWORD WINAPI wgc_producer(void *opaque)
{
    AVFilterContext *avctx = opaque;
    RewindWgcContext *s = avctx->priv;
    int i, ret = 0, slot;
    uint64_t previous;
    RwWgcFrame captured;
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

static int wgc_config(AVFilterLink *outlink)
{
    AVFilterContext *avctx = outlink->src;
    RewindWgcContext *s = avctx->priv;
    FilterLink *link = ff_filter_link(outlink);
    AVHWFramesContext *frames;
    AVD3D11VAFramesContext *hardware;
    AVHWDeviceContext *device;
    AVD3D11VADeviceContext *d3d;
    ID3D11Texture2D *textures[RWQ_CAPACITY];
    RwWgcStart start;
    typedef int (WINAPI *BorderInfo)(RwWgcBridge*,char*,unsigned);
    BorderInfo border_info;
    char border_json[768];
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
    wcscpy(last + 1, L"rewind_wgc_bridge.dll");
    s->library = LoadLibraryExW(library_path, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!s->library) {
        av_log(avctx, AV_LOG_ERROR, "Missing WGC bridge DLL beside this worker.\n");
        return AVERROR_EXTERNAL;
    }
    start = (RwWgcStart)GetProcAddress(s->library, s->primary ? "RwWgcBridgeStartPrimary" : "RwWgcBridgeStart");
    s->next = (RwWgcNext)GetProcAddress(s->library, "RwWgcBridgeNext");
    s->release = (RwWgcRelease)GetProcAddress(s->library, "RwWgcBridgeRelease");
    s->wake = (RwWgcWake)GetProcAddress(s->library, "RwWgcBridgeWake");
    s->stop = (RwWgcStop)GetProcAddress(s->library, "RwWgcBridgeStop");
    s->close = (RwWgcClose)GetProcAddress(s->library, "RwWgcBridgeClose");
    if (!start || !s->next || !s->release || !s->wake || !s->stop || !s->close) return AVERROR_EXTERNAL;
    if (!QueryPerformanceFrequency(&spin_frequency)) return AVERROR_EXTERNAL;
    s->spin_frequency = spin_frequency.QuadPart;
    s->frequency = 10000000; // WGC SystemRelativeTime uses 100ns units.
    s->notification = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!s->notification) return AVERROR_EXTERNAL;
    ret = start(d3d->device, textures, s->width, s->height, s->fps, (HWND)(uintptr_t)s->hwnd, &s->bridge);
    if (ret < 0) return AVERROR_EXTERNAL;
    border_info = (BorderInfo)GetProcAddress(s->library, "RwWgcBridgeBorderInfo");
    if (border_info && border_info(s->bridge, border_json, sizeof(border_json)) == 0)
        av_log(avctx, AV_LOG_WARNING, "rewind_wgc_border=%s\n", border_json);
    link->hw_frames_ctx = av_buffer_ref(s->frames);
    if (!link->hw_frames_ctx) return AVERROR(ENOMEM);
    link->frame_rate = (AVRational){s->fps, 1};
    outlink->w = s->width;
    outlink->h = s->height;
    outlink->sample_aspect_ratio = (AVRational){1, 1};
    outlink->time_base = AV_TIME_BASE_Q;
    if (s->trace) {
        s->traces = av_calloc(WGC_TRACE_CAPACITY, sizeof(*s->traces));
        s->trace_notification = CreateEventW(NULL, FALSE, FALSE, NULL);
        if (!s->traces || !s->trace_notification) return AVERROR(ENOMEM);
        s->logger = CreateThread(NULL, 0, wgc_logger, avctx, 0, NULL);
        if (!s->logger) return AVERROR_EXTERNAL;
        SetThreadPriority(s->logger, THREAD_PRIORITY_BELOW_NORMAL);
    }
    s->producer = CreateThread(NULL, 0, wgc_producer, avctx, 0, NULL);
    return s->producer ? 0 : AVERROR_EXTERNAL;
}

static int wgc_request(AVFilterLink *outlink)
{
    AVFilterContext *avctx = outlink->src;
    RewindWgcContext *s = avctx->priv;
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
    if (s->test_delay_ms) av_usleep((int64_t)s->test_delay_ms * 1000);
    if ((s->previous_pts != AV_NOPTS_VALUE && frame->pts <= s->previous_pts)
        || sequence <= s->last_delivered_sequence) {
        ++s->output_invalid;
        av_frame_free(&frame);
        return AVERROR_INVALIDDATA;
    }
    s->previous_pts = frame->pts;
    s->last_delivered_sequence = sequence;
    ++s->delivered;
    if (s->trace) {
        unsigned write = atomic_load_explicit(&s->trace_write, memory_order_relaxed);
        unsigned read = atomic_load_explicit(&s->trace_read, memory_order_acquire);
        if (write - read >= WGC_TRACE_CAPACITY) {
            av_frame_free(&frame);
            return AVERROR(ENOBUFS); // Integrity trace loss must fail, never silently pass.
        }
        s->traces[write % WGC_TRACE_CAPACITY] = (WgcTrace){sequence, frame->pts};
        atomic_store_explicit(&s->trace_write, write + 1, memory_order_release);
        SetEvent(s->trace_notification);
    }
    ret = ff_filter_frame(outlink, frame);
    s->wake(s->bridge); // Downstream may have synchronously released the last reference.
    return ret;
}
static av_cold void wgc_uninit(AVFilterContext *avctx)
{
    RewindWgcContext *s = avctx->priv;
    RwWgcStats stats = {0};
    int i, clean = -1;
    atomic_store_explicit(&s->stopping, 1, memory_order_release);
    if (s->bridge && s->stop) s->stop(s->bridge);
    if (s->notification) SetEvent(s->notification);
    if (s->producer) {
        WaitForSingleObject(s->producer, INFINITE);
        CloseHandle(s->producer);
    }
    if (s->trace_notification) SetEvent(s->trace_notification);
    if (s->logger) { WaitForSingleObject(s->logger, INFINITE); CloseHandle(s->logger); }
    if (s->bridge && s->close) clean = s->close(s->bridge, &stats);
    if (s->bridge) {
        av_log(avctx, AV_LOG_WARNING, "rewind_wgc_stats={\"Api\":\"WGC\",\"FramePoolCapacity\":2,\"ArrivedFrames\":%"PRIu64",\"GpuCopySubmissions\":%"PRIu64",\"PoolBusySkips\":%"PRIu64",\"ResizeEvents\":%"PRIu64",\"RateLimitedFrames\":%"PRIu64",\"IdleDrops\":%"PRIu64",\"AcquisitionErrors\":%"PRIu64",\"OpenedNtTextures\":%u,\"OpenedHardwareFence\":%u,\"TargetClosed\":%s,\"SourceTimestampChecks\":%"PRIu64",\"TimestampInvalidations\":%"PRIu64",\"DeliveredFrames\":%"PRIu64",\"ConsumerSpinUs\":%d,\"ConsumerSpinHits\":%"PRIu64",\"ConsumerEventWaits\":%"PRIu64",\"CleanShutdown\":%s}\n",
            stats.arrived, stats.copies, stats.pool_busy, stats.resized, stats.rate_limited, stats.idle_drops, stats.errors,
            stats.opened_textures, stats.opened_fence, stats.target_closed ? "true" : "false",
            s->source_checks, s->invalid+s->output_invalid, s->delivered, s->spin_us, s->spin_hits, s->event_waits, clean == 0 ? "true" : "false");
    av_log(avctx, AV_LOG_WARNING, "rewind_wgc_pacing={\"Requested\":%s,\"Supported\":%s,\"Applied\":%s,\"Status\":\"%s\",\"QueryHResult\":%d,\"SetHResult\":%d,\"ReadHResult\":%d,\"RequestedInterval100ns\":%"PRId64",\"EffectiveInterval100ns\":%"PRId64",\"ImmediateContext\":%s,\"FullCopies\":%"PRIu64",\"RegionCopies\":%"PRIu64",\"Clears\":%"PRIu64",\"Signals\":%"PRIu64",\"Flushes\":%"PRIu64",\"DescriptorQueries\":%"PRIu64"}\n",
        stats.pacing.requested?"true":"false",stats.pacing.supported?"true":"false",stats.pacing.applied?"true":"false",
        !stats.pacing.requested?"Disabled":!stats.pacing.supported?"Unavailable":stats.pacing.applied?"Applied":"RejectedOrUnreadable",
        stats.pacing.query_hr,stats.pacing.set_hr,stats.pacing.read_hr,stats.pacing.requested_interval,stats.pacing.effective_interval,
        stats.immediate_context?"true":"false",stats.full_copies,stats.region_copies,stats.clears,stats.signals,stats.flushes,stats.descriptor_queries);
    av_log(avctx, AV_LOG_WARNING, "rewind_queue_stats={\"AsyncProducer\":true,\"SyntheticSource\":false,\"PublishedFrames\":%"PRIu64",\"ReplacedUnreadFrames\":%"PRIu64",\"ConsumerStaleDrops\":%"PRIu64",\"PoolBusySkips\":%"PRIu64"}\n",
        s->published, s->replaced, s->stale, stats.pool_busy);
    }
    for (i = 0; i < RWQ_CAPACITY; ++i) {
        av_frame_free(&s->messages[i]);
        av_frame_free(&s->pool[i]);
    }
    if (s->trace_notification) CloseHandle(s->trace_notification);
    av_freep(&s->traces);
    if (s->notification) CloseHandle(s->notification);
    if (s->library) FreeLibrary(s->library);
    av_buffer_unref(&s->frames);
    av_buffer_unref(&s->device);
}
static const AVFilterPad wgc_outputs[] = {
    { .name="default", .type=AVMEDIA_TYPE_VIDEO, .config_props=wgc_config, .request_frame=wgc_request },
};
const FFFilter ff_vsrc_rewindwgc = {
    .p.name="rewindwgc",
    .p.description=NULL_IF_CONFIG_SMALL("Out-of-process WGC window into the dev.9 frame queue"),
    .p.priv_class=&rewindwgc_class,
    .p.flags=AVFILTER_FLAG_HWDEVICE,
    .priv_size=sizeof(RewindWgcContext),
    .init=wgc_init,
    .uninit=wgc_uninit,
    FILTER_OUTPUTS(wgc_outputs),
    FILTER_SINGLE_PIXFMT(AV_PIX_FMT_D3D11),
    .flags_internal=FF_FILTER_FLAG_HWFRAME_AWARE,
};
