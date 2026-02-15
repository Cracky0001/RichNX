#pragma once

#include <stddef.h>
#include <stdbool.h>
#include <switch.h>
#include "telemetry.h"

typedef struct {
    TelemetryState* telemetry;
    volatile bool running;
    Thread thread;
    int listen_fd;
    unsigned short port;
    volatile u64 accepted_count;
    volatile u64 request_count;
    volatile int last_errno;
    volatile int stage;
    volatile bool listening;
} HttpServer;

bool http_server_start(HttpServer* server, TelemetryState* telemetry, unsigned short port);
void http_server_stop(HttpServer* server);
void http_server_build_debug_json(const HttpServer* server, char* out, size_t out_size);
