/*
 * [AURA] native bridge.
 *
 * A thin, flat C API over the Growtopia-compatible ENet fork so that C# never
 * has to know ENet's internal struct layouts. This file only creates UDP
 * hosts, connects, sends and receives packets. It does not open files,
 * read the keyboard, touch the registry, start processes or contact any
 * address other than the one the caller passes to aura_host_connect().
 */
#include <string.h>
#include <stdint.h>
#include <enet/enet.h>

#if defined(_WIN32)
#  define AURA_API __declspec(dllexport)
#else
#  define AURA_API __attribute__((visibility("default")))
#endif

/* Mirrors AuraNative.Event in C#. Field order and sizes are fixed. */
typedef struct aura_event {
    int32_t  type;      /* 0 none, 1 connect, 2 disconnect, 3 receive */
    uint8_t  channel;
    uint8_t  pad0[3];
    uint32_t data;
    uint32_t pad1;
    void*    peer;
    void*    packet;    /* ENetPacket*, receive only; free with aura_packet_destroy */
    uint8_t* bytes;
    uint32_t length;
    uint32_t pad2;
} aura_event;

AURA_API int  aura_init(void)       { return enet_initialize(); }
AURA_API void aura_deinit(void)     { enet_deinitialize(); }
AURA_API int  aura_event_size(void) { return (int) sizeof(aura_event); }

/*
 * mode 0 = faces the Growtopia game: receives the game's new integrity
 *          header and answers with the classic header.
 * mode 1 = faces the Growtopia server: sends the new integrity header.
 * Both use CRC32 checksums and the range coder, like the official client.
 */
AURA_API void* aura_host_create(int mode, const char* bind_ip, uint16_t port, uint32_t peer_count)
{
    ENetHost* host;
    if (mode == 0) {
        ENetAddress address;
        memset(&address, 0, sizeof(address));
        if (bind_ip && bind_ip[0]) {
            if (enet_address_set_host_ip(&address, bind_ip) != 0) return NULL;
        } else {
            address.host = ENET_HOST_ANY;
        }
        address.port = port;
        host = enet_host_create(&address, peer_count, 2, 0, 0);
    } else {
        host = enet_host_create(NULL, peer_count, 2, 0, 0);
    }
    if (!host) return NULL;

    if (enet_host_compress_with_range_coder(host) != 0) {
        enet_host_destroy(host);
        return NULL;
    }
    host->checksum = enet_crc32;
    if (mode == 0) host->usingNewPacketForServer = 1;
    else           host->usingNewPacket = 1;
    return host;
}

/* Test helper: lets the test suite emulate the real game and the real server. */
AURA_API void aura_host_set_flags(void* host, int using_new_packet, int using_new_packet_for_server)
{
    if (!host) return;
    ((ENetHost*) host)->usingNewPacket = (enet_uint8) using_new_packet;
    ((ENetHost*) host)->usingNewPacketForServer = (enet_uint8) using_new_packet_for_server;
}

AURA_API void aura_host_destroy(void* host)
{
    if (host) enet_host_destroy((ENetHost*) host);
}

AURA_API void* aura_host_connect(void* host, const char* ip, uint16_t port)
{
    ENetAddress address;
    if (!host || !ip) return NULL;
    memset(&address, 0, sizeof(address));
    if (enet_address_set_host_ip(&address, ip) != 0) return NULL;
    address.port = port;
    return enet_host_connect((ENetHost*) host, &address, 2, 0);
}

AURA_API int aura_host_service(void* host, uint32_t timeout_ms, aura_event* out)
{
    ENetEvent ev;
    int rc;
    if (!host || !out) return -1;
    memset(out, 0, sizeof(*out));
    rc = enet_host_service((ENetHost*) host, &ev, timeout_ms);
    if (rc <= 0) return rc;

    out->type    = (int32_t) ev.type;
    out->channel = ev.channelID;
    out->data    = ev.data;
    out->peer    = ev.peer;
    if (ev.type == ENET_EVENT_TYPE_RECEIVE && ev.packet) {
        out->packet = ev.packet;
        out->bytes  = ev.packet->data;
        out->length = (uint32_t) ev.packet->dataLength;
    }
    return rc;
}

AURA_API void aura_host_flush(void* host)
{
    if (host) enet_host_flush((ENetHost*) host);
}

AURA_API void aura_packet_destroy(void* packet)
{
    if (packet) enet_packet_destroy((ENetPacket*) packet);
}

AURA_API int aura_peer_send(void* peer, uint8_t channel, const uint8_t* data, uint32_t length, uint32_t flags)
{
    ENetPacket* packet;
    if (!peer || (!data && length)) return -1;
    packet = enet_packet_create(data, length, flags);
    if (!packet) return -1;
    if (enet_peer_send((ENetPeer*) peer, channel, packet) != 0) {
        enet_packet_destroy(packet);
        return -1;
    }
    return 0;
}

AURA_API int  aura_peer_state(void* peer) { return peer ? (int) ((ENetPeer*) peer)->state : 0; }
AURA_API void aura_peer_disconnect(void* peer, uint32_t data)       { if (peer) enet_peer_disconnect((ENetPeer*) peer, data); }
AURA_API void aura_peer_disconnect_later(void* peer, uint32_t data) { if (peer) enet_peer_disconnect_later((ENetPeer*) peer, data); }
AURA_API void aura_peer_disconnect_now(void* peer, uint32_t data)   { if (peer) enet_peer_disconnect_now((ENetPeer*) peer, data); }
AURA_API void aura_peer_reset(void* peer)                           { if (peer) enet_peer_reset((ENetPeer*) peer); }

AURA_API void aura_peer_timeout(void* peer, uint32_t limit, uint32_t minimum, uint32_t maximum)
{
    if (peer) enet_peer_timeout((ENetPeer*) peer, limit, minimum, maximum);
}

/* Writes "a.b.c.d" into buf and returns the port, or 0 on failure. */
AURA_API uint16_t aura_peer_address(void* peer, char* buf, uint32_t buf_len)
{
    if (!peer || !buf || buf_len < 16) return 0;
    if (enet_address_get_host_ip(&((ENetPeer*) peer)->address, buf, buf_len) != 0) return 0;
    return ((ENetPeer*) peer)->address.port;
}
