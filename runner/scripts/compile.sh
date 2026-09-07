#!/bin/bash
# compile.sh — Compiles student C++ SFML 2.6.2 code
# Usage: compile.sh <source_file> <output_file>

set -e

SOURCE_FILE="$1"
OUTPUT_FILE="$2"

if [ -z "${SOURCE_FILE}" ] || [ -z "${OUTPUT_FILE}" ]; then
    echo "Usage: compile.sh <source_file> <output_file>"
    exit 1
fi

if [ ! -f "${SOURCE_FILE}" ]; then
    echo "Error: Source file not found: ${SOURCE_FILE}"
    exit 1
fi

# Find all .cpp files in /workspace
WORKSPACE_DIR="/workspace"
CPP_FILES=""
if [ -d "${WORKSPACE_DIR}" ]; then
    # Collect all .cpp files under /workspace
    CPP_FILES=$(find "${WORKSPACE_DIR}" -type f -name "*.cpp" 2>/dev/null | tr '\n' ' ')
fi

if [ -z "${CPP_FILES}" ]; then
    CPP_FILES="${SOURCE_FILE}"
fi

echo "Compiling C++ sources: ${CPP_FILES}"

# Compile with SFML 2.6.2 libraries
g++ ${CPP_FILES} \
    -std=c++17 \
    -o "${OUTPUT_FILE}" \
    -I/workspace \
    -I/workspace/src \
    -I/workspace/include \
    -I/usr/local/include \
    -L/usr/local/lib \
    -lsfml-graphics \
    -lsfml-window \
    -lsfml-system \
    -lsfml-audio \
    -lsfml-network \
    -Wl,-rpath,/usr/local/lib

echo "Compilation successful."

