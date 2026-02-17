#include "telemetry.h"

#include <stdio.h>
#include <string.h>

#define PROGRAM_QUERY_INTERVAL_SEC 3

static u64 sec_since_boot_now(void) {
    return armTicksToNs(armGetSystemTick()) / 1000000000ULL;
}

static void copy_utf8_trunc(char* dst, size_t dst_size, const char* src) {
    size_t n = 0;
    if (dst_size == 0) return;
    if (!src) {
        dst[0] = 0;
        return;
    }

    n = strnlen(src, dst_size - 1);
    memcpy(dst, src, n);
    dst[n] = '\0';
}

static void json_escape(const char* in, char* out, size_t out_size) {
    size_t oi = 0;
    size_t i;

    if (out_size == 0) {
        return;
    }

    for (i = 0; in && in[i] != '\0' && oi + 2 < out_size; i++) {
        const char c = in[i];
        if (c == '\\' || c == '"') {
            if (oi + 2 >= out_size) break;
            out[oi++] = '\\';
            out[oi++] = c;
        } else if ((unsigned char)c < 0x20) {
            out[oi++] = ' ';
        } else {
            out[oi++] = c;
        }
    }

    out[oi] = '\0';
}

void telemetry_init(TelemetryState* state) {
    memset(state, 0, sizeof(*state));
    rmutexInit(&state->lock);
    state->started_sec = sec_since_boot_now();
    state->next_query_sec = state->started_sec;
    state->pending_program_id = 0;
    state->pending_match_count = 0;
    state->detection_mode = false;
    snprintf(state->active_game, sizeof(state->active_game), "HOME");
    snprintf(state->firmware, sizeof(state->firmware), "unknown");
}

void telemetry_set_firmware(TelemetryState* state, const char* firmware) {
    rmutexLock(&state->lock);
    copy_utf8_trunc(state->firmware, sizeof(state->firmware), firmware ? firmware : "unknown");
    rmutexUnlock(&state->lock);
}

void telemetry_update(TelemetryState* state, bool allow_pm_query, bool allow_battery_query, bool allow_dock_query) {
    u64 now = sec_since_boot_now();
    u64 program_id = 0;
    u64 process_id = 0;
    Result pm_rc = 0;
    Result pminfo_rc = 0;
    Result ns_rc = 0;
    Result svc_rc = 0;
    Result psm_charge_rc = 0;
    Result psm_charger_rc = 0;
    Result dock_rc = 0;
    u32 battery_percent = 0;
    u32 opmode_info = 0;
    PsmChargerType charger_type = PsmChargerType_Unconnected;
    AppletOperationMode opmode = AppletOperationMode_Handheld;
    bool query_attempted = false;
    bool have_program = false;
    bool battery_percent_valid = false;
    bool is_charging_valid = false;
    bool is_docked_valid = false;
    bool is_docked = false;
    u32 dock_detection_source = 0;
    bool should_query_program = false;
    u32 source = 0;

    if (allow_battery_query) {
        psm_charge_rc = psmGetBatteryChargePercentage(&battery_percent);
        if (R_SUCCEEDED(psm_charge_rc)) {
            battery_percent_valid = true;
        }

        psm_charger_rc = psmGetChargerType(&charger_type);
        if (R_SUCCEEDED(psm_charger_rc)) {
            is_charging_valid = true;
        }
    }

    if (allow_dock_query) {
        dock_rc = appletGetOperationModeSystemInfo(&opmode_info);
        if (R_SUCCEEDED(dock_rc)) {
            opmode = appletGetOperationMode();
            is_docked = (opmode == AppletOperationMode_Console);
            is_docked_valid = true;
            dock_detection_source = 1;
        }
        (void)opmode_info;

        // Fallback for sysmodule contexts where applet mode may be unavailable.
        if (!is_docked_valid && is_charging_valid) {
            is_docked = (charger_type == PsmChargerType_EnoughPower);
            is_docked_valid = true;
            dock_detection_source = 2;
        }
    }

    rmutexLock(&state->lock);
    state->sample_count++;
    state->last_update_sec = now;
    if (allow_battery_query) {
        state->last_psm_charge_result = psm_charge_rc;
        state->last_psm_charger_result = psm_charger_rc;
        state->battery_percent_valid = battery_percent_valid;
        state->is_charging_valid = is_charging_valid;

        if (battery_percent_valid) {
            state->battery_percent = battery_percent;
        }
        if (is_charging_valid) {
            state->is_charging = (charger_type != PsmChargerType_Unconnected);
        }
    }
    if (allow_dock_query) {
        state->last_dock_result = dock_rc;
        state->is_docked_valid = is_docked_valid;
        state->dock_detection_source = dock_detection_source;
        if (is_docked_valid) {
            state->is_docked = is_docked;
        }
    }

    if (allow_pm_query) {
        state->detection_mode = true;
    }
    should_query_program = allow_pm_query && now >= state->next_query_sec;
    if (should_query_program) {
        state->next_query_sec = now + PROGRAM_QUERY_INTERVAL_SEC;
    }
    rmutexUnlock(&state->lock);
    
    if (!should_query_program) {
        return;
    }

    query_attempted = true;
    pm_rc = pmshellGetApplicationProcessIdForShell(&process_id);
    if (R_SUCCEEDED(pm_rc) && process_id != 0) {
        pminfo_rc = pminfoGetProgramId(&program_id, process_id);
        if (R_SUCCEEDED(pminfo_rc) && program_id != 0) {
            have_program = true;
            source = 1;
        } else {

        }
    }

    // Fallback: If pm-shit doesn't work
    if (!have_program) {
        u64 pids[64];
        s32 out_count = 0;
        svc_rc = svcGetProcessList(&out_count, pids, (s32)(sizeof(pids) / sizeof(pids[0])));
        if (R_SUCCEEDED(svc_rc) && out_count > 0) {
            u64 best = 0;
            int i;
            for (i = 0; i < out_count; i++) {
                u64 pid = pids[i];
                u64 candidate = 0;
                Result rc = pminfoGetProgramId(&candidate, pid);
                if (R_FAILED(rc) || candidate == 0) {
                    continue;
                }

                if ((candidate & 0xFFFF000000000000ULL) != 0x0100000000000000ULL) {
                    continue;
                }
                if ((candidate & 0xFFFFFFFFFFFF0000ULL) == 0x0100000000000000ULL) {
                    continue; 
                }

                if (candidate == 0x0100000000001000ULL) { // qlaunch
                    continue;
                }
                if (candidate == 0x00FF0000A1B2C3D4ULL) { // sysmodule title id
                    continue;
                }

                if (candidate > best) {
                    best = candidate;
                    process_id = pid;
                    program_id = candidate;
                    pminfo_rc = rc;
                }
            }

            if (best != 0) {
                have_program = true;
                source = 2;
            }
        }
    }

    rmutexLock(&state->lock);
    if (query_attempted) {
        state->detection_attempt_count++;
        state->detection_last_query_sec = now;
        state->last_pm_result = pm_rc;
        state->last_pminfo_result = pminfo_rc;
        state->last_ns_result = ns_rc;
        state->last_svc_result = svc_rc;
        state->last_process_id = process_id;
        state->detection_source = source;

        if (have_program) {
            state->detection_success_count++;
            state->detection_fail_streak = 0;
            state->detection_last_success_sec = now;
        } else {
            state->detection_fail_count++;
            if (state->detection_fail_streak < 0xFFFFFFFFU) {
                state->detection_fail_streak++;
            }
        }
    }

    if (!have_program) {
        state->pending_program_id = 0;
        state->pending_match_count = 0;
        state->active_program_id = 0;
        copy_utf8_trunc(state->active_game, sizeof(state->active_game), "HOME");
        rmutexUnlock(&state->lock);
        return;
    }

    if (state->pending_program_id == program_id) {
        if (state->pending_match_count < 255) state->pending_match_count++;
    } else {
        state->pending_program_id = program_id;
        state->pending_match_count = 1;
    }

    if (state->pending_match_count >= 2) {
        state->active_program_id = program_id;
        snprintf(state->active_game, sizeof(state->active_game), "0x%016llX",
                 (unsigned long long)program_id);
    }
    rmutexUnlock(&state->lock);
}

void telemetry_build_json(TelemetryState* state, char* out, size_t out_size) {
    char escaped_game[512];
    char escaped_firmware[64];
    u64 started_sec = 0;
    u64 last_update_sec = 0;
    u64 sample_count = 0;
    u64 active_program_id = 0;
    Result last_pm_result = 0;
    Result last_pminfo_result = 0;
    Result last_ns_result = 0;
    Result last_svc_result = 0;
    u64 last_process_id = 0;
    u32 detection_source = 0;
    bool detection_mode = false;
    u64 detection_attempt_count = 0;
    u64 detection_success_count = 0;
    u64 detection_fail_count = 0;
    u32 detection_fail_streak = 0;
    u64 detection_last_query_sec = 0;
    u64 detection_last_success_sec = 0;
    u32 battery_percent = 0;
    bool battery_percent_valid = false;
    bool is_charging = false;
    bool is_charging_valid = false;
    bool is_docked = false;
    bool is_docked_valid = false;
    Result last_psm_charge_result = 0;
    Result last_psm_charger_result = 0;
    Result last_dock_result = 0;
    u32 dock_detection_source = 0;
    char battery_percent_json[16];
    char is_charging_json[8];
    char is_docked_json[8];
    char active_game[sizeof(state->active_game)];
    char firmware[sizeof(state->firmware)];

    rmutexLock(&state->lock);
    started_sec = state->started_sec;
    last_update_sec = state->last_update_sec;
    sample_count = state->sample_count;
    active_program_id = state->active_program_id;
    last_pm_result = state->last_pm_result;
    last_pminfo_result = state->last_pminfo_result;
    last_ns_result = state->last_ns_result;
    last_svc_result = state->last_svc_result;
    last_process_id = state->last_process_id;
    detection_source = state->detection_source;
    detection_mode = state->detection_mode;
    detection_attempt_count = state->detection_attempt_count;
    detection_success_count = state->detection_success_count;
    detection_fail_count = state->detection_fail_count;
    detection_fail_streak = state->detection_fail_streak;
    detection_last_query_sec = state->detection_last_query_sec;
    detection_last_success_sec = state->detection_last_success_sec;
    battery_percent = state->battery_percent;
    battery_percent_valid = state->battery_percent_valid;
    is_charging = state->is_charging;
    is_charging_valid = state->is_charging_valid;
    is_docked = state->is_docked;
    is_docked_valid = state->is_docked_valid;
    last_psm_charge_result = state->last_psm_charge_result;
    last_psm_charger_result = state->last_psm_charger_result;
    last_dock_result = state->last_dock_result;
    dock_detection_source = state->dock_detection_source;
    copy_utf8_trunc(active_game, sizeof(active_game), state->active_game);
    copy_utf8_trunc(firmware, sizeof(firmware), state->firmware);
    rmutexUnlock(&state->lock);

    json_escape(active_game, escaped_game, sizeof(escaped_game));
    json_escape(firmware, escaped_firmware, sizeof(escaped_firmware));
    if (battery_percent_valid) {
        snprintf(battery_percent_json, sizeof(battery_percent_json), "%u", (unsigned int)battery_percent);
    } else {
        snprintf(battery_percent_json, sizeof(battery_percent_json), "null");
    }
    if (is_charging_valid) {
        snprintf(is_charging_json, sizeof(is_charging_json), "%s", is_charging ? "true" : "false");
    } else {
        snprintf(is_charging_json, sizeof(is_charging_json), "null");
    }
    if (is_docked_valid) {
        snprintf(is_docked_json, sizeof(is_docked_json), "%s", is_docked ? "true" : "false");
    } else {
        snprintf(is_docked_json, sizeof(is_docked_json), "null");
    }

    snprintf(
        out,
        out_size,
        "{"
        "\"service\":\"RichNX\","
        "\"firmware\":\"%s\","
        "\"active_program_id\":\"0x%016llX\","
        "\"active_game\":\"%s\","
        "\"started_sec\":%llu,"
        "\"last_update_sec\":%llu,"
        "\"sample_count\":%llu,"
        "\"last_pm_result\":\"0x%08lX\","
        "\"last_pminfo_result\":\"0x%08lX\","
        "\"last_ns_result\":\"0x%08lX\","
        "\"last_svc_result\":\"0x%08lX\","
        "\"last_process_id\":\"0x%016llX\","
        "\"detection_source\":%u,"
        "\"detection_mode\":%s,"
        "\"detection_attempt_count\":%llu,"
        "\"detection_success_count\":%llu,"
        "\"detection_fail_count\":%llu,"
        "\"detection_fail_streak\":%u,"
        "\"detection_last_query_sec\":%llu,"
        "\"detection_last_success_sec\":%llu,"
        "\"battery_percent\":%s,"
        "\"is_charging\":%s,"
        "\"is_docked\":%s,"
        "\"dock_detection_source\":%u,"
        "\"last_psm_charge_result\":\"0x%08lX\","
        "\"last_psm_charger_result\":\"0x%08lX\","
        "\"last_dock_result\":\"0x%08lX\""
        "}",
        escaped_firmware,
        (unsigned long long)active_program_id,
        escaped_game,
        (unsigned long long)started_sec,
        (unsigned long long)last_update_sec,
        (unsigned long long)sample_count,
        (unsigned long)last_pm_result,
        (unsigned long)last_pminfo_result,
        (unsigned long)last_ns_result,
        (unsigned long)last_svc_result,
        (unsigned long long)last_process_id,
        (unsigned int)detection_source,
        detection_mode ? "true" : "false",
        (unsigned long long)detection_attempt_count,
        (unsigned long long)detection_success_count,
        (unsigned long long)detection_fail_count,
        (unsigned int)detection_fail_streak,
        (unsigned long long)detection_last_query_sec,
        (unsigned long long)detection_last_success_sec,
        battery_percent_json,
        is_charging_json,
        is_docked_json,
        (unsigned int)dock_detection_source,
        (unsigned long)last_psm_charge_result,
        (unsigned long)last_psm_charger_result,
        (unsigned long)last_dock_result
    );
}
