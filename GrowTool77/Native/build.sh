#!/bin/sh
# Rebuilds the native [AURA] bridge (ENet relay + OpenSSL TLS front door).
#   Windows DLL (cross-compile with MinGW):  sh build.sh win
#   Linux .so (used by the test suite):      sh build.sh linux
# Set OPENSSL_DIR to an OpenSSL source/build tree that contains libssl.a and
# libcrypto.a plus include/. Defaults to the tree used during development.
set -e
cd "$(dirname "$0")"
OPENSSL_DIR="${OPENSSL_DIR:-/home/claude/research/openssl}"
ENET="enet/callbacks.c enet/compress.c enet/host.c enet/list.c enet/packet.c enet/peer.c enet/protocol.c"
if [ "$1" = "win" ]; then
  x86_64-w64-mingw32-gcc -O2 -DNDEBUG -shared -Ienet/include -I"$OPENSSL_DIR/include" \
    aura_enet.c aura_tls.c $ENET enet/win32.c \
    "$OPENSSL_DIR/libssl.a" "$OPENSSL_DIR/libcrypto.a" \
    -o aura_enet.dll -lws2_32 -lwinmm -lcrypt32 -lbcrypt -static-libgcc -Wl,--no-insert-timestamp -s
  echo "built aura_enet.dll"
else
  gcc -O2 -DNDEBUG -fPIC -shared -fvisibility=hidden -Ienet/include \
    -DHAS_FCNTL=1 -DHAS_POLL=1 -DHAS_GETADDRINFO=1 -DHAS_GETNAMEINFO=1 \
    -DHAS_INET_PTON=1 -DHAS_INET_NTOP=1 -DHAS_MSGHDR_FLAGS=1 -DHAS_SOCKLEN_T=1 \
    aura_enet.c aura_tls.c $ENET enet/unix.c -lssl -lcrypto -o libaura_enet.so
  echo "built libaura_enet.so"
fi
