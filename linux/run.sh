#!/bin/sh
# Lancia i benchmark (1 thread e tutti i thread, 10 render ciascuno) uno dopo l'altro.
cd "$(dirname "$0")/out" || exit 1
for b in ./rmbench-c ./rmbench-rs-native ./rmbench-rs-pgo ./net/rmbench-net; do
    [ -x "$b" ] && "$b" -n -b 10
done
