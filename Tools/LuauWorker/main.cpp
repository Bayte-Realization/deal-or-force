#include "lua.h"
#include "lualib.h"
#include "Luau/Compiler.h"
#include <fstream>
#include <iostream>
#include <string>
#include <cstdlib>

struct MemoryBudget { size_t used = 0; };
static void* allocate(void* data, void* ptr, size_t old_size, size_t new_size)
{
    auto& budget = *static_cast<MemoryBudget*>(data);
    if (!ptr) old_size = 0;
    if (!new_size) { budget.used -= old_size; std::free(ptr); return nullptr; }
    if (budget.used - old_size + new_size > 64 * 1024 * 1024) return nullptr;
    auto* result = std::realloc(ptr, new_size);
    if (result) budget.used = budget.used - old_size + new_size;
    return result;
}

static bool read_bounded(std::istream& stream, std::string& output, size_t limit)
{
    char value;
    while (stream.get(value))
    {
        if (output.size() == limit) return false;
        output.push_back(value);
    }
    return !stream.bad();
}

int main(int argc, char** argv)
{
    if (argc != 2) { std::cerr << "Expected one Luau script path\n"; return 2; }
    std::ifstream file(argv[1], std::ios::binary);
    std::string source, request;
    if (!file || !read_bounded(file, source, 256 * 1024) || !read_bounded(std::cin, request, 2 * 1024 * 1024))
    { std::cerr << "Script/request missing or oversized\n"; return 2; }
    MemoryBudget memory;
    lua_State* vm = lua_newstate(allocate, &memory);
    if (!vm) return 3;
    luaL_openlibs(vm);
    // Keep stdout exclusively for the protocol. Scripts return JSON rather than print it.
    lua_pushnil(vm); lua_setglobal(vm, "print");
    luaL_sandbox(vm);
    luaL_sandboxthread(vm);
    const auto bytecode = Luau::compile(source);
    if (luau_load(vm, argv[1], bytecode.data(), bytecode.size(), 0) || lua_pcall(vm, 0, 0, 0))
    { std::cerr << lua_tostring(vm, -1) << '\n'; lua_close(vm); return 4; }
    lua_getglobal(vm, "on_hook");
    if (!lua_isfunction(vm, -1))
    { std::cerr << "Missing on_hook(request_json: string): string\n"; lua_close(vm); return 4; }
    lua_pushlstring(vm, request.data(), request.size());
    if (lua_pcall(vm, 1, 1, 0))
    { std::cerr << lua_tostring(vm, -1) << '\n'; lua_close(vm); return 4; }
    size_t size = 0;
    const char* response = lua_type(vm, -1) == LUA_TSTRING ? lua_tolstring(vm, -1, &size) : nullptr;
    if (!response || size > 256 * 1024)
    { std::cerr << "Response must be bounded JSON text\n"; lua_close(vm); return 5; }
    std::cout.write(response, size);
    lua_close(vm);
    return 0;
}
