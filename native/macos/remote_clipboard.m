#import "remote_clipboard.h"

#import <AppKit/AppKit.h>
#include <freerdp/channels/cliprdr.h>
#include <freerdp/utils/cliprdr_utils.h>
#include <winpr/clipboard.h>
#include <winpr/file.h>
#include <winpr/shell.h>
#include <winpr/user.h>
#include <stdlib.h>
#include <stdatomic.h>
#include <string.h>

#define REMOTE_PNG_FORMAT 0xC001u
#define REMOTE_FILE_CHUNK (1024u * 1024u)

struct remote_clipboard {
    CliprdrClientContext* channel;
    wClipboard* converter;
    NSInteger pasteboard_change_count;
    UINT32 requested_format;
    char* requested_name;
    BOOL suppress_next_local_change;
    atomic_bool view_only;
    NSMutableArray<NSString*>* local_files;
    FILEDESCRIPTORW* remote_files;
    UINT32 remote_file_count, remote_file_index, remote_stream_id, remote_expected_chunk;
    UINT64 remote_file_offset;
    FILE* remote_output;
    NSString* remote_temp_root;
};

static const char* REMOTE_FILE_DESCRIPTOR = "FileGroupDescriptorW";

static void remote_clear_download(remote_clipboard* c) {
    if (c->remote_output) fclose(c->remote_output);
    c->remote_output = NULL;
    free(c->remote_files); c->remote_files = NULL;
    c->remote_file_count = c->remote_file_index = 0; c->remote_file_offset = 0;
    if (c->remote_temp_root) {
        [[NSFileManager defaultManager] removeItemAtPath:c->remote_temp_root error:nil];
        c->remote_temp_root = nil;
    }
}

static NSString* remote_descriptor_name(const FILEDESCRIPTORW* d) {
    NSUInteger n = 0; while (n < 260 && d->cFileName[n]) n++;
    NSString* raw = [[NSString alloc] initWithCharacters:(const unichar*)d->cFileName length:n];
    NSArray* parts = [raw componentsSeparatedByCharactersInSet:[NSCharacterSet characterSetWithCharactersInString:@"/\\"]];
    NSMutableArray* safe = [NSMutableArray array];
    for (NSString* p in parts) if (p.length && ![p isEqualToString:@"."] && ![p isEqualToString:@".."]) [safe addObject:p];
    return safe.count ? [NSString pathWithComponents:safe] : @"unnamed";
}

static void remote_add_local_tree(remote_clipboard* c, NSString* path) {
    [c->local_files addObject:path];
    BOOL dir = NO;
    if (![[NSFileManager defaultManager] fileExistsAtPath:path isDirectory:&dir] || !dir) return;
    NSArray* children = [[[NSFileManager defaultManager] contentsOfDirectoryAtPath:path error:nil] sortedArrayUsingSelector:@selector(localizedStandardCompare:)];
    for (NSString* child in children) remote_add_local_tree(c, [path stringByAppendingPathComponent:child]);
}

static void remote_fill_descriptor(FILEDESCRIPTORW* d, NSString* relative, NSString* path) {
    memset(d, 0, sizeof(*d));
    NSDictionary* a = [[NSFileManager defaultManager] attributesOfItemAtPath:path error:nil];
    BOOL dir = [a[NSFileType] isEqualToString:NSFileTypeDirectory];
    unsigned long long size = dir ? 0 : [a[NSFileSize] unsignedLongLongValue];
    d->dwFlags = FD_ATTRIBUTES | FD_FILESIZE;
    d->dwFileAttributes = dir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
    d->nFileSizeHigh = (DWORD)(size >> 32); d->nFileSizeLow = (DWORD)size;
    NSUInteger n = MIN((NSUInteger)259, relative.length);
    [relative getCharacters:(unichar*)d->cFileName range:NSMakeRange(0, n)]; d->cFileName[n] = 0;
}

static UINT remote_request_next_file_chunk(remote_clipboard* c);

static UINT remote_send_format_list(remote_clipboard* clipboard);

static UINT remote_clipboard_monitor_ready(CliprdrClientContext* context,
                                           const CLIPRDR_MONITOR_READY* ready) {
    (void)ready;
    remote_clipboard* clipboard = context ? context->custom : NULL;
    if (!clipboard) return ERROR_INVALID_PARAMETER;
    CLIPRDR_GENERAL_CAPABILITY_SET general = { 0 };
    general.capabilitySetType = CB_CAPSTYPE_GENERAL;
    general.capabilitySetLength = CB_CAPSTYPE_GENERAL_LEN;
    general.version = CB_CAPS_VERSION_2;
    general.generalFlags = CB_USE_LONG_FORMAT_NAMES | CB_STREAM_FILECLIP_ENABLED |
                           CB_FILECLIP_NO_FILE_PATHS | CB_HUGE_FILE_SUPPORT_ENABLED;
    CLIPRDR_CAPABILITIES capabilities = { 0 };
    capabilities.cCapabilitiesSets = 1;
    capabilities.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general;
    UINT rc = context->ClientCapabilities
        ? context->ClientCapabilities(context, &capabilities) : ERROR_INVALID_FUNCTION;
    if (rc != CHANNEL_RC_OK) return rc;
    clipboard->pasteboard_change_count = -1;
    remote_clipboard_poll(clipboard);
    return CHANNEL_RC_OK;
}

static UINT remote_clipboard_server_capabilities(CliprdrClientContext* context,
                                                  const CLIPRDR_CAPABILITIES* capabilities) {
    (void)context;
    (void)capabilities;
    return CHANNEL_RC_OK;
}

static UINT remote_send_format_list_response(CliprdrClientContext* context, BOOL ok) {
    CLIPRDR_FORMAT_LIST_RESPONSE response = { 0 };
    response.common.msgType = CB_FORMAT_LIST_RESPONSE;
    response.common.msgFlags = ok ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
    return context->ClientFormatListResponse
        ? context->ClientFormatListResponse(context, &response) : ERROR_INVALID_FUNCTION;
}

static UINT remote_request_format(remote_clipboard* clipboard, UINT32 id, const char* name) {
    if (!clipboard || !clipboard->channel || !clipboard->channel->ClientFormatDataRequest)
        return ERROR_INVALID_FUNCTION;
    free(clipboard->requested_name);
    clipboard->requested_name = name ? strdup(name) : NULL;
    clipboard->requested_format = id;
    CLIPRDR_FORMAT_DATA_REQUEST request = { 0 };
    request.common.msgType = CB_FORMAT_DATA_REQUEST;
    request.requestedFormatId = id;
    return clipboard->channel->ClientFormatDataRequest(clipboard->channel, &request);
}

static UINT remote_clipboard_server_format_list(CliprdrClientContext* context,
                                                 const CLIPRDR_FORMAT_LIST* list) {
    remote_clipboard* clipboard = context ? context->custom : NULL;
    if (!clipboard || !list) return ERROR_INVALID_PARAMETER;
    UINT rc = remote_send_format_list_response(context, TRUE);
    if (rc != CHANNEL_RC_OK) return rc;
    if (atomic_load(&clipboard->view_only)) return CHANNEL_RC_OK;

    const CLIPRDR_FORMAT* selected = NULL;
    for (UINT32 index = 0; index < list->numFormats; index++) {
        const CLIPRDR_FORMAT* format = &list->formats[index];
        if (format->formatName && strcasecmp(format->formatName, REMOTE_FILE_DESCRIPTOR) == 0) { selected = format; break; }
        if (format->formatName && strcasecmp(format->formatName, "PNG") == 0) {
            selected = format;
            break;
        }
        if (!selected && (format->formatId == CF_DIBV5 || format->formatId == CF_DIB))
            selected = format;
        if (!selected && format->formatId == CF_UNICODETEXT)
            selected = format;
    }
    return selected
        ? remote_request_format(clipboard, selected->formatId, selected->formatName)
        : CHANNEL_RC_OK;
}

static UINT remote_clipboard_server_format_list_response(
    CliprdrClientContext* context, const CLIPRDR_FORMAT_LIST_RESPONSE* response) {
    (void)context;
    (void)response;
    return CHANNEL_RC_OK;
}

static UINT remote_clipboard_server_data_request(CliprdrClientContext* context,
                                                 const CLIPRDR_FORMAT_DATA_REQUEST* request) {
    remote_clipboard* clipboard = context ? context->custom : NULL;
    if (!clipboard || !request || !context->ClientFormatDataResponse)
        return ERROR_INVALID_PARAMETER;
    if (atomic_load(&clipboard->view_only)) {
        CLIPRDR_FORMAT_DATA_RESPONSE denied = { 0 };
        denied.common.msgType = CB_FORMAT_DATA_RESPONSE;
        denied.common.msgFlags = CB_RESPONSE_FAIL;
        return context->ClientFormatDataResponse(context, &denied);
    }
    UINT32 size = 0;
    void* data = NULL;
    const char* name = ClipboardGetFormatName(clipboard->converter, request->requestedFormatId);
    if (name && strcmp(name, REMOTE_FILE_DESCRIPTOR) == 0 && clipboard->local_files.count) {
        UINT32 count = (UINT32)clipboard->local_files.count;
        FILEDESCRIPTORW* ds = calloc(count, sizeof(*ds));
        NSArray<NSURL*>* roots = [[NSPasteboard generalPasteboard] readObjectsForClasses:@[[NSURL class]] options:@{ NSPasteboardURLReadingFileURLsOnlyKey: @YES }];
        for (UINT32 i = 0; i < count; i++) {
            NSString* path = clipboard->local_files[i]; NSString* relative = path.lastPathComponent;
            for (NSURL* root in roots) {
                NSString* parent = root.path.stringByDeletingLastPathComponent;
                if ([path hasPrefix:[parent stringByAppendingString:@"/"]]) { relative = [path substringFromIndex:parent.length + 1]; break; }
            }
            remote_fill_descriptor(&ds[i], relative, path);
        }
        (void)cliprdr_serialize_file_list(ds, count, (BYTE**)&data, &size); free(ds);
    } else data = ClipboardGetData(clipboard->converter, request->requestedFormatId, &size);
    CLIPRDR_FORMAT_DATA_RESPONSE response = { 0 };
    response.common.msgType = CB_FORMAT_DATA_RESPONSE;
    response.common.msgFlags = data ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
    response.common.dataLen = data ? size : 0;
    response.requestedFormatData = data;
    UINT rc = context->ClientFormatDataResponse(context, &response);
    free(data);
    return rc;
}

static void remote_write_pasteboard(remote_clipboard* clipboard) {
    NSPasteboard* pasteboard = [NSPasteboard generalPasteboard];
    UINT32 size = 0;
    UINT32 png = ClipboardRegisterFormat(clipboard->converter, "PNG");
    void* png_data = ClipboardGetData(clipboard->converter, png, &size);
    if (png_data && size) {
        [pasteboard clearContents];
        [pasteboard setData:[NSData dataWithBytes:png_data length:size]
                    forType:NSPasteboardTypePNG];
        free(png_data);
        clipboard->pasteboard_change_count = pasteboard.changeCount;
        return;
    }
    free(png_data);

    UINT32 plain = ClipboardRegisterFormat(clipboard->converter, "text/plain");
    char* utf8 = ClipboardGetData(clipboard->converter, plain, &size);
    if (utf8 && size) {
        NSString* value = [[NSString alloc] initWithBytes:utf8
                                                   length:strnlen(utf8, size)
                                                 encoding:NSUTF8StringEncoding];
        if (value) {
            [pasteboard clearContents];
            [pasteboard setString:value forType:NSPasteboardTypeString];
            clipboard->pasteboard_change_count = pasteboard.changeCount;
        }
    }
    free(utf8);
}

static UINT remote_clipboard_server_data_response(
    CliprdrClientContext* context, const CLIPRDR_FORMAT_DATA_RESPONSE* response) {
    remote_clipboard* clipboard = context ? context->custom : NULL;
    if (!clipboard || !response) return ERROR_INVALID_PARAMETER;
    if (atomic_load(&clipboard->view_only)) return CHANNEL_RC_OK;
    if ((response->common.msgFlags & CB_RESPONSE_FAIL) ||
        !response->requestedFormatData || response->common.dataLen == 0)
        return ERROR_INTERNAL_ERROR;
    if (clipboard->requested_name && strcmp(clipboard->requested_name, REMOTE_FILE_DESCRIPTOR) == 0) {
        remote_clear_download(clipboard);
        UINT rc = cliprdr_parse_file_list(response->requestedFormatData, response->common.dataLen, &clipboard->remote_files, &clipboard->remote_file_count);
        if (rc != CHANNEL_RC_OK || !clipboard->remote_file_count || clipboard->remote_file_count > 4096) {
            remote_clear_download(clipboard);
            return ERROR_INTERNAL_ERROR;
        }
        clipboard->remote_temp_root = [NSTemporaryDirectory() stringByAppendingPathComponent:[@"RemoteClipboard-" stringByAppendingString:NSUUID.UUID.UUIDString]];
        if (![[NSFileManager defaultManager] createDirectoryAtPath:clipboard->remote_temp_root withIntermediateDirectories:YES attributes:nil error:nil]) return ERROR_INTERNAL_ERROR;
        return remote_request_next_file_chunk(clipboard);
    }
    ClipboardEmpty(clipboard->converter);
    UINT32 local_format = clipboard->requested_name
        ? ClipboardRegisterFormat(clipboard->converter, clipboard->requested_name)
        : clipboard->requested_format;
    if (!ClipboardSetData(clipboard->converter, local_format,
                          response->requestedFormatData, response->common.dataLen))
        return ERROR_INTERNAL_ERROR;
    remote_write_pasteboard(clipboard);
    return CHANNEL_RC_OK;
}

static UINT remote_clipboard_server_file_request(CliprdrClientContext* context, const CLIPRDR_FILE_CONTENTS_REQUEST* request) {
    remote_clipboard* c = context ? context->custom : NULL;
    if (!c || !request || request->listIndex >= c->local_files.count || !context->ClientFileContentsResponse) return ERROR_INVALID_PARAMETER;
    if (atomic_load(&c->view_only)) return ERROR_ACCESS_DENIED;
    NSString* path = c->local_files[request->listIndex]; BYTE* bytes = NULL;
    CLIPRDR_FILE_CONTENTS_RESPONSE response = { 0 }; response.common.msgType = CB_FILECONTENTS_RESPONSE; response.streamId = request->streamId;
    if (request->dwFlags & FILECONTENTS_SIZE) {
        UINT64 size = [[[[NSFileManager defaultManager] attributesOfItemAtPath:path error:nil] objectForKey:NSFileSize] unsignedLongLongValue];
        bytes = malloc(8); if (bytes) { memcpy(bytes, &size, 8); response.cbRequested = 8; }
    } else if (request->dwFlags & FILECONTENTS_RANGE) {
        FILE* f = fopen(path.fileSystemRepresentation, "rb");
        if (f) { UINT64 off = ((UINT64)request->nPositionHigh << 32) | request->nPositionLow; if (fseeko(f, (off_t)off, SEEK_SET) == 0) { bytes = malloc(request->cbRequested); if (bytes) response.cbRequested = (UINT32)fread(bytes, 1, request->cbRequested, f); } fclose(f); }
    }
    response.common.msgFlags = bytes ? CB_RESPONSE_OK : CB_RESPONSE_FAIL; response.common.dataLen = response.cbRequested; response.requestedData = bytes;
    UINT rc = context->ClientFileContentsResponse(context, &response); free(bytes); return rc;
}

static UINT remote_finish_remote_files(remote_clipboard* c) {
    NSMutableArray* urls = [NSMutableArray array];
    for (UINT32 i = 0; i < c->remote_file_count; i++) {
        NSString* relative = remote_descriptor_name(&c->remote_files[i]);
        if (relative.pathComponents.count == 1) [urls addObject:[NSURL fileURLWithPath:[c->remote_temp_root stringByAppendingPathComponent:relative]]];
    }
    NSPasteboard* p = [NSPasteboard generalPasteboard]; [p clearContents]; [p writeObjects:urls]; c->pasteboard_change_count = p.changeCount;
    return CHANNEL_RC_OK;
}

static UINT remote_request_next_file_chunk(remote_clipboard* c) {
    while (c->remote_file_index < c->remote_file_count) {
        FILEDESCRIPTORW* d = &c->remote_files[c->remote_file_index]; NSString* path = [c->remote_temp_root stringByAppendingPathComponent:remote_descriptor_name(d)];
        if (d->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) { [[NSFileManager defaultManager] createDirectoryAtPath:path withIntermediateDirectories:YES attributes:nil error:nil]; c->remote_file_index++; continue; }
        if (!c->remote_output) { [[NSFileManager defaultManager] createDirectoryAtPath:path.stringByDeletingLastPathComponent withIntermediateDirectories:YES attributes:nil error:nil]; c->remote_output = fopen(path.fileSystemRepresentation, "wb"); c->remote_file_offset = 0; if (!c->remote_output) return ERROR_INTERNAL_ERROR; }
        UINT64 size = ((UINT64)d->nFileSizeHigh << 32) | d->nFileSizeLow;
        if (c->remote_file_offset >= size) { fclose(c->remote_output); c->remote_output = NULL; c->remote_file_index++; c->remote_file_offset = 0; continue; }
        CLIPRDR_FILE_CONTENTS_REQUEST q = { 0 }; q.common.msgType = CB_FILECONTENTS_REQUEST; q.streamId = ++c->remote_stream_id; q.listIndex = c->remote_file_index; q.dwFlags = FILECONTENTS_RANGE; q.nPositionLow = (UINT32)c->remote_file_offset; q.nPositionHigh = (UINT32)(c->remote_file_offset >> 32); q.cbRequested = (UINT32)MIN((UINT64)REMOTE_FILE_CHUNK, size - c->remote_file_offset); c->remote_expected_chunk = q.cbRequested;
        return c->channel->ClientFileContentsRequest(c->channel, &q);
    }
    return remote_finish_remote_files(c);
}

static UINT remote_clipboard_server_file_response(CliprdrClientContext* context, const CLIPRDR_FILE_CONTENTS_RESPONSE* response) {
    remote_clipboard* c = context ? context->custom : NULL;
    if (c && atomic_load(&c->view_only)) return ERROR_ACCESS_DENIED;
    if (!c || !response || response->streamId != c->remote_stream_id || (response->common.msgFlags & CB_RESPONSE_FAIL) || !c->remote_output || !response->cbRequested || response->cbRequested > c->remote_expected_chunk) return ERROR_INTERNAL_ERROR;
    if (response->cbRequested && response->requestedData) { if (fwrite(response->requestedData, 1, response->cbRequested, c->remote_output) != response->cbRequested) return ERROR_WRITE_FAULT; c->remote_file_offset += response->cbRequested; }
    return remote_request_next_file_chunk(c);
}

static UINT remote_send_format_list(remote_clipboard* clipboard) {
    if (!clipboard || !clipboard->channel || !clipboard->channel->ClientFormatList)
        return ERROR_INVALID_FUNCTION;
    UINT32* ids = NULL;
    UINT32 count = ClipboardGetFormatIds(clipboard->converter, &ids);
    if (!count) {
        free(ids);
        return CHANNEL_RC_OK;
    }
    CLIPRDR_FORMAT* formats = calloc(count, sizeof(*formats));
    if (!formats) {
        free(ids);
        return CHANNEL_RC_NO_MEMORY;
    }
    for (UINT32 index = 0; index < count; index++) {
        formats[index].formatId = ids[index];
        const char* name = ClipboardGetFormatName(clipboard->converter, ids[index]);
        formats[index].formatName = (ids[index] > CF_MAX && name) ? (char*)name : NULL;
    }
    CLIPRDR_FORMAT_LIST list = { 0 };
    list.common.msgType = CB_FORMAT_LIST;
    list.numFormats = count;
    list.formats = formats;
    UINT rc = clipboard->channel->ClientFormatList(clipboard->channel, &list);
    free(formats);
    free(ids);
    return rc;
}

remote_clipboard* remote_clipboard_new(void) {
    remote_clipboard* clipboard = calloc(1, sizeof(*clipboard));
    if (!clipboard) return NULL;
    clipboard->converter = ClipboardCreate();
    clipboard->pasteboard_change_count = -1;
    if (!clipboard->converter) {
        free(clipboard);
        return NULL;
    }
    return clipboard;
}

void remote_clipboard_free(remote_clipboard* clipboard) {
    if (!clipboard) return;
    remote_clear_download(clipboard);
    free(clipboard->requested_name);
    ClipboardDestroy(clipboard->converter);
    free(clipboard);
}

void remote_clipboard_attach(remote_clipboard* clipboard, CliprdrClientContext* context) {
    if (!clipboard || !context) return;
    clipboard->channel = context;
    context->custom = clipboard;
    context->MonitorReady = remote_clipboard_monitor_ready;
    context->ServerCapabilities = remote_clipboard_server_capabilities;
    context->ServerFormatList = remote_clipboard_server_format_list;
    context->ServerFormatListResponse = remote_clipboard_server_format_list_response;
    context->ServerFormatDataRequest = remote_clipboard_server_data_request;
    context->ServerFormatDataResponse = remote_clipboard_server_data_response;
    context->ServerFileContentsRequest = remote_clipboard_server_file_request;
    context->ServerFileContentsResponse = remote_clipboard_server_file_response;
}

void remote_clipboard_detach(remote_clipboard* clipboard, CliprdrClientContext* context) {
    if (!clipboard || clipboard->channel != context) return;
    if (context) context->custom = NULL;
    clipboard->channel = NULL;
    remote_clear_download(clipboard);
}

void remote_clipboard_poll(remote_clipboard* clipboard) {
    if (!clipboard || !clipboard->channel || atomic_load(&clipboard->view_only)) return;
    @autoreleasepool {
        NSPasteboard* pasteboard = [NSPasteboard generalPasteboard];
        if (clipboard->pasteboard_change_count == pasteboard.changeCount) return;
        clipboard->pasteboard_change_count = pasteboard.changeCount;
        ClipboardEmpty(clipboard->converter);

        NSArray<NSURL*>* urls = [pasteboard readObjectsForClasses:@[[NSURL class]] options:@{ NSPasteboardURLReadingFileURLsOnlyKey: @YES }];
        if (urls.count) {
            clipboard->local_files = [NSMutableArray array];
            for (NSURL* url in urls) remote_add_local_tree(clipboard, url.path);
            UINT32 id = ClipboardRegisterFormat(clipboard->converter, REMOTE_FILE_DESCRIPTOR); const BYTE marker = 1;
            ClipboardSetData(clipboard->converter, id, &marker, 1);
        } else clipboard->local_files = nil;

        NSString* text = [pasteboard stringForType:NSPasteboardTypeString];
        if (text) {
            NSData* utf16 = [text dataUsingEncoding:NSUTF16LittleEndianStringEncoding];
            NSMutableData* terminated = [utf16 mutableCopy];
            const uint16_t zero = 0;
            [terminated appendBytes:&zero length:sizeof(zero)];
            ClipboardSetData(clipboard->converter, CF_UNICODETEXT,
                             terminated.bytes, (UINT32)terminated.length);
        }
        NSData* png = [pasteboard dataForType:NSPasteboardTypePNG];
        if (png) {
            UINT32 png_format = ClipboardRegisterFormat(clipboard->converter, "PNG");
            ClipboardSetData(clipboard->converter, png_format, png.bytes, (UINT32)png.length);
            UINT32 dib_size = 0;
            void* dib = ClipboardGetData(clipboard->converter, CF_DIB, &dib_size);
            if (dib && dib_size)
                ClipboardSetData(clipboard->converter, CF_DIB, dib, dib_size);
            free(dib);
        }
        (void)remote_send_format_list(clipboard);
    }
}

void remote_clipboard_set_view_only(remote_clipboard* clipboard, int view_only) {
    if (!clipboard) return;
    atomic_store(&clipboard->view_only, view_only != 0);
}
