#include "http_server.h"

#include "logger.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

#define SERVER_STACK_SIZE (64 * 1024)
#define SERVER_THREAD_PRIO 0x2B
#define SERVER_THREAD_CPUID -2
#define ACCEPT_ERROR_REOPEN_THRESHOLD 32
#define ACCEPT_ERRNO_NET_UNREACH 113

// Use static stack memory for sysmodule thread stability (avoid heap-backed stack alloc failures).
static u8 g_http_thread_stack[SERVER_STACK_SIZE] __attribute__((aligned(0x1000)));

static bool http_server_open_listen_socket(HttpServer* server) {
    struct sockaddr_in addr;

    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(server->port);

    server->stage = 1; // creating socket
    server->listen_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (server->listen_fd < 0) {
        server->last_errno = errno;
        server->stage = -1;
        logger_write("http: socket failed errno=%d", errno);
        return false;
    }

    {
        int yes = 1;
        setsockopt(server->listen_fd, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));
    }

    server->stage = 2; // binding
    if (bind(server->listen_fd, (const struct sockaddr*)&addr, sizeof(addr)) < 0) {
        server->last_errno = errno;
        server->stage = -2;
        logger_write("http: bind failed errno=%d", errno);
        close(server->listen_fd);
        server->listen_fd = -1;
        return false;
    }

    server->stage = 3; // listening
    if (listen(server->listen_fd, 4) < 0) {
        server->last_errno = errno;
        server->stage = -3;
        logger_write("http: listen failed errno=%d", errno);
        close(server->listen_fd);
        server->listen_fd = -1;
        return false;
    }

    server->listening = true;
    server->stage = 4; // serving
    logger_write("http: listening on 0.0.0.0:%u", server->port);
    return true;
}

static void send_http_json(int client_fd, const char* json_body) {
    char response[4096];
    const int body_len = (int)strlen(json_body);

    snprintf(
        response,
        sizeof(response),
        "HTTP/1.1 200 OK\r\n"
        "Content-Type: application/json\r\n"
        "Access-Control-Allow-Origin: *\r\n"
        "Connection: close\r\n"
        "Content-Length: %d\r\n"
        "\r\n"
        "%s",
        body_len,
        json_body
    );

    send(client_fd, response, strlen(response), 0);
}

static void send_http_not_found(int client_fd) {
    static const char response[] =
        "HTTP/1.1 404 Not Found\r\n"
        "Connection: close\r\n"
        "Content-Length: 0\r\n"
        "\r\n";
    send(client_fd, response, sizeof(response) - 1, 0);
}

static void server_handle_client(HttpServer* server, int client_fd) {
    char req_buf[1024];
    int recv_len = recv(client_fd, req_buf, sizeof(req_buf) - 1, 0);
    if (recv_len < 0) {
        logger_write("http: recv failed errno=%d", errno);
        return;
    }

    req_buf[recv_len] = '\0';
    server->request_count++;

    if (strncmp(req_buf, "GET /debug", 10) == 0) {
        char json_body[1024];
        http_server_build_debug_json(server, json_body, sizeof(json_body));
        send_http_json(client_fd, json_body);
        return;
    }

    if (strncmp(req_buf, "GET /state", 10) != 0 && strncmp(req_buf, "GET / ", 6) != 0) {
        send_http_not_found(client_fd);
        return;
    }

    {
        char json_body[2048];
        telemetry_build_json(server->telemetry, json_body, sizeof(json_body));
        send_http_json(client_fd, json_body);
    }
}

static void http_server_thread(void* arg) {
    HttpServer* server = (HttpServer*)arg;
    int accept_error_streak = 0;

    if (!http_server_open_listen_socket(server)) {
        return;
    }

    while (server->running) {
        fd_set readfds;
        struct timeval timeout;
        int sel_rc;

        FD_ZERO(&readfds);
        FD_SET(server->listen_fd, &readfds);
        timeout.tv_sec = 1;
        timeout.tv_usec = 0;

        sel_rc = select(server->listen_fd + 1, &readfds, NULL, NULL, &timeout);
        if (sel_rc < 0) {
            if (errno == EINTR) {
                continue;
            }
            server->last_errno = errno;
            server->stage = -4;
            logger_write("http: select failed errno=%d", errno);
            break;
        }
        if (sel_rc == 0 || !FD_ISSET(server->listen_fd, &readfds)) {
            continue;
        }

        {
            int client_fd = accept(server->listen_fd, NULL, NULL);
            if (client_fd < 0) {
                if (errno != EINTR) {
                    const int accept_errno = errno;
                    server->last_errno = errno;
                    server->stage = -5;
                    logger_write("http: accept failed errno=%d", accept_errno);
                    accept_error_streak++;

                    if (accept_errno == ACCEPT_ERRNO_NET_UNREACH || accept_error_streak >= ACCEPT_ERROR_REOPEN_THRESHOLD) {
                        logger_write(
                            "http: recover-v2 reopen accept_errno=%d streak=%d",
                            accept_errno,
                            accept_error_streak
                        );
                        accept_error_streak = 0;
                        server->listening = false;
                        if (server->listen_fd >= 0) {
                            close(server->listen_fd);
                            server->listen_fd = -1;
                        }
                        svcSleepThread(500ULL * 1000000ULL);
                        if (!http_server_open_listen_socket(server)) {
                            svcSleepThread(1000ULL * 1000000ULL);
                        }
                    }
                }
                continue;
            }

            accept_error_streak = 0;
            server->accepted_count++;
            server_handle_client(server, client_fd);
            close(client_fd);
        }
    }

    server->listening = false;
    if (server->listen_fd >= 0) {
        close(server->listen_fd);
        server->listen_fd = -1;
    }

    logger_write("http: thread stopped");
}

bool http_server_start(HttpServer* server, TelemetryState* telemetry, unsigned short port) {
    Result rc;

    memset(server, 0, sizeof(*server));
    server->telemetry = telemetry;
    server->running = true;
    server->listen_fd = -1;
    server->port = port;
    server->accepted_count = 0;
    server->request_count = 0;
    server->last_errno = 0;
    server->stage = 0;
    server->listening = false;

    rc = threadCreate(
        &server->thread,
        http_server_thread,
        server,
        g_http_thread_stack,
        SERVER_STACK_SIZE,
        SERVER_THREAD_PRIO,
        SERVER_THREAD_CPUID
    );
    if (R_FAILED(rc)) {
        logger_write(
            "http: threadCreate failed rc=0x%08lX prio=%d cpuid=%d",
            (unsigned long)rc,
            SERVER_THREAD_PRIO,
            SERVER_THREAD_CPUID
        );
        server->running = false;
        return false;
    }

    rc = threadStart(&server->thread);
    if (R_FAILED(rc)) {
        logger_write("http: threadStart failed rc=0x%08lX", (unsigned long)rc);
        threadClose(&server->thread);
        server->running = false;
        return false;
    }

    return true;
}

void http_server_stop(HttpServer* server) {
    if (!server->running) {
        return;
    }

    server->running = false;
    if (server->listen_fd >= 0) {
        shutdown(server->listen_fd, SHUT_RDWR);
    }

    threadWaitForExit(&server->thread);
    threadClose(&server->thread);
}

void http_server_build_debug_json(const HttpServer* server, char* out, size_t out_size) {
    snprintf(
        out,
        out_size,
        "{"
        "\"running\":%s,"
        "\"listening\":%s,"
        "\"stage\":%d,"
        "\"listen_fd\":%d,"
        "\"port\":%u,"
        "\"accepted_count\":%llu,"
        "\"request_count\":%llu,"
        "\"last_errno\":%d"
        "}",
        server->running ? "true" : "false",
        server->listening ? "true" : "false",
        server->stage,
        server->listen_fd,
        (unsigned int)server->port,
        (unsigned long long)server->accepted_count,
        (unsigned long long)server->request_count,
        server->last_errno
    );
}
