//! Single-instance startup and launch request forwarding for RustText.
//!
//! The primary process holds an OS file lock for its entire lifetime and accepts
//! bounded, authenticated messages over a loopback-only TCP listener. The lock and
//! endpoint metadata live below the current user's profile directory.

use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, TryRecvError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

const PROTOCOL_VERSION: u8 = 1;
const MAX_MESSAGE_BYTES: usize = 256 * 1024;
const MAX_PATHS: usize = 512;
const MAX_PATH_BYTES: usize = 32 * 1024;
const STARTUP_RETRY: Duration = Duration::from_secs(3);
const CONNECT_RETRY: Duration = Duration::from_millis(40);
const READ_TIMEOUT: Duration = Duration::from_millis(700);
const POLL_INTERVAL: Duration = Duration::from_millis(35);
const ACK_OK: u8 = 1;
const ACK_REJECTED: u8 = 0;

/// A request sent when the application starts, including subsequent launches.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LaunchRequest {
    /// Directory from which relative command-line paths should be resolved.
    pub cwd: PathBuf,
    /// Paths exactly as supplied by the launching process.
    pub paths: Vec<PathBuf>,
    /// Second launches can use this to bring the existing window forward.
    pub activate: bool,
}

impl LaunchRequest {
    /// Captures this process's working directory and file arguments.
    pub fn from_process() -> io::Result<Self> {
        let cwd = std::env::current_dir()?;
        let paths = std::env::args_os().skip(1).map(PathBuf::from).collect();
        Ok(Self {
            cwd,
            paths,
            activate: true,
        })
    }

    /// Returns paths made absolute relative to the launcher's captured working directory.
    pub fn absolute_paths(&self) -> Vec<PathBuf> {
        self.paths
            .iter()
            .map(|path| {
                if path.is_absolute() {
                    path.clone()
                } else {
                    self.cwd.join(path)
                }
            })
            .collect()
    }

    fn validate(&self) -> io::Result<()> {
        if self.paths.len() > MAX_PATHS {
            return Err(invalid_input("too many file paths in launch request"));
        }
        if path_wire_len(&self.cwd) > MAX_PATH_BYTES
            || self
                .paths
                .iter()
                .any(|path| path_wire_len(path) > MAX_PATH_BYTES)
        {
            return Err(invalid_input("a launch path exceeds the size limit"));
        }
        Ok(())
    }
}

/// Result of claiming the per-user application instance.
pub enum InstanceRole {
    /// This process owns the lock and must keep the value alive until shutdown.
    Primary(SingleInstance),
    /// An existing process accepted the launch request.
    Forwarded,
}

/// The primary instance's lock and IPC receiver.
pub struct SingleInstance {
    _lock_file: File,
    stop: Arc<AtomicBool>,
    listener_thread: Option<JoinHandle<()>>,
    receiver: Receiver<LaunchRequest>,
    metadata_path: PathBuf,
}

impl SingleInstance {
    /// Claims the per-user instance, or forwards `initial_request` to its owner.
    pub fn acquire(initial_request: LaunchRequest) -> io::Result<InstanceRole> {
        initial_request.validate()?;
        let profile_dir = profile_directory()?;
        Self::acquire_in(&profile_dir, initial_request)
    }

    fn acquire_in(profile_dir: &Path, initial_request: LaunchRequest) -> io::Result<InstanceRole> {
        initial_request.validate()?;
        fs::create_dir_all(profile_dir)?;
        let lock_path = profile_dir.join("instance.lock");
        let metadata_path = profile_dir.join("instance.json");
        let lock_file = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(&lock_path)?;

        match lock_file.try_lock() {
            Ok(()) => Self::become_primary(lock_file, metadata_path),
            Err(std::fs::TryLockError::WouldBlock) => {
                match forward_to_primary(&metadata_path, initial_request) {
                    Ok(()) => {}
                    Err(forward_error) => {
                        // The current owner may be exiting or still publishing its
                        // endpoint. Recheck ownership so a stale lock-file race can
                        // become a new primary instead of failing the launch.
                        if let Ok(()) = lock_file.try_lock() {
                            return Self::become_primary(lock_file, metadata_path);
                        }
                        return Err(forward_error);
                    }
                }
                Ok(InstanceRole::Forwarded)
            }
            Err(std::fs::TryLockError::Error(error)) => Err(error),
        }
    }

    /// Returns the next request forwarded by a subsequent application launch.
    pub fn try_receive(&self) -> io::Result<Option<LaunchRequest>> {
        match self.receiver.try_recv() {
            Ok(request) => Ok(Some(request)),
            Err(TryRecvError::Empty) => Ok(None),
            Err(TryRecvError::Disconnected) => Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "single-instance listener stopped",
            )),
        }
    }

    fn become_primary(lock_file: File, metadata_path: PathBuf) -> io::Result<InstanceRole> {
        let listener = TcpListener::bind(("127.0.0.1", 0))?;
        listener.set_nonblocking(true)?;
        let address = listener.local_addr()?;
        let capability = random_capability()?;
        let metadata = EndpointMetadata {
            version: PROTOCOL_VERSION,
            address: address.to_string(),
            capability,
        };
        let bytes = serde_json::to_vec(&metadata).map_err(io::Error::other)?;
        write_metadata_atomically(&metadata_path, &bytes)?;

        let (sender, receiver) = mpsc::channel();
        let stop = Arc::new(AtomicBool::new(false));
        let thread_stop = Arc::clone(&stop);
        let listener_thread = thread::Builder::new()
            .name("rusttext-instance-ipc".into())
            .spawn(move || listener_loop(listener, metadata.capability, sender, thread_stop))?;

        Ok(InstanceRole::Primary(Self {
            _lock_file: lock_file,
            stop,
            listener_thread: Some(listener_thread),
            receiver,
            metadata_path,
        }))
    }
}

impl Drop for SingleInstance {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(thread) = self.listener_thread.take() {
            let _ = thread.join();
        }
        let _ = fs::remove_file(&self.metadata_path);
        // `_lock_file` remains held until after metadata cleanup and this drop returns.
    }
}

#[derive(serde::Serialize, serde::Deserialize)]
struct EndpointMetadata {
    version: u8,
    address: String,
    capability: String,
}

#[derive(serde::Serialize, serde::Deserialize)]
struct WireEnvelope {
    version: u8,
    capability: String,
    request: WireRequest,
}

#[derive(serde::Serialize, serde::Deserialize)]
struct WireRequest {
    cwd: String,
    paths: Vec<String>,
    activate: bool,
}

impl From<&LaunchRequest> for WireRequest {
    fn from(request: &LaunchRequest) -> Self {
        Self {
            cwd: request.cwd.to_string_lossy().into_owned(),
            paths: request
                .paths
                .iter()
                .map(|path| path.to_string_lossy().into_owned())
                .collect(),
            activate: request.activate,
        }
    }
}

impl TryFrom<WireRequest> for LaunchRequest {
    type Error = io::Error;

    fn try_from(request: WireRequest) -> Result<Self, Self::Error> {
        let request = Self {
            cwd: PathBuf::from(request.cwd),
            paths: request.paths.into_iter().map(PathBuf::from).collect(),
            activate: request.activate,
        };
        request.validate()?;
        Ok(request)
    }
}

fn listener_loop(
    listener: TcpListener,
    capability: String,
    sender: mpsc::Sender<LaunchRequest>,
    stop: Arc<AtomicBool>,
) {
    while !stop.load(Ordering::Acquire) {
        match listener.accept() {
            Ok((mut stream, _)) => {
                let _ = stream.set_read_timeout(Some(READ_TIMEOUT));
                let _ = stream.set_write_timeout(Some(READ_TIMEOUT));
                let accepted = receive_request(&mut stream, &capability).and_then(|request| {
                    sender
                        .send(request)
                        .map_err(|_| io::Error::new(io::ErrorKind::BrokenPipe, "receiver closed"))
                });
                let ack = if accepted.is_ok() {
                    ACK_OK
                } else {
                    ACK_REJECTED
                };
                let _ = stream.write_all(&[ack]);
            }
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => thread::sleep(POLL_INTERVAL),
            Err(_) => thread::sleep(POLL_INTERVAL),
        }
    }
}

fn receive_request(stream: &mut TcpStream, capability: &str) -> io::Result<LaunchRequest> {
    let mut length = [0_u8; 4];
    stream.read_exact(&mut length)?;
    let length = u32::from_be_bytes(length) as usize;
    if length == 0 || length > MAX_MESSAGE_BYTES {
        return Err(invalid_input("invalid launch request size"));
    }
    let mut bytes = vec![0; length];
    stream.read_exact(&mut bytes)?;
    let envelope: WireEnvelope = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
    if envelope.version != PROTOCOL_VERSION
        || !constant_time_eq(envelope.capability.as_bytes(), capability.as_bytes())
    {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "launch request authentication failed",
        ));
    }
    envelope.request.try_into()
}

fn forward_to_primary(metadata_path: &Path, request: LaunchRequest) -> io::Result<()> {
    let deadline = Instant::now() + STARTUP_RETRY;
    loop {
        match read_metadata(metadata_path).and_then(|metadata| send_request(&metadata, &request)) {
            Ok(()) => return Ok(()),
            Err(error) if Instant::now() >= deadline => return Err(error),
            Err(_) => thread::sleep(CONNECT_RETRY),
        }
    }
}

fn read_metadata(path: &Path) -> io::Result<EndpointMetadata> {
    let mut bytes = Vec::new();
    File::open(path)?
        .take((MAX_MESSAGE_BYTES + 1) as u64)
        .read_to_end(&mut bytes)?;
    if bytes.len() > MAX_MESSAGE_BYTES {
        return Err(invalid_data("instance metadata exceeds the size limit"));
    }
    let metadata: EndpointMetadata = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
    if metadata.version != PROTOCOL_VERSION {
        return Err(invalid_data("unsupported single-instance protocol version"));
    }
    let address = metadata
        .address
        .parse::<std::net::SocketAddr>()
        .map_err(|_| invalid_data("invalid instance endpoint address"))?;
    if !address.ip().is_loopback() {
        return Err(invalid_data(
            "instance endpoint must use a loopback address",
        ));
    }
    if metadata.capability.len() != 64
        || !metadata
            .capability
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit())
    {
        return Err(invalid_data("instance capability has an invalid format"));
    }
    Ok(metadata)
}

fn send_request(metadata: &EndpointMetadata, request: &LaunchRequest) -> io::Result<()> {
    let address = metadata
        .address
        .parse()
        .map_err(|_| invalid_data("invalid instance endpoint address"))?;
    let mut stream = TcpStream::connect_timeout(&address, READ_TIMEOUT)?;
    stream.set_read_timeout(Some(READ_TIMEOUT))?;
    stream.set_write_timeout(Some(READ_TIMEOUT))?;
    let envelope = WireEnvelope {
        version: PROTOCOL_VERSION,
        capability: metadata.capability.clone(),
        request: WireRequest::from(request),
    };
    let payload = serde_json::to_vec(&envelope).map_err(io::Error::other)?;
    if payload.is_empty() || payload.len() > MAX_MESSAGE_BYTES {
        return Err(invalid_input("launch request exceeds the size limit"));
    }
    stream.write_all(&(payload.len() as u32).to_be_bytes())?;
    stream.write_all(&payload)?;
    let mut ack = [0];
    stream.read_exact(&mut ack)?;
    if ack[0] == ACK_OK {
        Ok(())
    } else {
        Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "primary instance rejected launch request",
        ))
    }
}

fn write_metadata_atomically(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let temp_path = path.with_extension(format!("{}.tmp", std::process::id()));
    match fs::remove_file(&temp_path) {
        Ok(()) => {}
        Err(error) if error.kind() == io::ErrorKind::NotFound => {}
        Err(error) => return Err(error),
    }
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&temp_path)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);
    // The lock is already held, so no live owner can be publishing this file.
    // Removing a stale file first is required because Windows rename does not
    // replace an existing destination.
    match fs::remove_file(path) {
        Ok(()) => {}
        Err(error) if error.kind() == io::ErrorKind::NotFound => {}
        Err(error) => {
            let _ = fs::remove_file(&temp_path);
            return Err(error);
        }
    }
    match fs::rename(&temp_path, path) {
        Ok(()) => Ok(()),
        Err(error) => {
            let _ = fs::remove_file(&temp_path);
            Err(error)
        }
    }
}

fn profile_directory() -> io::Result<PathBuf> {
    #[cfg(windows)]
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "LOCALAPPDATA is not set"))?;
    #[cfg(not(windows))]
    let base = std::env::var_os("XDG_RUNTIME_DIR")
        .or_else(|| std::env::var_os("LOCALAPPDATA"))
        .or_else(|| std::env::var_os("HOME"))
        .map(PathBuf::from)
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "no user profile directory is available",
            )
        })?;
    Ok(base.join("RustText"))
}

fn random_capability() -> io::Result<String> {
    let mut bytes = [0_u8; 32];
    secure_random(&mut bytes)?;
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        write!(&mut output, "{byte:02x}").expect("writing to String cannot fail");
    }
    Ok(output)
}

#[cfg(windows)]
fn secure_random(output: &mut [u8]) -> io::Result<()> {
    const BCRYPT_USE_SYSTEM_PREFERRED_RNG: u32 = 0x00000002;
    #[link(name = "bcrypt")]
    unsafe extern "system" {
        fn BCryptGenRandom(
            algorithm: *mut core::ffi::c_void,
            buffer: *mut u8,
            length: u32,
            flags: u32,
        ) -> i32;
    }
    let length =
        u32::try_from(output.len()).map_err(|_| invalid_input("random buffer is too large"))?;
    // SAFETY: output is a valid writable buffer for `length` bytes; a null algorithm selects the system RNG.
    let status = unsafe {
        BCryptGenRandom(
            std::ptr::null_mut(),
            output.as_mut_ptr(),
            length,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    };
    if status >= 0 {
        Ok(())
    } else {
        Err(io::Error::from_raw_os_error(status))
    }
}

#[cfg(not(windows))]
fn secure_random(output: &mut [u8]) -> io::Result<()> {
    File::open("/dev/urandom")?.read_exact(output)
}

fn path_wire_len(path: &Path) -> usize {
    path.to_string_lossy().len()
}
fn invalid_input(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, message)
}
fn invalid_data(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
    let mut difference = left.len() ^ right.len();
    for index in 0..left.len().max(right.len()) {
        difference |= usize::from(
            left.get(index).copied().unwrap_or(0) ^ right.get(index).copied().unwrap_or(0),
        );
    }
    difference == 0
}

#[cfg(test)]
#[path = "../tests/unit/single_instance.rs"]
mod tests;
