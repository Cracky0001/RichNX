#include "logger.h"

#include <stdio.h>
#include <switch.h>

#define LOG_PATH "sdmc:/switch/switch-dcrpc/log.log"

static bool g_logger_enabled = false;
static u64 g_log_line = 0;

void logger_set_enabled(bool enabled) {
    g_logger_enabled = enabled;
}

void logger_vwrite(const char* fmt, va_list args) {
    if (!g_logger_enabled) {
        return;
    }

    FILE* f = fopen(LOG_PATH, "a");
    if (!f) {
        return;
    }

    const u64 sec_since_boot = armTicksToNs(armGetSystemTick()) / 1000000000ULL;
    fprintf(f, "[%llu s] [line=%llu] ",
            (unsigned long long)sec_since_boot,
            (unsigned long long)g_log_line++);
    vfprintf(f, fmt, args);
    fputc('\n', f);
    fclose(f);
}

void logger_write(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    logger_vwrite(fmt, args);
    va_end(args);
}
