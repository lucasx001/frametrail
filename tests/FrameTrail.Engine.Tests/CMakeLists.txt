add_executable(FrameTrail.Engine.Tests engine_tests.cpp)
target_link_libraries(FrameTrail.Engine.Tests PRIVATE FrameTrail.Engine)
add_test(NAME FrameTrail.Engine.Tests COMMAND FrameTrail.Engine.Tests)
