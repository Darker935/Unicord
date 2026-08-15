use davey::{DaveSession, MediaType, ProposalsOperationType};
use std::num::NonZeroU16;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::slice;

pub struct Handle {
    session: DaveSession,
}

fn leak_vec(data: Vec<u8>, out: *mut *mut u8, out_len: *mut i32) {
    let mut data = data;
    data.shrink_to_fit();
    let len = data.len() as i32;
    let ptr = data.as_mut_ptr();
    std::mem::forget(data);
    unsafe {
        *out = ptr;
        *out_len = len;
    }
}

#[no_mangle]
pub extern "C" fn dave_create(protocol_version: u16, user_id: u64, channel_id: u64) -> *mut Handle {
    catch_unwind(|| {
        let version = match NonZeroU16::new(protocol_version) {
            Some(v) => v,
            None => return ptr::null_mut(),
        };
        match DaveSession::new(version, user_id, channel_id, None) {
            Ok(session) => Box::into_raw(Box::new(Handle { session })),
            Err(_) => ptr::null_mut(),
        }
    })
    .unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn dave_destroy(handle: *mut Handle) {
    if handle.is_null() {
        return;
    }
    drop(Box::from_raw(handle));
}

#[no_mangle]
pub unsafe extern "C" fn dave_free_buf(ptr: *mut u8, len: i32) {
    if ptr.is_null() || len < 0 {
        return;
    }
    let _ = Vec::from_raw_parts(ptr, len as usize, len as usize);
}

#[no_mangle]
pub unsafe extern "C" fn dave_set_external_sender(
    handle: *mut Handle,
    data: *const u8,
    len: i32,
) -> i32 {
    if handle.is_null() || data.is_null() || len < 0 {
        return -1;
    }
    let session = &mut (*handle).session;
    let bytes = slice::from_raw_parts(data, len as usize);
    catch_unwind(AssertUnwindSafe(|| session.set_external_sender(bytes).is_ok()))
        .ok()
        .and_then(|ok| ok.then_some(0))
        .unwrap_or(-1)
}

#[no_mangle]
pub unsafe extern "C" fn dave_create_key_package(
    handle: *mut Handle,
    out: *mut *mut u8,
    out_len: *mut i32,
) -> i32 {
    if handle.is_null() || out.is_null() || out_len.is_null() {
        return -1;
    }
    let session = &mut (*handle).session;
    match catch_unwind(AssertUnwindSafe(|| session.create_key_package())) {
        Ok(Ok(buf)) => {
            leak_vec(buf, out, out_len);
            0
        }
        _ => -1,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dave_process_proposals(
    handle: *mut Handle,
    op_type: u8,
    data: *const u8,
    len: i32,
    user_ids: *const u64,
    user_count: i32,
    out: *mut *mut u8,
    out_len: *mut i32,
) -> i32 {
    if handle.is_null() || data.is_null() || len < 0 || out.is_null() || out_len.is_null() {
        return -1;
    }
    let session = &mut (*handle).session;
    let bytes = slice::from_raw_parts(data, len as usize);
    let op = match op_type {
        0 => ProposalsOperationType::APPEND,
        1 => ProposalsOperationType::REVOKE,
        _ => return -1,
    };
    let ids = if user_ids.is_null() || user_count <= 0 {
        None
    } else {
        Some(slice::from_raw_parts(user_ids, user_count as usize))
    };
    match catch_unwind(AssertUnwindSafe(|| session.process_proposals(op, bytes, ids))) {
        Ok(Ok(Some(cw))) => {
            let mut combined = cw.commit;
            if let Some(welcome) = cw.welcome {
                combined.extend_from_slice(&welcome);
            }
            leak_vec(combined, out, out_len);
            0
        }
        Ok(Ok(None)) => {
            *out = ptr::null_mut();
            *out_len = 0;
            0
        }
        _ => -1,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dave_process_commit(handle: *mut Handle, data: *const u8, len: i32) -> i32 {
    if handle.is_null() || data.is_null() || len < 0 {
        return -1;
    }
    let session = &mut (*handle).session;
    let bytes = slice::from_raw_parts(data, len as usize);
    catch_unwind(AssertUnwindSafe(|| session.process_commit(bytes).is_ok()))
        .ok()
        .and_then(|ok| ok.then_some(0))
        .unwrap_or(-1)
}

#[no_mangle]
pub unsafe extern "C" fn dave_process_welcome(handle: *mut Handle, data: *const u8, len: i32) -> i32 {
    if handle.is_null() || data.is_null() || len < 0 {
        return -1;
    }
    let session = &mut (*handle).session;
    let bytes = slice::from_raw_parts(data, len as usize);
    catch_unwind(AssertUnwindSafe(|| session.process_welcome(bytes).is_ok()))
        .ok()
        .and_then(|ok| ok.then_some(0))
        .unwrap_or(-1)
}

#[no_mangle]
pub unsafe extern "C" fn dave_encrypt_opus(
    handle: *mut Handle,
    data: *const u8,
    len: i32,
    out: *mut *mut u8,
    out_len: *mut i32,
) -> i32 {
    if handle.is_null() || data.is_null() || len < 0 || out.is_null() || out_len.is_null() {
        return -1;
    }
    let session = &mut (*handle).session;
    let bytes = slice::from_raw_parts(data, len as usize);
    match catch_unwind(AssertUnwindSafe(|| session.encrypt_opus(bytes).map(|c| c.into_owned()))) {
        Ok(Ok(buf)) => {
            leak_vec(buf, out, out_len);
            0
        }
        _ => -1,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dave_decrypt(
    handle: *mut Handle,
    user_id: u64,
    data: *const u8,
    len: i32,
    out: *mut *mut u8,
    out_len: *mut i32,
) -> i32 {
    if handle.is_null() || data.is_null() || len < 0 || out.is_null() || out_len.is_null() {
        return -1;
    }
    let session = &mut (*handle).session;
    let bytes = slice::from_raw_parts(data, len as usize);
    match catch_unwind(AssertUnwindSafe(|| session.decrypt(user_id, MediaType::AUDIO, bytes))) {
        Ok(Ok(buf)) => {
            leak_vec(buf, out, out_len);
            0
        }
        _ => -1,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dave_is_ready(handle: *mut Handle) -> i32 {
    if handle.is_null() {
        return 0;
    }
    (*handle).session.is_ready() as i32
}

#[no_mangle]
pub unsafe extern "C" fn dave_set_passthrough(handle: *mut Handle, enabled: i32, expiry: u32) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        (*handle)
            .session
            .set_passthrough_mode(enabled != 0, Some(expiry));
    }));
}
