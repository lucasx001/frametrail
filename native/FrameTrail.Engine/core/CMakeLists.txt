cmake_minimum_required(VERSION 3.25)

include(FetchContent)
set(JSON_BuildTests OFF CACHE INTERNAL "")
set(JSON_Install OFF CACHE INTERNAL "")
FetchContent_Declare(nlohmann_json
    URL https://github.com/nlohmann/json/archive/refs/tags/v3.11.3.tar.gz
    URL_HASH SHA256=0d8ef5af7f9794e3263480193c491549b2ba6cc74bb018906202ada498a79406
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(nlohmann_json)

add_library(FrameTrail.Core STATIC src/project.cpp src/timeline.cpp src/scene.cpp)
target_include_directories(FrameTrail.Core PUBLIC include)
target_compile_features(FrameTrail.Core PUBLIC cxx_std_20)
target_link_libraries(FrameTrail.Core PRIVATE nlohmann_json::nlohmann_json)
set_target_properties(FrameTrail.Core PROPERTIES POSITION_INDEPENDENT_CODE ON)
if(MSVC)
    target_compile_options(FrameTrail.Core PRIVATE /W4 /permissive- /EHsc /utf-8)
else()
    target_compile_options(FrameTrail.Core PRIVATE -Wall -Wextra -Wpedantic)
endif()
