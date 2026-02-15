#include <stdbool.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <switch.h>
#include "http_server.h"
#include "logger.h"
#include "telemetry.h"

#define INNER_HEAP_SIZE            0x400000
#define LOOP_SLEEP_NS              (2ULL * 1000000000ULL)
#define APPINIT_DELAY_NS           (20ULL * 1000000000ULL)
#define INIT_RETRY_TICKS           3
#define HEARTBEAT_TICKS            15
#define HTTP_PORT                  6000
#define STATUS_PATH                "sdmc:/switch/switch-dcrpc/status.txt"
#define DETECTION_DISABLE_FLAG_PATH "sdmc:/switch/switch-dcrpc/detection.off"
#define ENABLE_PM_SERVICES         1
#define ENABLE_DETECTION_WORKER    0
#define ENABLE_RISKY_MAINLOOP_DETECTION 1
#define DETECTION_START_DELAY_SEC  45
#define DETECTION_SLEEP_NS         (3ULL * 1000000000ULL)
#define DETECTION_STACK_SIZE       (64 * 1024)
#define DETECTION_THREAD_PRIO      0x2D
#define DETECTION_THREAD_CPUID     3
#define DETECTION_FAIL_STREAK_MAX  8
#define DETECTION_COOLDOWN_SEC     120
#define DETECTION_HEARTBEAT_TIMEOUT_SEC 20
#define DETECTION_STALE_DISABLE_SEC 3600

#ifdef __cplusplus
extern "C" {
#endif

u32 __nx_applet_type = AppletType_None;
u32 __nx_fs_num_sessions = 1;
bool __nx_fsdev_support_cwd = false;

static bool g_sm_ready = false;
static bool g_fs_ready = false;
static bool g_setsys_ready = false;
static bool g_pmshell_ready = false;
static bool g_pminfo_ready = false;
static bool g_nifm_ready = false;
static bool g_socket_ready = false;
static bool g_fw_valid = false;
static char g_fw_str[32];
static char g_stage[64] = "boot";
static Result g_last_rc = 0;
static u64 g_session_id = 0;
static u64 g_heartbeat_count = 0;
static bool g_unclean_prev = false;
static bool g_ns_ready = false;
static bool g_detection_thread_started = false;
static volatile bool g_detection_thread_running = false;
static volatile bool g_detection_thread_alive = false;
static u64 g_detection_thread_last_heartbeat_sec = 0;
static bool g_detection_services_ready = false;
static bool g_detection_services_ready_logged = false;
static u64 g_last_logged_active_program_id = 0;
static bool g_detection_wait_logged = false;
static bool g_detection_kill_switch = false;
static u64 g_detection_attempt_count = 0;
static u64 g_detection_success_count = 0;
static u64 g_detection_fail_count = 0;
static u32 g_detection_fail_streak = 0;
static u64 g_detection_disabled_until_sec = 0;
static Result g_detection_last_rc = 0;
static u64 g_detection_last_logged_program_id = 0;
static Thread g_detection_thread;
static u8 g_detection_thread_stack[DETECTION_STACK_SIZE] __attribute__((aligned(0x1000)));

static TelemetryState g_telemetry;
static HttpServer g_server;

static u64 sec_since_boot_now(void) {
    return armTicksToNs(armGetSystemTick()) / 1000000000ULL;
}

static void set_stage(const char* stage) {
    snprintf(g_stage, sizeof(g_stage), "%s", stage ? stage : "unknown");
    logger_write("stage: %s", g_stage);
}

static bool file_exists(const char* path) {
    FILE* f = fopen(path, "r");
    if (!f) return false;
    fclose(f);
    return true;
}

static void refresh_detection_kill_switch(void) {
    bool enabled_now;

    if (!g_fs_ready) return;

    enabled_now = file_exists(DETECTION_DISABLE_FLAG_PATH);
    if (enabled_now != g_detection_kill_switch) {
        g_detection_kill_switch = enabled_now;
        logger_write(
            "detector: kill-switch %s (%s)",
            g_detection_kill_switch ? "enabled" : "disabled",
            DETECTION_DISABLE_FLAG_PATH
        );
    }
}

static void log_active_title_if_changed(void) {
    u64 active_program_id = 0;

    rmutexLock(&g_telemetry.lock);
    active_program_id = g_telemetry.active_program_id;
    rmutexUnlock(&g_telemetry.lock);

    if (active_program_id == 0 || active_program_id == g_last_logged_active_program_id) {
        return;
    }

    g_last_logged_active_program_id = active_program_id;
    logger_write("title: active_program_id=0x%016llX", (unsigned long long)active_program_id);
}

static void detection_worker_thread(void* arg) {
    bool ns_ready_local = false;
    (void)arg;

    g_detection_thread_alive = true;
    g_detection_thread_last_heartbeat_sec = sec_since_boot_now();
    logger_write(
        "detector: thread started prio=%d cpuid=%d",
        DETECTION_THREAD_PRIO,
        DETECTION_THREAD_CPUID
    );

    while (g_detection_thread_running) {
        const u64 now = sec_since_boot_now();
        Result ns_rc;
        u64 active_program_id;
        char active_game[256];

        g_detection_thread_last_heartbeat_sec = now;

        if (g_detection_kill_switch) {
            if (ns_ready_local) {
                nsExit();
                ns_ready_local = false;
                g_ns_ready = false;
                logger_write("detector: ns shutdown because kill-switch is active");
            }
            svcSleepThread(DETECTION_SLEEP_NS);
            continue;
        }

        if (g_detection_disabled_until_sec > now) {
            svcSleepThread(DETECTION_SLEEP_NS);
            continue;
        }
        if (g_detection_disabled_until_sec != 0) {
            g_detection_disabled_until_sec = 0;
            g_detection_fail_streak = 0;
            logger_write("detector: cooldown elapsed, resuming");
        }

        if (!ns_ready_local) {
            g_detection_last_rc = nsInitialize();
            if (R_FAILED(g_detection_last_rc)) {
                g_detection_fail_count++;
                if (g_detection_fail_streak < 0xFFFFFFFFU) g_detection_fail_streak++;
                logger_write(
                    "detector: nsInitialize failed rc=0x%08lX streak=%u",
                    (unsigned long)g_detection_last_rc,
                    (unsigned int)g_detection_fail_streak
                );
                if (g_detection_fail_streak >= DETECTION_FAIL_STREAK_MAX) {
                    g_detection_disabled_until_sec = now + DETECTION_COOLDOWN_SEC;
                    logger_write(
                        "detector: auto-cooldown for %us after init failures",
                        (unsigned int)DETECTION_COOLDOWN_SEC
                    );
                }
                svcSleepThread(DETECTION_SLEEP_NS);
                continue;
            }

            ns_ready_local = true;
            g_ns_ready = true;
            g_detection_fail_streak = 0;
            logger_write("detector: ns ready");
        }

        telemetry_update(&g_telemetry, true);

        rmutexLock(&g_telemetry.lock);
        ns_rc = g_telemetry.last_ns_result;
        active_program_id = g_telemetry.active_program_id;
        snprintf(active_game, sizeof(active_game), "%s", g_telemetry.active_game);
        rmutexUnlock(&g_telemetry.lock);

        g_detection_attempt_count++;
        g_detection_last_rc = ns_rc;

        if (R_SUCCEEDED(ns_rc)) {
            if (g_detection_fail_streak > 0) {
                logger_write("detector: recovered after fail_streak=%u", (unsigned int)g_detection_fail_streak);
            }
            g_detection_fail_streak = 0;
            g_detection_success_count++;
            if (active_program_id != g_detection_last_logged_program_id) {
                g_detection_last_logged_program_id = active_program_id;
                logger_write(
                    "detector: active changed program=0x%016llX game=%s",
                    (unsigned long long)active_program_id,
                    active_game
                );
            }
        } else {
            g_detection_fail_count++;
            if (g_detection_fail_streak < 0xFFFFFFFFU) g_detection_fail_streak++;
            if (g_detection_fail_streak == 1 || (g_detection_fail_streak % 3) == 0) {
                logger_write(
                    "detector: query failed ns_rc=0x%08lX streak=%u",
                    (unsigned long)ns_rc,
                    (unsigned int)g_detection_fail_streak
                );
            }
            if (g_detection_fail_streak >= DETECTION_FAIL_STREAK_MAX) {
                g_detection_disabled_until_sec = now + DETECTION_COOLDOWN_SEC;
                logger_write(
                    "detector: auto-cooldown for %us after query failures",
                    (unsigned int)DETECTION_COOLDOWN_SEC
                );
                if (ns_ready_local) {
                    nsExit();
                    ns_ready_local = false;
                    g_ns_ready = false;
                    logger_write("detector: ns shutdown for cooldown");
                }
            }
        }

        svcSleepThread(DETECTION_SLEEP_NS);
    }

    if (ns_ready_local) {
        nsExit();
        g_ns_ready = false;
    }
    g_detection_thread_alive = false;
    logger_write("detector: thread stopped");
}

static void stop_detection_worker(void) {
    if (!g_detection_thread_started) return;
    g_detection_thread_running = false;
    threadClose(&g_detection_thread);
    g_detection_thread_alive = false;
    g_detection_thread_started = false;
    logger_write("detector: worker stop requested (non-blocking)");
}

static bool start_detection_worker(void) {
    Result rc;

    if (g_detection_thread_started || g_detection_kill_switch) {
        return false;
    }

    g_detection_thread_running = true;
    g_detection_thread_alive = false;
    g_detection_thread_last_heartbeat_sec = sec_since_boot_now();
    rc = threadCreate(
        &g_detection_thread,
        detection_worker_thread,
        NULL,
        g_detection_thread_stack,
        DETECTION_STACK_SIZE,
        DETECTION_THREAD_PRIO,
        DETECTION_THREAD_CPUID
    );
    if (R_FAILED(rc)) {
        g_detection_thread_running = false;
        g_detection_last_rc = rc;
        logger_write(
            "detector: threadCreate failed rc=0x%08lX prio=%d cpuid=%d",
            (unsigned long)rc,
            DETECTION_THREAD_PRIO,
            DETECTION_THREAD_CPUID
        );
        return false;
    }

    rc = threadStart(&g_detection_thread);
    if (R_FAILED(rc)) {
        g_detection_thread_running = false;
        g_detection_last_rc = rc;
        threadClose(&g_detection_thread);
        logger_write("detector: threadStart failed rc=0x%08lX", (unsigned long)rc);
        return false;
    }

    g_detection_thread_started = true;
    logger_write("detector: worker started");
    return true;
}

static void update_status_file(const char* state) {
    FILE* f;

    if (!g_fs_ready) return;

    f = fopen(STATUS_PATH, "w");
    if (!f) return;

    fprintf(
        f,
        "state=%s\n"
        "session_id=%llu\n"
        "uptime_sec=%llu\n"
        "stage=%s\n"
        "last_rc=0x%08lX\n"
        "heartbeats=%llu\n"
        "sm=%d fs=%d setsys=%d pmshell=%d pminfo=%d nifm=%d socket=%d\n" 
        "detector_started=%d detector_running=%d detector_ns=%d kill_switch=%d\n"
        "detector_alive=%d detector_last_hb=%llu\n"
        "detector_attempts=%llu detector_ok=%llu detector_fail=%llu detector_streak=%u\n"
        "detector_cooldown_until=%llu detector_last_rc=0x%08lX\n",
        state ? state : "UNKNOWN",
        (unsigned long long)g_session_id,
        (unsigned long long)sec_since_boot_now(),
        g_stage,
        (unsigned long)g_last_rc,
        (unsigned long long)g_heartbeat_count,
        g_sm_ready,
        g_fs_ready,
        g_setsys_ready,
        g_pmshell_ready,
        g_pminfo_ready,
        g_nifm_ready,
        g_socket_ready,
        g_detection_thread_started,
        g_detection_thread_running ? 1 : 0,
        g_ns_ready,
        g_detection_kill_switch,
        g_detection_thread_alive ? 1 : 0,
        (unsigned long long)g_detection_thread_last_heartbeat_sec,
        (unsigned long long)g_detection_attempt_count,
        (unsigned long long)g_detection_success_count,
        (unsigned long long)g_detection_fail_count,
        (unsigned int)g_detection_fail_streak,
        (unsigned long long)g_detection_disabled_until_sec,
        (unsigned long)g_detection_last_rc
    );
    fclose(f);
}

static void detect_previous_unclean_shutdown(void) {
    FILE* f;
    char buf[512];
    size_t n;

    f = fopen(STATUS_PATH, "r");
    if (!f) return;

    n = fread(buf, 1, sizeof(buf) - 1, f);
    fclose(f);
    buf[n] = '\0';

    if (strstr(buf, "state=RUNNING") != NULL) {
        g_unclean_prev = true;
        logger_write("warn: previous session did not shutdown cleanly (possible crash/hang)");
    }
}

void __libnx_initheap(void) {
    static u8 inner_heap[INNER_HEAP_SIZE];
    extern void* fake_heap_start;
    extern void* fake_heap_end;

    fake_heap_start = inner_heap;
    fake_heap_end = inner_heap + sizeof(inner_heap);
}

void __appInit(void) {
    Result rc;

    // Delay early-boot service.
    svcSleepThread(APPINIT_DELAY_NS);

    rc = smInitialize();
    if (R_FAILED(rc)) {
        logger_set_enabled(false);
        return;
    }

    g_sm_ready = true;
    logger_set_enabled(false);
}

void __appExit(void) {
    set_stage("exit");
    logger_write("shutdown: begin");
    update_status_file("STOPPED");

    stop_detection_worker();
    http_server_stop(&g_server);
    if (g_socket_ready) socketExit();
    if (g_nifm_ready) nifmExit();
    if (g_pminfo_ready) pminfoExit();
    if (g_pmshell_ready) pmshellExit();
    if (g_ns_ready) nsExit();
    if (g_setsys_ready) setsysExit();
    if (g_fs_ready) {
        fsdevUnmountAll();
        fsExit();
    }
    if (g_sm_ready) smExit();
}

#ifdef __cplusplus
}
#endif

int main(int argc, char* argv[]) {
    u64 ticks = 0;
    bool http_started = false;

    (void)argc;
    (void)argv;

    memset(&g_server, 0, sizeof(g_server));
    telemetry_init(&g_telemetry);
    g_session_id = sec_since_boot_now();

    while (1) {
        if ((ticks % INIT_RETRY_TICKS) == 0) {
            Result rc;

            if (!g_fs_ready) {
                set_stage("fs.init");
                rc = fsInitialize();
                g_last_rc = rc;
                if (R_SUCCEEDED(rc)) {
                    rc = fsdevMountSdmc();
                    g_last_rc = rc;
                    if (R_SUCCEEDED(rc)) {
                        g_fs_ready = true;
                        mkdir("sdmc:/switch", 0777);
                        mkdir("sdmc:/switch/switch-dcrpc", 0777);
                        logger_set_enabled(true);
                        logger_write("boot: fs ready");
                        detect_previous_unclean_shutdown();
                        update_status_file("RUNNING");
                    } else {
                        fsExit();
                    }
                }
            }

            if (!g_setsys_ready) {
                set_stage("setsys.init");
                rc = setsysInitialize();
                g_last_rc = rc;
                if (R_SUCCEEDED(rc)) {
                    SetSysFirmwareVersion fw;
                    rc = setsysGetFirmwareVersion(&fw);
                    g_last_rc = rc;
                    if (R_SUCCEEDED(rc)) {
                        snprintf(g_fw_str, sizeof(g_fw_str), "%u.%u.%u", fw.major, fw.minor, fw.micro);
                        g_fw_valid = true;
                        telemetry_set_firmware(&g_telemetry, g_fw_str);
                        logger_write("init: firmware=%s", g_fw_str);
                    }
                    g_setsys_ready = true;
                }
            }

            if (!g_nifm_ready) {
                set_stage("nifm.init");
                rc = nifmInitialize(NifmServiceType_User);
                g_last_rc = rc;
                if (R_SUCCEEDED(rc)) g_nifm_ready = true;
            }

            if (!g_socket_ready) {
                set_stage("socket.init");
                rc = socketInitializeDefault();
                g_last_rc = rc;
                if (R_SUCCEEDED(rc)) g_socket_ready = true;
            }

            if (g_socket_ready && !http_started) {
                set_stage("http.start");
                http_started = http_server_start(&g_server, &g_telemetry, HTTP_PORT);
                logger_write("http: start %s port=%d", http_started ? "ok" : "failed", HTTP_PORT);
            }

            refresh_detection_kill_switch();

            // Start detection
            if (http_started && ENABLE_RISKY_MAINLOOP_DETECTION && !g_detection_kill_switch) { 
                if (ENABLE_PM_SERVICES && !g_pmshell_ready) { 
                    set_stage("pmshell.init"); 
                    rc = pmshellInitialize(); 
                    g_last_rc = rc; 
                    if (R_SUCCEEDED(rc)) { 
                        g_pmshell_ready = true; 
                        logger_write("init: pmshell ready"); 
                    } else { 
                        logger_write("init: pmshell failed rc=0x%08lX", (unsigned long)rc); 
                    } 
                } 

                if (ENABLE_PM_SERVICES && !g_pminfo_ready) {
                    set_stage("pminfo.init");
                    rc = pminfoInitialize();
                    g_last_rc = rc;
                    if (R_SUCCEEDED(rc)) {
                        g_pminfo_ready = true;
                        logger_write("init: pminfo ready");
                    } else {
                        logger_write("init: pminfo failed rc=0x%08lX", (unsigned long)rc);
                    }
                }
                
                g_detection_services_ready = (g_pmshell_ready && g_pminfo_ready); 
                if (g_detection_services_ready && !g_detection_services_ready_logged) { 
                    g_detection_services_ready_logged = true; 
                    logger_write( 
                        "detect: services ready (pmshell=%d pminfo=%d)", 
                        g_pmshell_ready, 
                        g_pminfo_ready 
                    ); 
                } 
            } 

            if (ENABLE_DETECTION_WORKER && http_started && !g_detection_thread_started && !g_detection_kill_switch) {
                const u64 uptime = sec_since_boot_now();
                if (uptime >= DETECTION_START_DELAY_SEC) {
                    start_detection_worker();
                } else if (!g_detection_wait_logged) {
                    g_detection_wait_logged = true;
                    logger_write(
                        "detector: delayed start active (uptime=%llus < %us)",
                        (unsigned long long)uptime,
                        (unsigned int)DETECTION_START_DELAY_SEC
                    );
                }
            }

            if (ENABLE_DETECTION_WORKER && g_detection_thread_started) {
                const u64 now = sec_since_boot_now();
                const bool stale_heartbeat =
                    (g_detection_thread_last_heartbeat_sec > 0) &&
                    ((now - g_detection_thread_last_heartbeat_sec) > DETECTION_HEARTBEAT_TIMEOUT_SEC);
                if (!g_detection_thread_alive || stale_heartbeat) {
                    logger_write(
                        "detector: stale worker detected (alive=%d last_hb=%llus now=%llus), disabling detection",
                        g_detection_thread_alive ? 1 : 0,
                        (unsigned long long)g_detection_thread_last_heartbeat_sec,
                        (unsigned long long)now
                    );
                    g_detection_kill_switch = true;
                    g_detection_disabled_until_sec = now + DETECTION_STALE_DISABLE_SEC;
                    stop_detection_worker();
                    logger_write(
                        "detector: disabled for %us after stale worker",
                        (unsigned int)DETECTION_STALE_DISABLE_SEC
                    );
                }
            }
        }

        if (ticks == 0) {
            logger_write(
                "telemetry: mode=%s",
                ENABLE_DETECTION_WORKER ? "worker-detection" :
                (ENABLE_RISKY_MAINLOOP_DETECTION ?
                    (ENABLE_PM_SERVICES ? "pm+ns-mainloop-detection" : "ns-mainloop-detection")
                    : "safe-mode")
            );
        }
        set_stage("telemetry.update");
        telemetry_update(
            &g_telemetry,
            ENABLE_RISKY_MAINLOOP_DETECTION && http_started && g_detection_services_ready && !g_detection_kill_switch
        );
        if (ENABLE_RISKY_MAINLOOP_DETECTION && http_started && g_detection_services_ready && !g_detection_kill_switch) {
            log_active_title_if_changed();
        }

        if ((ticks % HEARTBEAT_TICKS) == 0) {
            char dbg[512];
            g_heartbeat_count++;
            set_stage("heartbeat");
            http_server_build_debug_json(&g_server, dbg, sizeof(dbg));
            logger_write(
                "heartbeat: n=%llu uptime=%llus stage=%s rc=0x%08lX sm=%d fs=%d setsys=%d pmshell=%d pminfo=%d nifm=%d socket=%d http_started=%d detector_started=%d detector_run=%d detector_alive=%d detector_hb=%llu detector_ns=%d detector_streak=%u detector_kill=%d cooldown_until=%llu unclean_prev=%d", 
                (unsigned long long)g_heartbeat_count,
                (unsigned long long)sec_since_boot_now(),
                g_stage,
                (unsigned long)g_last_rc,
                g_sm_ready,
                g_fs_ready,
                g_setsys_ready,
                g_pmshell_ready, 
                g_pminfo_ready, 
                g_nifm_ready,
                g_socket_ready,
                http_started,
                g_detection_thread_started,
                g_detection_thread_running ? 1 : 0,
                g_detection_thread_alive ? 1 : 0,
                (unsigned long long)g_detection_thread_last_heartbeat_sec,
                g_ns_ready,
                (unsigned int)g_detection_fail_streak,
                g_detection_kill_switch,
                (unsigned long long)g_detection_disabled_until_sec,
                g_unclean_prev
            );
            logger_write("heartbeat-http: %s", dbg);
            update_status_file("RUNNING");
        }

        ticks++;
        svcSleepThread(LOOP_SLEEP_NS);
    }

    return 0;
}
