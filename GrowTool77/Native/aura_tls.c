/*
 * [AURA] TLS front door.
 *
 * A tiny blocking HTTPS 1.1 server built on the same OpenSSL that the working
 * GTProxy uses, with the same handshake options (TLS 1.2 minimum, no
 * compression). Growtopia's own client is also OpenSSL, and it refuses the
 * handshake offered by Windows' TLS stack — this file exists so the game
 * talks to OpenSSL on both ends.
 *
 * It serves exactly one path (/growtopia/server_data.php and anything else) by
 * handing the request body up to C#, which returns the bytes to send back.
 * Certificate and key are passed in from C# as PEM text held in memory. This
 * file opens no files, starts no processes and makes no outbound connections;
 * the only socket it owns is the local listener on 127.0.0.1.
 */
#include <string.h>
#include <stdint.h>
#include <stdlib.h>

#if defined(_WIN32)
   /* Keep wincrypt.h out: it #defines X509_NAME and friends, which clash with OpenSSL. */
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <winsock2.h>
#  include <ws2tcpip.h>
#  undef X509_NAME
#  undef X509_EXTENSIONS
#  undef PKCS7_SIGNER_INFO
#  undef OCSP_REQUEST
#  undef OCSP_RESPONSE
#endif

#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/pem.h>
#include <openssl/x509v3.h>
#include <openssl/evp.h>
#include <time.h>
#if defined(_WIN32)
#  include <process.h>
#else
#  include <pthread.h>
#endif

#if defined(_WIN32)
#  define AURA_API __declspec(dllexport)
   typedef SOCKET sock_t;
#  define BAD_SOCK INVALID_SOCKET
#else
#  include <sys/socket.h>
#  include <netinet/in.h>
#  include <arpa/inet.h>
#  include <unistd.h>
#  define AURA_API __attribute__((visibility("default")))
   typedef int sock_t;
#  define BAD_SOCK (-1)
#  define closesocket close
#endif

/*
 * C# gives this callback the request (method, path, body) and fills in the
 * response body. It returns the HTTP status code; 0 means "drop the
 * connection". request_id lets C# match the call to its own state.
 */
typedef int (*aura_http_cb)(
    void*        user,
    const char*  method,
    const char*  path,
    const uint8_t* body, uint32_t body_len,
    uint8_t**    out_body, uint32_t* out_len,
    char*        out_content_type, uint32_t content_type_cap);

/* Optional notifications so C# can log connection-level events on port 443.
   event: 0 = TCP accepted, 1 = TLS handshake ok, 2 = TLS handshake failed. */
typedef void (*aura_tls_event_cb)(void* user, int event, const char* detail);

/* forward declarations */
AURA_API uint8_t* aura_tls_alloc(uint32_t len);
AURA_API void     aura_tls_free(uint8_t* p);

typedef struct aura_tls_server {
    SSL_CTX*          ctx;
    sock_t            listener;
    aura_http_cb      cb;
    aura_tls_event_cb event_cb;
    void*             user;
    volatile int      running;
    char*             cert_pem;   /* PEM of the server certificate, for trust install */
} aura_tls_server;

static int _strncasecmp_portable(const char* a, const char* b, int n)
{
    for (int i = 0; i < n; i++) {
        unsigned char ca = (unsigned char) a[i], cb = (unsigned char) b[i];
        if (ca >= 'A' && ca <= 'Z') ca = (unsigned char)(ca - 'A' + 'a');
        if (cb >= 'A' && cb <= 'Z') cb = (unsigned char)(cb - 'A' + 'a');
        if (ca != cb) return ca - cb;
        if (ca == 0) return 0;
    }
    return 0;
}

static int tls_recv_head(SSL* ssl, char* buf, int cap, int* body_start);

AURA_API int aura_tls_global_init(void)
{
#if defined(_WIN32)
    WSADATA wsa;                                   /* reference-counted; safe next to ENet's own */
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return -2;
#endif
    return OPENSSL_init_ssl(OPENSSL_INIT_LOAD_SSL_STRINGS | OPENSSL_INIT_LOAD_CRYPTO_STRINGS
                            | OPENSSL_INIT_NO_LOAD_CONFIG, NULL) ? 0 : -1;
}

/* Builds the TLS context with GTProxy's exact server options around a
   certificate and key that are already in memory. Takes ownership of both. */
static aura_tls_server* build_server(X509* cert, EVP_PKEY* key, aura_http_cb cb, void* user)
{
    aura_tls_server* s;
    SSL_CTX* ctx;

    if (!cert || !key || !cb) { if (cert) X509_free(cert); if (key) EVP_PKEY_free(key); return NULL; }

    ctx = SSL_CTX_new(TLS_server_method());
    if (!ctx) { X509_free(cert); EVP_PKEY_free(key); return NULL; }

    /* Same as cpp-httplib / GTProxy: no compression, TLS 1.2 floor. */
    SSL_CTX_set_options(ctx, SSL_OP_NO_COMPRESSION | SSL_OP_NO_SESSION_RESUMPTION_ON_RENEGOTIATION);
    SSL_CTX_set_min_proto_version(ctx, TLS1_2_VERSION);

    if (SSL_CTX_use_certificate(ctx, cert) != 1
        || SSL_CTX_use_PrivateKey(ctx, key) != 1
        || SSL_CTX_check_private_key(ctx) != 1)
    {
        X509_free(cert); EVP_PKEY_free(key); SSL_CTX_free(ctx);
        return NULL;
    }
    /* Keep a PEM copy of the certificate so C# can install it as a trusted root. */
    char* cert_pem_copy = NULL;
    {
        BIO* mem = BIO_new(BIO_s_mem());
        if (mem) {
            if (PEM_write_bio_X509(mem, cert) == 1) {
                char* data = NULL;
                long len = BIO_get_mem_data(mem, &data);
                if (len > 0 && data) {
                    cert_pem_copy = (char*) malloc((size_t) len + 1);
                    if (cert_pem_copy) { memcpy(cert_pem_copy, data, (size_t) len); cert_pem_copy[len] = '\0'; }
                }
            }
            BIO_free(mem);
        }
    }

    X509_free(cert);          /* the context keeps its own references */
    EVP_PKEY_free(key);

    s = (aura_tls_server*) calloc(1, sizeof(*s));
    if (!s) { SSL_CTX_free(ctx); free(cert_pem_copy); return NULL; }
    s->cert_pem = cert_pem_copy;
    s->ctx      = ctx;
    s->listener = BAD_SOCK;
    s->cb       = cb;
    s->user     = user;
    s->running  = 0;
    return s;
}

/* Certificate and key given as PEM text (kept for tooling and tests). */
AURA_API aura_tls_server* aura_tls_create(const char* cert_pem, const char* key_pem, aura_http_cb cb, void* user)
{
    BIO* bio;
    X509* cert = NULL;
    EVP_PKEY* key = NULL;

    if (!cert_pem || !key_pem) return NULL;
    bio = BIO_new_mem_buf(cert_pem, -1);
    if (bio) { cert = PEM_read_bio_X509(bio, NULL, NULL, NULL); BIO_free(bio); }
    bio = BIO_new_mem_buf(key_pem, -1);
    if (bio) { key = PEM_read_bio_PrivateKey(bio, NULL, NULL, NULL); BIO_free(bio); }
    return build_server(cert, key, cb, user);
}

/*
 * Creates a fresh RSA-2048 key and a self-signed certificate for the
 * Growtopia login names entirely inside OpenSSL, in memory. Nothing touches
 * the Windows key store and nothing is written to disk. Valid for one year.
 */
AURA_API aura_tls_server* aura_tls_create_selfsigned(aura_http_cb cb, void* user)
{
    EVP_PKEY* key = EVP_PKEY_Q_keygen(NULL, NULL, "RSA", (size_t) 2048);
    X509* cert;
    X509_NAME* name;
    X509V3_CTX v3;
    X509_EXTENSION* ext;

    if (!key) return NULL;
    cert = X509_new();
    if (!cert) { EVP_PKEY_free(key); return NULL; }

    X509_set_version(cert, 2);                                            /* X.509 v3 */
    ASN1_INTEGER_set(X509_get_serialNumber(cert), (long) (time(NULL) & 0x7fffffff));
    X509_gmtime_adj(X509_getm_notBefore(cert), -24L * 60 * 60);
    X509_gmtime_adj(X509_getm_notAfter(cert),  365L * 24 * 60 * 60);
    X509_set_pubkey(cert, key);

    name = X509_get_subject_name(cert);
    X509_NAME_add_entry_by_txt(name, "CN", MBSTRING_ASC, (const unsigned char*) "www.growtopia1.com", -1, -1, 0);
    X509_set_issuer_name(cert, name);                                     /* self-signed */

    X509V3_set_ctx_nodb(&v3);
    X509V3_set_ctx(&v3, cert, cert, NULL, NULL, 0);
    ext = X509V3_EXT_nconf_nid(NULL, &v3, NID_subject_alt_name,
        "DNS:www.growtopia1.com,DNS:www.growtopia2.com,DNS:growtopia1.com,DNS:growtopia2.com");
    if (ext) { X509_add_ext(cert, ext, -1); X509_EXTENSION_free(ext); }
    /* Mark as a CA so, once installed as a trusted root, the client accepts
       this same certificate as the issuer of the server certificate. */
    ext = X509V3_EXT_nconf_nid(NULL, &v3, NID_basic_constraints, "critical,CA:TRUE");
    if (ext) { X509_add_ext(cert, ext, -1); X509_EXTENSION_free(ext); }
    ext = X509V3_EXT_nconf_nid(NULL, &v3, NID_key_usage, "critical,keyCertSign,digitalSignature,keyEncipherment");
    if (ext) { X509_add_ext(cert, ext, -1); X509_EXTENSION_free(ext); }

    if (!X509_sign(cert, key, EVP_sha256())) { X509_free(cert); EVP_PKEY_free(key); return NULL; }
    return build_server(cert, key, cb, user);
}

/* Registers an optional connection-event callback (see aura_tls_event_cb). */
AURA_API void aura_tls_set_event_cb(aura_tls_server* s, aura_tls_event_cb cb)
{
    if (s) s->event_cb = cb;
}

/* Binds 127.0.0.1:port and returns 0, or a negative error. */
AURA_API int aura_tls_listen(aura_tls_server* s, uint16_t port)
{
    struct sockaddr_in addr;
    int yes = 1;

    if (!s) return -1;
    s->listener = socket(AF_INET, SOCK_STREAM, 0);
    if (s->listener == BAD_SOCK) return -2;

#if defined(_WIN32)
    /* Windows: refuse to share the port with another program (SO_REUSEADDR would allow it). */
    setsockopt(s->listener, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char*)&yes, sizeof(yes));
#else
    setsockopt(s->listener, SOL_SOCKET, SO_REUSEADDR, (const char*)&yes, sizeof(yes));
#endif

    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port   = htons(port);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);   /* 127.0.0.1 only */

    if (bind(s->listener, (struct sockaddr*)&addr, sizeof(addr)) != 0) { closesocket(s->listener); s->listener = BAD_SOCK; return -3; }
    if (listen(s->listener, 16) != 0)                                  { closesocket(s->listener); s->listener = BAD_SOCK; return -4; }

    s->running = 1;
    return 0;
}

/* One accepted connection: handshake, read request, call back, reply. */
typedef struct conn_job { aura_tls_server* s; sock_t client; } conn_job;

static void set_socket_timeout(sock_t fd, int seconds)
{
#if defined(_WIN32)
    DWORD ms = (DWORD)(seconds * 1000);
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, (const char*)&ms, sizeof(ms));
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, (const char*)&ms, sizeof(ms));
#else
    struct timeval tv; tv.tv_sec = seconds; tv.tv_usec = 0;
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, (const void*)&tv, sizeof(tv));
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, (const void*)&tv, sizeof(tv));
#endif
}

static void handle_connection(aura_tls_server* s, sock_t client)
{
    SSL* ssl;
    set_socket_timeout(client, 20);               /* no single client can wedge the server */

    if (s->event_cb) s->event_cb(s->user, 0, "443");   /* TCP accepted */

    ssl = SSL_new(s->ctx);
    if (!ssl) { closesocket(client); return; }
    SSL_set_fd(ssl, (int) client);

    {
        int accept_rc = SSL_accept(ssl);
        if (accept_rc == 1 && s->event_cb) s->event_cb(s->user, 1, SSL_get_version(ssl));
        if (accept_rc != 1 && s->event_cb) {
            unsigned long e = ERR_get_error();
            char buf[128];
            ERR_error_string_n(e, buf, sizeof(buf));
            s->event_cb(s->user, 2, buf[0] ? buf : "handshake failed");
        }
        if (accept_rc == 1) {
            char head[16384];
            int body_start = 0;
            int head_len = tls_recv_head(ssl, head, (int)sizeof(head) - 1, &body_start);
            if (head_len > 0) {
                /* Parse the request line and Content-Length. */
                char method[16] = {0}, path[2048] = {0};
                int content_length = 0;
                sscanf(head, "%15s %2047s", method, path);

                {
                    const char* p = head;
                    while ((p = strchr(p, '\n')) != NULL) {
                        p++;
                        if (_strncasecmp_portable(p, "content-length:", 15) == 0) {
                            content_length = atoi(p + 15);
                            if (content_length < 0 || content_length > 4 * 1024 * 1024) content_length = 0;
                        }
                    }
                }

                uint8_t* body = NULL;
                int have = head_len - body_start;
                if (have < 0) have = 0;
                if (content_length > 0) {
                    body = (uint8_t*) malloc(content_length);
                    if (body) {
                        int copy = have < content_length ? have : content_length;
                        if (copy > 0) memcpy(body, head + body_start, copy);
                        int got = copy;
                        while (got < content_length) {
                            int n = SSL_read(ssl, body + got, content_length - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        content_length = got;
                    } else {
                        content_length = 0;
                    }
                }

                uint8_t* out_body = NULL;
                uint32_t out_len = 0;
                char content_type[128] = "text/html";
                int status = s->cb(s->user, method, path, body, (uint32_t)content_length,
                                   &out_body, &out_len, content_type, (uint32_t)sizeof(content_type));
                if (body) free(body);

                if (status > 0) {
                    char resp_head[512];
                    int n = snprintf(resp_head, sizeof(resp_head),
                        "HTTP/1.1 %d OK\r\nContent-Type: %s\r\nContent-Length: %u\r\nConnection: close\r\n\r\n",
                        status, content_type, out_len);
                    SSL_write(ssl, resp_head, n);
                    if (out_body && out_len) SSL_write(ssl, out_body, (int) out_len);
                }
                if (out_body) aura_tls_free(out_body);
            }
            SSL_shutdown(ssl);
        }
    }

    SSL_free(ssl);
    closesocket(client);
}

#if defined(_WIN32)
static unsigned __stdcall conn_thread(void* arg)
#else
static void* conn_thread(void* arg)
#endif
{
    conn_job* job = (conn_job*) arg;
    handle_connection(job->s, job->client);
    free(job);
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}

/* Serves connections until aura_tls_stop() is called. Blocks the caller.
   Each connection is handled on its own short-lived thread, so one slow or
   stuck client never blocks the next login attempt. */
AURA_API void aura_tls_serve(aura_tls_server* s)
{
    if (!s || s->listener == BAD_SOCK) return;

    while (s->running) {
        sock_t client = accept(s->listener, NULL, NULL);
        if (client == BAD_SOCK) {
            if (!s->running) break;
            continue;
        }

        conn_job* job = (conn_job*) malloc(sizeof(*job));
        if (!job) { closesocket(client); continue; }
        job->s = s;
        job->client = client;

#if defined(_WIN32)
        {
            uintptr_t th = _beginthreadex(NULL, 0, conn_thread, job, 0, NULL);
            if (th == 0) { closesocket(client); free(job); continue; }
            CloseHandle((HANDLE) th);
        }
#else
        {
            pthread_t th;
            if (pthread_create(&th, NULL, conn_thread, job) != 0) { closesocket(client); free(job); continue; }
            pthread_detach(th);
        }
#endif
    }
}

AURA_API void aura_tls_stop(aura_tls_server* s)
{
    if (!s) return;
    s->running = 0;
    if (s->listener != BAD_SOCK) {
        closesocket(s->listener);   /* wakes accept() */
        s->listener = BAD_SOCK;
    }
}

AURA_API void aura_tls_destroy(aura_tls_server* s)
{
    if (!s) return;
    if (s->listener != BAD_SOCK) closesocket(s->listener);
    if (s->ctx) SSL_CTX_free(s->ctx);
    free(s->cert_pem);
    free(s);
}

/* Returns the server certificate as PEM text (owned by the server; do not free). */
AURA_API const char* aura_tls_get_cert_pem(aura_tls_server* s)
{
    return s ? s->cert_pem : NULL;
}

/* Frees a response body that C# allocated with aura_tls_alloc. */
AURA_API uint8_t* aura_tls_alloc(uint32_t len) { return (uint8_t*) malloc(len ? len : 1); }
AURA_API void     aura_tls_free(uint8_t* p)    { if (p) free(p); }

/* Reads bytes until the end of the HTTP header ("\r\n\r\n"). */
static int tls_recv_head(SSL* ssl, char* buf, int cap, int* body_start)
{
    int len = 0;
    *body_start = 0;
    while (len < cap) {
        int n = SSL_read(ssl, buf + len, cap - len);
        if (n <= 0) return len > 0 ? len : -1;
        len += n;
        buf[len] = '\0';
        char* end = strstr(buf, "\r\n\r\n");
        if (end) { *body_start = (int)(end - buf) + 4; return len; }
    }
    return len;
}
