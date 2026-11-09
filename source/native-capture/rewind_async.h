/* SPDX-License-Identifier: LGPL-2.1-or-later
 * Included by vsrc_ddagrab.c after its device/probe initialization helpers.
 */
static void rewind_async_stop(DdagrabContext *dda)
{
    int i;
    if (dda->producer) {
        atomic_store_explicit(&dda->producer_stopping, 1, memory_order_release);
        SetEvent(dda->producer_stop);
        SetEvent(dda->producer_run);
        /* Joining before freeing frames/device state prevents use-after-free.
         * A driver call cannot safely be cancelled by terminating its thread. */
        WaitForSingleObject(dda->producer, INFINITE);
        CloseHandle(dda->producer); dda->producer = NULL;
    }
    for (i = 0; i < RWQ_CAPACITY; i++) {
        av_frame_free(&dda->queue_messages[i]);
        av_frame_free(&dda->queue_frames[i]);
    }
    av_frame_free(&dda->producer_last);
#define RW_CLOSE_HANDLE(name) do { if (dda->name) CloseHandle(dda->name); dda->name = NULL; } while (0)
    RW_CLOSE_HANDLE(producer_stop);
    RW_CLOSE_HANDLE(producer_initialized);
    RW_CLOSE_HANDLE(producer_run);
    RW_CLOSE_HANDLE(frame_available);
    RW_CLOSE_HANDLE(producer_timer);
#undef RW_CLOSE_HANDLE
}

static void rewind_frame_metadata(AVFrame *frame, DXGI_FORMAT format)
{
    frame->sample_aspect_ratio = (AVRational){1, 1};
    frame->color_range = AVCOL_RANGE_JPEG;
    frame->color_primaries = AVCOL_PRI_BT709;
    frame->colorspace = AVCOL_SPC_RGB;
    frame->color_trc = format == DXGI_FORMAT_R16G16B16A16_FLOAT ? AVCOL_TRC_LINEAR : AVCOL_TRC_IEC61966_2_1;
}

/* Only the producer accesses its own cache, queue message writes and immediate
 * context. Buffer reference counts protect textures leased downstream. */
static int rewind_retry_pause(DdagrabContext *dda)
{
    HANDLE wait[] = { dda->producer_stop, dda->producer_timer };
    LARGE_INTEGER due;
    DWORD wake;
    due.QuadPart = -10000; /* 1 ms, only on the independent producer */
    if (!SetWaitableTimer(dda->producer_timer, &due, 0, NULL, NULL, FALSE)) return AVERROR_EXTERNAL;
    wake = WaitForMultipleObjects(2, wait, FALSE, INFINITE);
    return wake == WAIT_OBJECT_0+1 ? 0 : wake == WAIT_OBJECT_0 ? AVERROR_EXIT : AVERROR_EXTERNAL;
}

static int rewind_produce(AVFilterContext *avctx, int64_t epoch, uint8_t *test_pixels)
{
    DdagrabContext *dda = avctx->priv;
    ID3D11Texture2D *texture = NULL;
    D3D11_TEXTURE2D_DESC desc;
    D3D11_BOX box = { dda->offset_x, dda->offset_y, 0,
                     dda->offset_x + dda->width, dda->offset_y + dda->height, 1 };
    unsigned excluded = 0;
    uint64_t previous;
    int slot = -1, owner = -1, attempt, i, ret;
    int64_t retry_until;
    AVFrame *frame;
    for (attempt = 0; attempt < RWQ_CAPACITY; attempt++) {
        slot = rwq_write_claim(&dda->queue, excluded, &previous);
        if (slot < 0) break;
        if ((previous & 3) == RWQ_READY) dda->queue_replaced++;
        av_frame_unref(dda->queue_messages[slot]);
        /* No consumer can acquire a new reference when the total is one:
         * no queued message, encoder or producer cache then references it. */
        for (i = 0; i < RWQ_CAPACITY; i++) {
            if (av_buffer_get_ref_count(dda->queue_frames[i]->buf[0]) == 1) { owner = i; break; }
        }
        if (owner >= 0) break;
        rwq_write_cancel(&dda->queue, slot, previous & ~UINT64_C(3));
        excluded |= 1u << slot; slot = -1;
    }
    if (slot < 0 || owner < 0) { dda->pool_busy_skips++; return 0; }

    frame = dda->queue_frames[owner];
    if (dda->rewind_synthetic) {
        /* Generated test input ONLY. The live path never uploads CPU pixels. */
        uint8_t shade = (uint8_t)(dda->queue_published % 240);
        for (i = 0; i < dda->width * dda->height; i++) {
            test_pixels[i*4] = shade; test_pixels[i*4+1] = 255-shade;
            test_pixels[i*4+2] = shade; test_pixels[i*4+3] = 255;
        }
        ID3D11DeviceContext_UpdateSubresource(dda->device_hwctx->device_context,
                (ID3D11Resource *)frame->data[0], (UINT)(intptr_t)frame->data[1],
                NULL, test_pixels, dda->width * 4, 0);
    } else {
        if (dda->probed_texture) {
            texture = dda->probed_texture; dda->probed_texture = NULL; ret = 0;
        } else {
            /* Immediately reacquiring after release can precede DXGI's surface
             * update. Retry with a short sleep, not a hot loop or a positive
             * AcquireNextFrame timeout. Bound retries to half a frame period. */
            retry_until = av_gettime_relative() + FFMIN(8000, av_rescale_q(1, dda->time_base, AV_TIME_BASE_Q)/2);
            for (;;) {
                ret = next_frame_internal(avctx, &texture, 0);
                if (ret != AVERROR(EAGAIN) || av_gettime_relative() >= retry_until) break;
                ret = rewind_retry_pause(dda);
                if (ret < 0) break;
                dda->acquire_retry_polls++;
            }
        }
        if (ret == AVERROR(EAGAIN) && dda->producer_last->buf[0]) {
            ret = av_frame_ref(dda->queue_messages[slot], dda->producer_last);
            if (ret < 0) goto fail;
            dda->duplicate_frames++;
            goto publish;
        }
        if (ret < 0) goto fail;
        ID3D11Texture2D_GetDesc(texture, &desc);
        if (desc.Format != dda->raw_format || desc.Width != dda->raw_width || desc.Height != dda->raw_height) {
            ret = AVERROR_OUTPUT_CHANGED; goto fail;
        }
        ID3D11DeviceContext_CopySubresourceRegion(dda->device_hwctx->device_context,
                (ID3D11Resource *)frame->data[0], (UINT)(intptr_t)frame->data[1],
                0, 0, 0, (ID3D11Resource *)texture, 0, &box);
        dda->gpu_copies++;
        release_resource(&texture);
    }
    ret = av_frame_replace(dda->producer_last, frame);
    if (ret < 0) goto fail;
    ret = av_frame_ref(dda->queue_messages[slot], frame);
    if (ret < 0) goto fail;
publish:
    /* Generated input encodes its color index in PTS for post-decode lease
     * validation. Live capture always uses the actual publication timestamp. */
    dda->queue_messages[slot]->pts = dda->rewind_synthetic ? dda->queue_published * INT64_C(16666) : av_gettime_relative() - epoch;
    rwq_publish(&dda->queue, slot);
    dda->queue_published++;
    SetEvent(dda->frame_available);
    return 0;
fail:
    release_resource(&texture);
    rwq_write_cancel(&dda->queue, slot, previous & ~UINT64_C(3));
    return ret == AVERROR(EAGAIN) ? 0 : ret;
}

static DWORD WINAPI rewind_producer_thread(void *opaque)
{
    AVFilterContext *avctx = opaque;
    DdagrabContext *dda = avctx->priv;
    HRESULT com = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    HANDLE startup[] = { dda->producer_stop, dda->producer_run };
    HANDLE pacing[] = { dda->producer_stop, dda->producer_timer };
    uint8_t *test_pixels = NULL;
    int ret = 0;
    int64_t epoch, tick = 0, now, deadline, delay, next_tick;
    LARGE_INTEGER due;
    if (FAILED(com)) { ret = AVERROR_EXTERNAL; goto initialized; }
    if (dda->rewind_synthetic) {
        dda->raw_format = DXGI_FORMAT_B8G8R8A8_UNORM;
        dda->raw_width = 320; dda->raw_height = 180;
        if (dda->width <= 0) dda->width = 320;
        if (dda->height <= 0) dda->height = 180;
    } else {
        ret = init_dxgi_dda(avctx);
        if (ret >= 0) ret = probe_output_format(avctx);
    }
initialized:
    dda->producer_init_result = ret;
    SetEvent(dda->producer_initialized);
    if (ret < 0 || WaitForMultipleObjects(2, startup, FALSE, INFINITE) != WAIT_OBJECT_0+1) goto end;
    if (dda->rewind_synthetic) {
        test_pixels = av_malloc((size_t)dda->width * dda->height * 4);
        if (!test_pixels) { ret = AVERROR(ENOMEM); goto end; }
    }
    epoch = av_gettime_relative();
    while (!atomic_load_explicit(&dda->producer_stopping, memory_order_acquire)) {
        now = av_gettime_relative();
        ret = rewind_produce(avctx, epoch, test_pixels);
        if (ret < 0) break;
        tick++;
        now = av_gettime_relative();
        deadline = epoch + av_rescale_q(tick, dda->time_base, AV_TIME_BASE_Q);
        if (deadline <= now) {
            next_tick = av_rescale_q_rnd(now-epoch, AV_TIME_BASE_Q, dda->time_base, AV_ROUND_UP) + 1;
            dda->deadline_skips += FFMAX(next_tick - tick, 0);
            tick = next_tick;
            deadline = epoch + av_rescale_q(tick, dda->time_base, AV_TIME_BASE_Q);
        }
        delay = FFMAX(deadline - av_gettime_relative(), 1);
        due.QuadPart = -delay * 10;
        if (!SetWaitableTimer(dda->producer_timer, &due, 0, NULL, NULL, FALSE)) { ret = AVERROR_EXTERNAL; break; }
        {
            DWORD wake = WaitForMultipleObjects(2, pacing, FALSE, INFINITE);
            if (wake == WAIT_FAILED) ret = AVERROR_EXTERNAL;
            if (wake != WAIT_OBJECT_0+1) break;
        }
    }
end:
    release_resource(&dda->probed_texture);
    if (FAILED(rewind_release_frame(dda)) && ret >= 0) ret = AVERROR_EXTERNAL;
    av_free(test_pixels);
    atomic_store_explicit(&dda->producer_error, ret, memory_order_release);
    SetEvent(dda->frame_available);
    if (SUCCEEDED(com)) CoUninitialize();
    return 0;
}

static int rewind_async_start_probe(AVFilterContext *avctx)
{
    DdagrabContext *dda = avctx->priv;
    ID3D10Multithread *protection = NULL;
    HRESULT hr;
    rwq_init(&dda->queue);
    atomic_init(&dda->producer_error, 0);
    atomic_init(&dda->producer_stopping, 0);
    if (!rwq_lock_free(&dda->queue)) return AVERROR(ENOSYS);
    /* FFmpeg normally enables this at device creation. Require it explicitly
     * for asynchronous use: a lock-free handoff does not make D3D11 lock-free. */
    hr = ID3D11Device_QueryInterface(dda->device_hwctx->device, &IID_ID3D10Multithread, (void **)&protection);
    if (FAILED(hr)) return AVERROR_EXTERNAL;
    ID3D10Multithread_SetMultithreadProtected(protection, TRUE);
    ID3D10Multithread_Release(protection);
    dda->producer_stop = CreateEventW(NULL, TRUE, FALSE, NULL);
    dda->producer_initialized = CreateEventW(NULL, TRUE, FALSE, NULL);
    dda->producer_run = CreateEventW(NULL, TRUE, FALSE, NULL);
    dda->frame_available = CreateEventW(NULL, FALSE, FALSE, NULL);
    dda->producer_timer = CreateWaitableTimerExW(NULL, NULL, 2 /* HIGH_RESOLUTION */, TIMER_ALL_ACCESS);
    if (!dda->producer_timer) dda->producer_timer = CreateWaitableTimerW(NULL, FALSE, NULL);
    if (!dda->producer_stop || !dda->producer_initialized || !dda->producer_run || !dda->frame_available || !dda->producer_timer) return AVERROR_EXTERNAL;
    dda->producer = CreateThread(NULL, 0, rewind_producer_thread, avctx, 0, NULL);
    if (!dda->producer) return AVERROR_EXTERNAL;
    if (WaitForSingleObject(dda->producer_initialized, 10000) != WAIT_OBJECT_0) return AVERROR(ETIMEDOUT);
    return dda->producer_init_result;
}

static int rewind_async_start_frames(AVFilterContext *avctx)
{
    DdagrabContext *dda = avctx->priv;
    int i, ret;
    for (i = 0; i < RWQ_CAPACITY; i++) {
        dda->queue_frames[i] = av_frame_alloc();
        dda->queue_messages[i] = av_frame_alloc();
        if (!dda->queue_frames[i] || !dda->queue_messages[i]) return AVERROR(ENOMEM);
        ret = av_hwframe_get_buffer(dda->frames_ref, dda->queue_frames[i], 0);
        if (ret < 0) return ret;
        rewind_frame_metadata(dda->queue_frames[i], dda->raw_format);
    }
    dda->producer_last = av_frame_alloc();
    if (!dda->producer_last) return AVERROR(ENOMEM);
    SetEvent(dda->producer_run);
    return 0;
}

/* This try-pop contains no OS wait, DXGI call, allocation of GPU memory or
 * device-context operation. Only the filter thread calls FFmpeg graph APIs. */
static int rewind_async_try_pop(DdagrabContext *dda, AVFrame **frame)
{
    uint64_t claimed;
    int slot = rwq_read_claim(&dda->queue, &claimed, &dda->consumer_stale_drops);
    if (slot < 0) { dda->consumer_empty_polls++; return AVERROR(EAGAIN); }
    *frame = av_frame_clone(dda->queue_messages[slot]);
    av_frame_unref(dda->queue_messages[slot]);
    rwq_read_release(&dda->queue, slot, claimed);
    return *frame ? 0 : AVERROR(ENOMEM);
}

static int rewind_async_request_frame(AVFilterLink *outlink)
{
    DdagrabContext *dda = outlink->src->priv;
    AVFrame *frame = NULL;
    int ret, error;
    int64_t started = av_gettime_relative();
    for (;;) {
        error = atomic_load_explicit(&dda->producer_error, memory_order_acquire);
        if (error < 0) return error;
        ret = rewind_async_try_pop(dda, &frame);
        if (ret != AVERROR(EAGAIN)) break;
        /* FFmpeg's pull scheduler retries empty source filters. A notification
         * wait here avoids busy-spinning; it never holds up the producer. */
        if (av_gettime_relative() - started > 1000000) return AVERROR(ETIMEDOUT);
        WaitForSingleObject(dda->frame_available, 50);
    }
    if (ret < 0) return ret;
    /* Deliberately hold a GPU buffer lease while the synthetic producer keeps
     * publishing. This path is rejected unless generated test input is active. */
    if (dda->rewind_test_delay_ms) Sleep(dda->rewind_test_delay_ms);
    return ff_filter_frame(outlink, frame);
}
