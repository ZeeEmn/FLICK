/* SPDX-License-Identifier: LGPL-2.1-or-later
 * Bounded single-producer/single-consumer latest-frame handoff.
 * Generation and state share one atomic word to avoid ABA on recycled slots.
 */
#ifndef REWIND_QUEUE_H
#define REWIND_QUEUE_H
#include <stdatomic.h>
#include <stdint.h>
#include <limits.h>
#define RWQ_CAPACITY 8
enum { RWQ_FREE, RWQ_WRITING, RWQ_READY, RWQ_READING };
typedef struct RewindQueue {
    atomic_uint_least64_t tag[RWQ_CAPACITY];
    uint64_t next_sequence; /* producer only */
    uint64_t last_sequence; /* consumer only */
} RewindQueue;
static inline void rwq_init(RewindQueue *q)
{
    int i;
    for (i = 0; i < RWQ_CAPACITY; i++) atomic_init(&q->tag[i], RWQ_FREE);
    q->next_sequence = q->last_sequence = 0;
}
static inline int rwq_lock_free(RewindQueue *q) { return atomic_is_lock_free(&q->tag[0]); }
static inline int rwq_write_claim(RewindQueue *q, unsigned excluded, uint64_t *previous)
{
    int attempt, i, chosen;
    uint64_t best, tag, expected;
    for (attempt = 0; attempt < RWQ_CAPACITY; attempt++) {
        best = UINT64_MAX; chosen = -1;
        for (i = 0; i < RWQ_CAPACITY; i++) {
            if (excluded & (1u << i)) continue;
            tag = atomic_load_explicit(&q->tag[i], memory_order_acquire);
            if ((tag & 3) == RWQ_FREE) { chosen = i; best = tag; break; }
            if ((tag & 3) == RWQ_READY && tag < best) { chosen = i; best = tag; }
        }
        if (chosen < 0) return -1;
        expected = best;
        if (atomic_compare_exchange_strong_explicit(&q->tag[chosen], &expected,
                (best & ~UINT64_C(3)) | RWQ_WRITING, memory_order_acq_rel, memory_order_acquire)) {
            *previous = best; return chosen;
        }
    }
    return -1;
}
static inline void rwq_write_cancel(RewindQueue *q, int i, uint64_t previous)
{ atomic_store_explicit(&q->tag[i], previous, memory_order_release); }
static inline void rwq_publish(RewindQueue *q, int i)
{ atomic_store_explicit(&q->tag[i], (++q->next_sequence << 2) | RWQ_READY, memory_order_release); }
static inline int rwq_read_claim(RewindQueue *q, uint64_t *claimed, uint64_t *discarded)
{
    int attempt, i, chosen;
    uint64_t newest, tag, expected;
    for (attempt = 0; attempt < RWQ_CAPACITY; attempt++) {
        newest = 0; chosen = -1;
        for (i = 0; i < RWQ_CAPACITY; i++) {
            tag = atomic_load_explicit(&q->tag[i], memory_order_acquire);
            if ((tag & 3) != RWQ_READY) continue;
            if ((tag >> 2) <= q->last_sequence) {
                expected = tag;
                if (atomic_compare_exchange_strong_explicit(&q->tag[i], &expected,
                        (tag & ~UINT64_C(3)) | RWQ_FREE, memory_order_acq_rel, memory_order_acquire))
                    (*discarded)++;
            } else if (tag > newest) { newest = tag; chosen = i; }
        }
        if (chosen < 0) return -1;
        expected = newest;
        if (atomic_compare_exchange_strong_explicit(&q->tag[chosen], &expected,
                (newest & ~UINT64_C(3)) | RWQ_READING, memory_order_acq_rel, memory_order_acquire)) {
            q->last_sequence = newest >> 2; *claimed = newest; return chosen;
        }
    }
    return -1;
}
static inline void rwq_read_release(RewindQueue *q, int i, uint64_t claimed)
{ atomic_store_explicit(&q->tag[i], (claimed & ~UINT64_C(3)) | RWQ_FREE, memory_order_release); }
#endif
