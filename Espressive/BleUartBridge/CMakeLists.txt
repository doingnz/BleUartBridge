cmake_minimum_required(VERSION 3.16)

# Target must be set before project.cmake is included.
# Changing this requires deleting build/ and sdkconfig then rebuilding.
set(IDF_TARGET "esp32s3")

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(BleUartBridge)
