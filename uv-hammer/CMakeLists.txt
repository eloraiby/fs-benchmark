cmake_minimum_required(VERSION 2.8)

project(uv-hammer)

add_subdirectory(mimalloc)
add_subdirectory(HdrHistogram_c)
add_executable(${PROJECT_NAME}
        "hammer.c"
    )

if(UNIX)
    find_package(PkgConfig REQUIRED)
    pkg_check_modules(LIBUV REQUIRED libuv)

    SET(CMAKE_C_FLAGS "-Wall -Wextra -std=gnu99 -fdata-sections -O3 -ffunction-sections" CACHE INTERNAL "c compiler flags" FORCE)

    SET(CMAKE_EXE_LINKER_FLAGS "-Wl,--gc-sections")
    SET(CMAKE_CXX_LINK_EXECUTABLE "gcc")

    target_link_libraries(${PROJECT_NAME} mimalloc-static hdr_histogram_static ${LIBUV_LIBRARIES} m ${CMAKE_DL_LIBS})
    target_include_directories(${PROJECT_NAME} PUBLIC ${LIBUV_INCLUDE_DIRS})
    target_compile_options(${PROJECT_NAME} PUBLIC ${LIBUV_CFLAGS_OTHER} )
endif()