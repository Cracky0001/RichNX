#pragma once

#include <stdarg.h>
#include <stdbool.h>

void logger_set_enabled(bool enabled);
void logger_write(const char* fmt, ...);
void logger_vwrite(const char* fmt, va_list args);
