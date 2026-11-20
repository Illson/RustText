import ctypes
import os
import queue
import subprocess
import sys
import threading
import time
from ctypes import wintypes
from pathlib import Path


HEIGHT = 2000


def api():
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                   wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.GetConsoleScreenBufferInfo.argtypes = [wintypes.HANDLE, wintypes.LPVOID]
    kernel.SetConsoleScreenBufferSize.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.SetConsoleScreenBufferSize.restype = wintypes.BOOL
    kernel.SetConsoleCursorPosition.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.SetConsoleCursorPosition.restype = wintypes.BOOL
    kernel.WriteConsoleW.argtypes = [wintypes.HANDLE, wintypes.LPCWSTR, wintypes.DWORD,
                                     ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
    kernel.WriteConsoleW.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    kernel.SetConsoleCtrlHandler.argtypes = [ctypes.c_void_p, wintypes.BOOL]
    kernel.SetConsoleCtrlHandler.restype = wintypes.BOOL
    kernel.GenerateConsoleCtrlEvent.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.GenerateConsoleCtrlEvent.restype = wintypes.BOOL
    kernel.GetConsoleCP.restype = wintypes.UINT
    return kernel


def check(result, name):
    if not result:
        raise ctypes.WinError(ctypes.get_last_error(), name)


def write_rows(kernel, handle, rows):
    text = "".join(row + "\r\n" for row in rows)
    written = wintypes.DWORD()
    check(kernel.WriteConsoleW(handle, text, len(text), ctypes.byref(written), None), "WriteConsoleW")
    if written.value != len(text):
        raise RuntimeError(f"WriteConsoleW wrote {written.value} of {len(text)} characters")


def fixture(kernel):
    handle = kernel.CreateFileW("CONOUT$", 0xC0000000, 3, None, 3, 0, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error(), "CreateFileW(CONOUT$)")
    # Keep the host's width so the buffer is never narrower than the console window.
    info = (ctypes.c_short * 11)()
    check(kernel.GetConsoleScreenBufferInfo(handle, ctypes.byref(info)), "GetConsoleScreenBufferInfo")
    check(kernel.SetConsoleScreenBufferSize(handle, info[0] | (HEIGHT << 16)), "SetConsoleScreenBufferSize")
    check(kernel.SetConsoleCursorPosition(handle, (HEIGHT - 1) << 16), "SetConsoleCursorPosition")

    handler_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.DWORD)
    handler = handler_type(lambda event: event in (0, 1))
    check(kernel.SetConsoleCtrlHandler(ctypes.cast(handler, ctypes.c_void_p), True), "SetConsoleCtrlHandler")
    try:
        print("ready", flush=True)
        for command in sys.stdin:
            command = command.strip()
            if command == "break":
                check(kernel.GenerateConsoleCtrlEvent(0, 0), "GenerateConsoleCtrlEvent")
                print("ok 0", flush=True)
                continue

            if command.startswith("U"):
                rows = [f"S{number:06d}" for number in range(1, int(command[1:]) + 1)]
            elif command.startswith("R"):
                rows = ["repeat"] * int(command[1:])
            elif command == "burst":
                number = 1
                for index in range(62):
                    count = (37 * index + 13) % 115 + 1
                    write_rows(kernel, handle, (f"B{item:06d}" for item in range(number, number + count)))
                    number += count
                    time.sleep(0.013)
                print(f"ok {number - 1}", flush=True)
                continue
            else:
                raise RuntimeError(f"Unknown fixture command: {command}")

            write_rows(kernel, handle, rows)
            print(f"ok {len(rows)}", flush=True)
        return 0
    finally:
        kernel.SetConsoleCtrlHandler(ctypes.cast(handler, ctypes.c_void_p), False)
        kernel.CloseHandle(handle)


def start_reader(stream):
    lines = queue.Queue()

    def read():
        try:
            for line in stream:
                lines.put(line.rstrip("\r\n"))
        finally:
            lines.put(None)

    threading.Thread(target=read, daemon=True).start()
    return lines


def take(lines, deadline, reason):
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError(f"Timed out waiting for {reason}")
    try:
        line = lines.get(timeout=remaining)
    except queue.Empty as error:
        raise TimeoutError(f"Timed out waiting for {reason}") from error
    if line is None:
        raise RuntimeError(f"Output closed while waiting for {reason}")
    return line


def expect(lines, expected, timeout=10):
    deadline = time.monotonic() + timeout
    actual = []
    while len(actual) < len(expected):
        header = take(lines, deadline, "console frame")
        try:
            count = int(header)
        except ValueError as error:
            raise RuntimeError(f"Invalid console frame header: {header!r}") from error
        if not 0 <= count <= HEIGHT:
            raise RuntimeError(f"Unexpected console frame size: {count}")
        actual.extend(take(lines, deadline, "console row") for _ in range(count))
    if actual != expected:
        raise AssertionError(f"Expected {len(expected)} rows, captured {len(actual)} or got them out of order")


def idle(lines):
    try:
        line = lines.get(timeout=0.6)
    except queue.Empty:
        return
    if line is None:
        raise RuntimeError("Console reader exited during idle polling")
    raise AssertionError(f"Unexpected duplicate console output: {line!r}")


def send_fixture(process, lines, command, expected):
    process.stdin.write(command + "\n")
    process.stdin.flush()
    if take(lines, time.monotonic() + 5, f"fixture response to {command}") != f"ok {expected}":
        raise RuntimeError(f"Fixture did not complete {command}")


def stop(process):
    if process is None:
        return
    try:
        process.stdin.close()
    except OSError:
        pass
    try:
        process.wait(timeout=3)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=3)
    for stream in (process.stdin, process.stdout, process.stderr):
        if stream is not None:
            stream.close()


def run(executable):
    if not executable.is_file():
        raise FileNotFoundError(f"rusttext.exe was not found: {executable}")
    kernel = api()
    if kernel.GetConsoleCP():
        check(kernel.SetConsoleCtrlHandler(None, False), "SetConsoleCtrlHandler(parent)")

    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = subprocess.SW_HIDE
    fixture_process = None
    worker = None
    try:
        fixture_process = subprocess.Popen(
            [sys.executable, "-B", "-u", str(Path(__file__).resolve()), "--fixture"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, bufsize=1, startupinfo=startup, creationflags=subprocess.CREATE_NEW_CONSOLE,
        )
        fixture_lines = start_reader(fixture_process.stdout)
        if take(fixture_lines, time.monotonic() + 10, "fixture startup") != "ready":
            raise RuntimeError("Hidden console fixture failed to start")

        worker = subprocess.Popen(
            [str(executable), "--read-console-buffer", str(fixture_process.pid)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", bufsize=1, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        worker_lines = start_reader(worker.stdout)
        expect(worker_lines, [""] * (HEIGHT - 1))
        send_fixture(fixture_process, fixture_lines, "U300", 300)
        expect(worker_lines, [f"S{number:06d}" for number in range(1, 301)])
        send_fixture(fixture_process, fixture_lines, "R300", 300)
        expect(worker_lines, ["repeat"] * 300)
        send_fixture(fixture_process, fixture_lines, "R1", 1)
        expect(worker_lines, ["repeat"])
        idle(worker_lines)
        send_fixture(fixture_process, fixture_lines, "R1999", 1999)
        expect(worker_lines, ["repeat"] * 1999)
        idle(worker_lines)
        send_fixture(fixture_process, fixture_lines, "R1", 1)
        expect(worker_lines, ["repeat"])
        idle(worker_lines)
        send_fixture(fixture_process, fixture_lines, "break", 0)
        send_fixture(fixture_process, fixture_lines, "U1", 1)
        expect(worker_lines, ["S000001"])
        idle(worker_lines)

        send_fixture(fixture_process, fixture_lines, "burst", 3560)
        expect(worker_lines, [f"B{number:06d}" for number in range(1, 3561)], timeout=15)
        idle(worker_lines)
        fixture_process.stdin.close()
        if fixture_process.wait(timeout=5) or worker.wait(timeout=5):
            raise AssertionError("The console reader should exit cleanly after its source exits")
        print("Console capture smoke test passed")
    finally:
        try:
            stop(worker)
        finally:
            stop(fixture_process)


def main():
    if os.name != "nt":
        raise RuntimeError("This smoke test requires Windows")
    if sys.argv[1:] == ["--fixture"]:
        return fixture(api())
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python tests/windows/console_capture.py <rusttext.exe-path>")
    run(Path(sys.argv[1]).resolve())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
