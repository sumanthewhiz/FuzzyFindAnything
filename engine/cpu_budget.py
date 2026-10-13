"""One inherited physical-core budget shared by every application worker."""

import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import os
import struct


@dataclass(frozen=True)
class CpuBudget:
    total_cores: int
    processors: int
    affinity_mask: int
    workers: int
    threads_per_worker: int


def select_mask(core_masks: list[int], allowed: int, total_cores: int) -> int:
    limit = max(1, total_cores // 2)
    selected = 0
    for mask in core_masks:
        available = mask & allowed
        if available:
            selected |= available & -available
            if selected.bit_count() >= limit:
                break
    if not selected:
        raise RuntimeError("No processors are available within the indexing CPU budget.")
    return selected


def get_budget(apply: bool = False) -> CpuBudget:
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.GetProcessAffinityMask.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_size_t), ctypes.POINTER(ctypes.c_size_t)]
    kernel.SetProcessAffinityMask.argtypes = [wintypes.HANDLE, ctypes.c_size_t]
    kernel.GetLogicalProcessorInformationEx.argtypes = [ctypes.c_int, ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD)]
    kernel.GetProcessGroupAffinity.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.WORD), ctypes.POINTER(wintypes.WORD)]
    process = kernel.GetCurrentProcess()
    process_mask, system_mask = ctypes.c_size_t(), ctypes.c_size_t()
    if not kernel.GetProcessAffinityMask(process, ctypes.byref(process_mask), ctypes.byref(system_mask)):
        raise ctypes.WinError(ctypes.get_last_error())
    group_count = wintypes.WORD(64)
    groups = (wintypes.WORD * 64)()
    if not kernel.GetProcessGroupAffinity(process, ctypes.byref(group_count), groups):
        raise ctypes.WinError(ctypes.get_last_error())
    primary_group = groups[0]
    length = wintypes.DWORD()
    kernel.GetLogicalProcessorInformationEx(0, None, ctypes.byref(length))
    buffer = ctypes.create_string_buffer(length.value)
    if not kernel.GetLogicalProcessorInformationEx(0, buffer, ctypes.byref(length)):
        raise ctypes.WinError(ctypes.get_last_error())
    data = buffer.raw[:length.value]
    offset, total, masks = 0, 0, []
    while offset < len(data):
        relationship, size = struct.unpack_from("<II", data, offset)
        if size < 32 or offset + size > len(data):
            raise RuntimeError("Windows returned invalid CPU topology data.")
        if relationship == 0:
            total += 1
            count = struct.unpack_from("<H", data, offset + 30)[0]
            for index in range(count):
                mask, group = struct.unpack_from("<QH", data, offset + 32 + index * 16)
                if group == primary_group:
                    masks.append(mask)
        offset += size
    if total < 2:
        raise RuntimeError("Fuzzy Find Anything requires at least two physical CPU cores.")
    allowed = process_mask.value or system_mask.value
    mask = select_mask(masks, allowed, total)
    inherited = os.environ.get("FFA_CPU_MASK")
    if inherited:
        mask &= int(inherited, 16)
    if not mask:
        raise RuntimeError("The inherited CPU budget is incompatible with this worker.")
    if apply and not kernel.SetProcessAffinityMask(process, mask):
        raise ctypes.WinError(ctypes.get_last_error())
    processors = mask.bit_count()
    workers = 2 if processors >= 4 else 1
    threads = max(1, processors // workers)
    if apply:
        os.environ["FFA_CPU_MASK"] = f"{mask:X}"
        os.environ["FFA_CPU_LIMIT"] = str(processors)
        os.environ["FFA_TOTAL_CORES"] = str(total)
        for name in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
            os.environ[name] = str(threads)
    return CpuBudget(total, processors, mask, workers, threads)
