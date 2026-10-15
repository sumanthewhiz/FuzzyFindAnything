#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>
#include "index.h"
#include "logger.h"

#define FFA_API extern "C" __declspec(dllexport)
using NativeIndex = diskann::Index<float, uint32_t, uint32_t>;
using LogCallback = void(__cdecl *)(int, const char *);
static thread_local std::string last_error;
static std::mutex logger_mutex;
static std::once_flag redirect_once;

struct IndexHandle
{
    uint32_t dimensions;
    uint32_t capacity;
    uint32_t threads;
    std::unique_ptr<NativeIndex> index;
};

static std::unique_ptr<NativeIndex> make_index(uint32_t dimensions, uint32_t capacity, uint32_t threads)
{
    auto parameters = diskann::IndexWriteParametersBuilder(96, 48)
                          .with_num_threads(threads).with_alpha(1.2f).with_saturate_graph(false).build();
    return std::make_unique<NativeIndex>(diskann::Metric::L2, dimensions, capacity,
        std::make_shared<diskann::IndexWriteParameters>(parameters),
        std::make_shared<diskann::IndexSearchParams>(128, threads), 1, true, true, false, false, 0, false);
}

static IndexHandle &checked(void *handle)
{
    if (!handle) throw std::invalid_argument("The DiskANN handle is null.");
    return *static_cast<IndexHandle *>(handle);
}

static void validate_vector(const float *values, uint32_t dimensions)
{
    if (!values) throw std::invalid_argument("The vector buffer is null.");
    for (uint32_t i = 0; i < dimensions; ++i)
        if (!std::isfinite(values[i])) throw std::invalid_argument("A vector contains a non-finite value.");
}

template <typename F> static int invoke(F action) noexcept
{
    try { last_error.clear(); action(); return 0; }
    catch (const std::exception &error) { last_error = error.what(); return -1; }
    catch (...) { last_error = "Unrecognized native DiskANN failure."; return -1; }
}

FFA_API const char *__cdecl ffa_diskann_version() noexcept { return "0.7.0+df225d32;abi=1"; }
FFA_API const char *__cdecl ffa_diskann_error() noexcept { return last_error.c_str(); }
FFA_API int __cdecl ffa_diskann_logger(LogCallback callback) noexcept
{
    return invoke([&] {
        std::lock_guard<std::mutex> guard(logger_mutex);
        diskann::SetCustomLogger([callback](diskann::LogLevel level, const char *message) {
            if (callback) callback(static_cast<int>(level), message);
        });
    });
}

FFA_API int __cdecl ffa_diskann_create(uint32_t dimensions, uint32_t capacity, uint32_t threads, void **result) noexcept
{
    if (result) *result = nullptr;
    return invoke([&] {
        std::call_once(redirect_once, [] { std::cout.rdbuf(std::cerr.rdbuf()); });
        if (!result || dimensions == 0 || dimensions % 8 != 0 || capacity == 0 || threads == 0)
            throw std::invalid_argument("Invalid dimensions, capacity, threads, or output handle.");
        auto handle = std::make_unique<IndexHandle>();
        handle->dimensions = dimensions;
        handle->capacity = capacity;
        handle->threads = threads;
        handle->index = make_index(dimensions, capacity, threads);
        *result = handle.release();
    });
}

FFA_API int __cdecl ffa_diskann_destroy(void *handle) noexcept
{
    return invoke([&] { delete static_cast<IndexHandle *>(handle); });
}

FFA_API int __cdecl ffa_diskann_insert(void *handle, const float *values, uint32_t count, uint32_t tag) noexcept
{
    return invoke([&] {
        auto &index = checked(handle);
        if (count != index.dimensions || tag == 0) throw std::invalid_argument("Invalid vector dimension or tag.");
        validate_vector(values, count);
        if (index.index->insert_point(values, tag) != 0) throw std::runtime_error("DiskANN insertion failed.");
    });
}

FFA_API int __cdecl ffa_diskann_delete(void *handle, uint32_t tag) noexcept
{
    return invoke([&] {
        if (checked(handle).index->lazy_delete(tag) != 0) throw std::runtime_error("DiskANN could not delete the tag.");
    });
}

FFA_API int __cdecl ffa_diskann_search(void *handle, const float *query, uint32_t dimensions,
    uint32_t count, uint32_t *tags, float *distances, uint32_t *written) noexcept
{
    if (written) *written = 0;
    return invoke([&] {
        auto &index = checked(handle);
        if (!written || !tags || !distances || dimensions != index.dimensions || count == 0)
            throw std::invalid_argument("Invalid search buffers or dimensions.");
        validate_vector(query, dimensions);
        std::fill(tags, tags + count, 0);
        std::fill(distances, distances + count, std::numeric_limits<float>::infinity());
        std::vector<float *> vectors;
        *written = static_cast<uint32_t>(index.index->search_with_tags(query, count, std::max(128u, count), tags, distances, vectors));
    });
}

FFA_API int __cdecl ffa_diskann_load(void *handle, const char *prefix) noexcept
{
    return invoke([&] {
        auto &index = checked(handle);
        if (!prefix || !*prefix) throw std::invalid_argument("An index prefix is required.");
        index.index->load(prefix, index.threads, 128);
    });
}

FFA_API int __cdecl ffa_diskann_save_packed(void *handle, const float *values, const uint32_t *tags,
    uint32_t count, const char *prefix) noexcept
{
    return invoke([&] {
        auto &handle_value = checked(handle);
        if (!values || !tags || !prefix || !*prefix || count == 0)
            throw std::invalid_argument("Packed snapshots require vectors, tags, and an output prefix.");
        std::vector<uint32_t> identifiers(tags, tags + count);
        for (uint32_t i = 0; i < count; ++i)
        {
            if (!tags[i]) throw std::invalid_argument("Vector tag zero is reserved.");
            validate_vector(values + static_cast<size_t>(i) * handle_value.dimensions, handle_value.dimensions);
        }
        auto packed = make_index(handle_value.dimensions, count, handle_value.threads);
        packed->build(values, count, identifiers);
        packed->save(prefix, false);
    });
}
