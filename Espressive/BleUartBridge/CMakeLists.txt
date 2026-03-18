cmake_minimum_required(VERSION 3.16)

# Target must be set before project.cmake is included.
# Using CACHE ... FORCE so this overrides any stale CMakeCache.txt entry.
# The VS Code ESP-IDF extension also reads idf.adapterTargetName from
# .vscode/settings.json — both must agree.
set(IDF_TARGET "esp32s3" CACHE STRING "IDF target chip" FORCE)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(BleUartBridge)
