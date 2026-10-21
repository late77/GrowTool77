#!/bin/sh
# Rebuilds the native [AURA] bridge from the sources in this folder.
#   Windows DLL (cross-compile with MinGW):  sh build.sh win
#   Linux .so (used by the test suite):      sh build.sh linux
set -e
cd "$(dirname "$0")"
SRCS="aura_enet.c enet/callbacks.c enet/compress.c enet/host.c enet/list.c enet/packet.c enet/peer.c enet/protocol.c"
if [ "$1" = "win" ]; then
  x86_64-w64-mingw32-gcc -O2 -DNDEBUG -shared -Ienet/include $SRCS enet/win32.c \
    -o aura_enet.dll -lws2_32 -lwinmm -static-libgcc -Wl,--no-insert-timestamp -s
  echo "built aura_enet.dll"
else
  gcc -O2 -DNDEBUG -fPIC -shared -fvisibility=hidden -Ienet/include \
    -DHAS_FCNTL=1 -DHAS_POLL=1 -DHAS_GETADDRINFO=1 -DHAS_GETNAMEINFO=1 \
    -DHAS_INET_PTON=1 -DHAS_INET_NTOP=1 -DHAS_MSGHDR_FLAGS=1 -DHAS_SOCKLEN_T=1 \
    $SRCS enet/unix.c -o libaura_enet.so
  echo "built libaura_enet.so"
fi
