#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <vector>

extern "C" __declspec(dllimport) int __cdecl ffa_diskann_create(uint32_t, uint32_t, uint32_t, void **);
extern "C" __declspec(dllimport) int __cdecl ffa_diskann_insert(void *, const float *, uint32_t, uint32_t);
extern "C" __declspec(dllimport) int __cdecl ffa_diskann_search(void *, const float *, uint32_t, uint32_t, uint32_t *, float *, uint32_t *);
extern "C" __declspec(dllimport) int __cdecl ffa_diskann_delete(void *, uint32_t);
extern "C" __declspec(dllimport) int __cdecl ffa_diskann_destroy(void *);
extern "C" __declspec(dllimport) const char *__cdecl ffa_diskann_error();

static void check(int status) { if (status) throw std::runtime_error(ffa_diskann_error()); }
int main()
{
    void *index = nullptr;
    try
    {
        check(ffa_diskann_create(768, 128, 2, &index));
        std::vector<float> first(768), second(768);
        first[0] = 1; second[1] = 1;
        check(ffa_diskann_insert(index, first.data(), 768, 17));
        check(ffa_diskann_insert(index, second.data(), 768, 29));
        uint32_t tags[2], written = 0; float distances[2];
        check(ffa_diskann_search(index, first.data(), 768, 2, tags, distances, &written));
        if (written != 2 || tags[0] != 17) throw std::runtime_error("Native search returned incorrect tags.");
        check(ffa_diskann_delete(index, 17));
        check(ffa_diskann_destroy(index)); index = nullptr;
        std::cout << "Native DiskANN C ABI smoke passed\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        if (index) ffa_diskann_destroy(index);
        std::cerr << error.what() << '\n'; return 1;
    }
}
