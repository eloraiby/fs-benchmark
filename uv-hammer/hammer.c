// Copyright (C) 2019 Wael El Oraiby
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
#include <stdio.h>
#include "mimalloc/include/mimalloc.h"
#include "HdrHistogram_c/src/hdr_histogram.h"
#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#include <malloc.h>
#include <stdlib.h>
#include <unistd.h>
#include <stdatomic.h>
#include <time.h>

#include <uv.h>

#define MAX_THREADS 48
#ifndef MIN
#   define MIN(a, b) (a < b ? a : b)
#endif
#ifndef MAX
#   define MAX(a, b) (a > b ? a : b)
#endif

static  void onClientRead(uv_stream_t* stream, ssize_t nread, const uv_buf_t* buf);
static  void onClientWrite(uv_write_t* req, int status);

static
void
allocBuffer(uv_handle_t* handle, size_t suggested_size, uv_buf_t* buf) {
    (void)handle;
    buf->base = mi_calloc(1, suggested_size);
    buf->len = suggested_size;
}

static
char
askmRequest[16384 * 16] = {0};

static
void
onClientClose(uv_handle_t* h) {
    mi_free(((uv_stream_t*)h)->connect_req);
    mi_free(h);
}

typedef struct {
    size_t      clientCount;
    const char* ipaddr;
    const char* req;
    int         port;
    size_t      id;
    struct hdr_histogram*  hist;
} Thread;

typedef struct {
    Thread*     thread;
    struct timespec last;
} Client;

static size_t   threadCount;
static atomic_size_t asksPs[MAX_THREADS];
static atomic_size_t packetPs[MAX_THREADS];
static struct timespec oldTime;
static size_t   reqSize = 8;
static Thread   threads[MAX_THREADS];

static
void
onClientWrite(uv_write_t* req, int status) {
    if (status) {
        fprintf(stderr, "uv_write error: %s\n", uv_strerror(errno));
        uv_close((uv_handle_t*)req->handle, onClientClose);
    } else {
        // continue reading
        Client*     client  = (Client*)(req->handle->data);
        Thread*     thread  = client->thread;
        clock_gettime(CLOCK_MONOTONIC, &client->last);
        uv_read_start(req->handle, allocBuffer, onClientRead);
    }
    mi_free(req);
}

static
void
onClientRead(uv_stream_t* stream, ssize_t nread, const uv_buf_t* buf) {
    if(nread >= 0) {
        Client*     client  = (Client*)(stream->data);
        Thread*     thread  = client->thread;
        size_t id   = thread->id;

        //fwrite(buf->base, nread, 1, stream->data);
        atomic_fetch_add(&packetPs[id], 1);
        atomic_fetch_add(&asksPs[id], reqSize);
        uv_write_t*     write_req   = mi_calloc(1, sizeof(uv_write_t));
        uv_buf_t        buff    = { .base = askmRequest, .len = strlen(askmRequest) };

        struct timespec stop;
        struct timespec start    = ((Client*)stream->data)->last;

        clock_gettime(CLOCK_MONOTONIC, &stop);

        int64_t result = (stop.tv_sec - start.tv_sec) * 1e6 + (stop.tv_nsec - start.tv_nsec) / 1e3;    // in microseconds
        if( !hdr_record_value(thread->hist, result) ) {
            //fprintf(stderr, "unable to record value %ld - counts len: %d\n", result, thread->hist->counts_len);
        }
        uv_write(write_req, stream, &buff, 1, onClientWrite);
    } else {
        uv_close((uv_handle_t*)stream, onClientClose);
    }

    //cargo-culted
    mi_free(buf->base);
}

static
void
onClientConnect(uv_connect_t* connection, int status) {
    if( status ) {
        fprintf(stderr, "error on connection: %s\n", uv_strerror(status));
        mi_free(connection->handle);
        mi_free(connection);
    } else {
        fprintf(stderr, "Connected...\n");
        uv_stream_t*    stream  = connection->handle;
        uv_write_t*     write_req   = mi_calloc(1, sizeof(uv_write_t));
        uv_buf_t        buff    = { .base = askmRequest, .len = strlen(askmRequest) };
        clock_gettime(CLOCK_MONOTONIC, &((Client*)(stream->data))->last);
        uv_write(write_req, stream, &buff, 1, onClientWrite);
    }
}

void
startClient(uv_loop_t* loop, const char* ipaddr, int port, int id) {
    uv_tcp_t*       socket      = mi_calloc(1, sizeof(uv_tcp_t));
    uv_connect_t*   connect     = mi_calloc(1, sizeof(uv_connect_t));

    uv_tcp_init(loop, socket);
    socket->data    = mi_calloc(1, sizeof(Client));
    ((Client*)socket->data)->thread = &threads[id];
    uv_tcp_keepalive(socket, 1, 50);

    struct sockaddr_in addr;
    uv_ip4_addr(ipaddr, port, (struct sockaddr_in*)&addr);


    uv_tcp_connect(connect, socket, (struct sockaddr*)&addr, onClientConnect);
}



void
startThread(void* thread_) {
    Thread* thread  = (Thread*)thread_;

    uv_loop_t*   loop   = (uv_loop_t*)mi_calloc(1, sizeof(uv_loop_t));
    if( uv_loop_init(loop) ) {
        fprintf(stderr, "unable to initialize loop\n");
        exit(1);
    }

    for( size_t c = 0; c < thread->clientCount; ++c ) {
        startClient(loop, thread->ipaddr, thread->port, thread->id);
    }

    //mi_free(thread);
    uv_run(loop, UV_RUN_DEFAULT);
}


static
double
timeSpecToSeconds(struct timespec* ts) {
    return (double)ts->tv_sec + (double)ts->tv_nsec / 1000000000.0;
}

void
countersCb(uv_timer_t* handle) {
    uv_async_send(handle->data);
}

void
asyncCb(uv_async_t* handle) {
    size_t  totalAsks       = 0;
    size_t  totalPackets    = 0;

    int64_t p50     = 0;
    int64_t p95     = 0;
    int64_t p99     = 0;

    for( size_t t = 0; t < threadCount; ++t ) {
        totalAsks += atomic_load(&asksPs[t]);
        atomic_store(&asksPs[t], 0);
        totalPackets    += atomic_load(&packetPs[t]);
        atomic_store(&packetPs[t], 0);
        p50 = hdr_value_at_percentile(threads[t].hist, 0.5);
        p95 = hdr_value_at_percentile(threads[t].hist, 95);
        p99 = hdr_value_at_percentile(threads[t].hist, 99);
        hdr_reset(threads[t].hist);
    }

    struct timespec currentTime;
    clock_gettime(CLOCK_MONOTONIC, &currentTime);

    double last     = timeSpecToSeconds(&oldTime);
    double current  = timeSpecToSeconds(&currentTime);
    double diff     = current - last;
    double pCount   = ((double)totalPackets) / diff;
    double aCount   = ((double)totalAsks)    / diff;
    fprintf(stderr, "packets count: %lu - ask count: %lu - median: %luus p95: %luus - p99: %luus\n", (ssize_t)pCount, (ssize_t)aCount, p50, p95, p99);
    oldTime = currentTime;
}

void
startMonitoringThread(void* args_) {
    uv_loop_t   loop;
    uv_timer_t  timer;
    uv_async_t  async;

    uv_loop_init(&loop);
    uv_timer_init(&loop, &timer);
    uv_async_init(&loop, &async, asyncCb);
    timer.data  = &async;

    clock_gettime(CLOCK_MONOTONIC, &oldTime);

    uv_timer_start(&timer, countersCb, 1000, 1000);

    (void)args_;
    uv_run(&loop, UV_RUN_DEFAULT);
    uv_loop_close(&loop);
}

int
main(int argc, char* argv[]) {
    uv_thread_t uvThreads[MAX_THREADS];

    int     opt     = 0;
    int     port    = 0;
    char*   ip      = NULL;
    int     clientsPerThread    = 1;

    while( (opt = getopt(argc, argv, "i:p:t:c:n:")) != -1 ) {
        switch(opt) {
        case 'i':
            ip  = optarg;
            break;
        case 'p':
            port = atoi(optarg);
            break;
        case 't':
            threadCount = (size_t)MAX(MIN(MAX_THREADS, atoi(optarg)), 1);
            break;
        case 'c':
            clientsPerThread = atoi(optarg);
            break;
        case 'n':
            reqSize     = MAX(1, MIN((size_t)atoi(optarg), 1024));
            break;

        default:
            fprintf(stderr, "invalid option: %c\nUsage: %s <-i 123.123.123.123> <-p port> [-t 16] [-c 16] [-n 8]\n", (char)opt, argv[0]);
            exit(1);
        }
    }

    if( !ip ) {
        fprintf(stderr, "no ip is given\nUsage: %s <-i 123.123.123.123> <-p port> [-t 16] [-c 16] [-n 8]\n", argv[0]);
        exit(1);
    }

    if( !port ) {
        fprintf(stderr, "no port is given\nUsage: %s <-i 123.123.123.123> <-p port> [-t 16] [-c 16] [-n 8]\n", argv[0]);
        exit(1);
    }

    sprintf(askmRequest, "CB");
    for( size_t n = 0; n < reqSize; ++n ) {
        sprintf(&askmRequest[strlen(askmRequest)], " 1234:4567");
    }
    sprintf(&askmRequest[strlen(askmRequest)], "\n");

    fprintf(stderr, "sending: %s", askmRequest);

    for( size_t t = 0; t < (size_t)threadCount; ++t ) {
        Thread* thread = &threads[t];
        thread->port = port;
        thread->ipaddr   = ip;
        thread->clientCount  = (size_t)clientsPerThread;
        thread->id      = t;
        thread->hist    = NULL;//calloc(1, sizeof(struct hdr_histogram));
        hdr_init(10, 1000000, 3, &thread->hist);
        uv_thread_create(&uvThreads[t], startThread, thread);
    }

    fprintf(stderr, "request per packet: %lu\n", reqSize);
    uv_thread_t monitor;
    uv_thread_create(&monitor, startMonitoringThread, NULL);

    for( size_t t = 0; t < (size_t)threadCount; ++t ) {
        uv_thread_join(&uvThreads[t]);
    }


    uv_thread_join(&monitor);

    return 0;
}
