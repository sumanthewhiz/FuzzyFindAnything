"""Readiness checkpoints and a conservative memory budget for inference workers."""

import ctypes
from dataclasses import dataclass
import threading

GIB = 1024 ** 3
WORKER_MEMORY = 3 * GIB
MEMORY_RESERVE = 2 * GIB
MODEL_STEPS = 6
STARTUP_STEPS = 9


def available_memory():
    class MemoryStatus(ctypes.Structure):
        _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong)] + [
            (name, ctypes.c_ulonglong) for name in (
                "total", "available", "pagefile", "available_pagefile", "virtual", "available_virtual", "extended"
            )
        ]
    status = MemoryStatus()
    status.length = ctypes.sizeof(status)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    if not kernel.GlobalMemoryStatusEx(ctypes.byref(status)):
        raise ctypes.WinError(ctypes.get_last_error())
    return status.available


@dataclass(frozen=True)
class WorkerPlan:
    workers: int
    threads: int


def plan_workers(processors, desired, memory):
    permitted = (memory - MEMORY_RESERVE) // WORKER_MEMORY
    if permitted < 1:
        raise MemoryError(
            f"At least {(WORKER_MEMORY + MEMORY_RESERVE) / GIB:.0f} GiB of available RAM is required "
            f"to start inference safely; {memory / GIB:.1f} GiB is available. Close other applications and retry."
        )
    if processors < 1 or desired < 1:
        raise ValueError("Inference requires a positive CPU and worker budget.")
    workers = min(desired, processors, permitted)
    return WorkerPlan(workers, max(1, processors // workers))


class StartupProgress:
    def __init__(self, report=None):
        self.lock = threading.RLock()
        self.report = report
        self.sequence = 0
        self.completed = 0
        self.stage = "Starting local engine"
        self.state = "Loading"
        self.error = ""

    def snapshot(self):
        with self.lock:
            return {
                "sequence": self.sequence, "percent": self.completed * 100 // STARTUP_STEPS,
                "completed_steps": self.completed, "total_steps": STARTUP_STEPS,
                "stage": self.stage, "state": self.state, "error": self.error,
            }

    def update(self, completed, stage, *, state="Loading", error=""):
        if not 0 <= completed <= STARTUP_STEPS or state not in ("Loading", "Ready", "Error"):
            raise ValueError("Invalid startup checkpoint.")
        if state == "Ready" and completed != STARTUP_STEPS:
            raise ValueError("Readiness requires every startup checkpoint.")
        with self.lock:
            if self.state in ("Ready", "Error") or completed < self.completed:
                return
            self.completed, self.stage, self.state, self.error = completed, stage, state, error
            self.sequence += 1
            value = self.snapshot()
        if self.report:
            self.report(value)

    def model_progress(self, completed, stage):
        if not 0 <= completed <= MODEL_STEPS:
            raise ValueError("Invalid model checkpoint.")
        self.update(2 + completed, stage)

    def ready(self):
        self.update(STARTUP_STEPS, "Search engine ready", state="Ready")

    def fail(self, error):
        self.update(self.completed, "Search engine unavailable", state="Error", error=str(error))
