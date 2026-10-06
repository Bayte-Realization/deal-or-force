cmake_minimum_required(VERSION 3.24)
project(DealOrForceLuauWorker LANGUAGES CXX)
include(FetchContent)
set(LUAU_BUILD_CLI OFF CACHE BOOL "" FORCE)
set(LUAU_BUILD_TESTS OFF CACHE BOOL "" FORCE)
# Official Luau VM/compiler; pin the source, do not substitute a Lua interpreter.
FetchContent_Declare(luau GIT_REPOSITORY https://github.com/luau-lang/luau.git
    GIT_TAG 421cc8158a752c8933a3d73555fd79df631a90b6)
FetchContent_MakeAvailable(luau)
add_executable(deal-or-force-luau main.cpp)
target_compile_features(deal-or-force-luau PRIVATE cxx_std_17)
target_link_libraries(deal-or-force-luau PRIVATE Luau.Compiler Luau.VM)
