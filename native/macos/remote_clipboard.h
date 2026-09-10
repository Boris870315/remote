#pragma once

#include <freerdp/client/cliprdr.h>

typedef struct remote_clipboard remote_clipboard;

remote_clipboard* remote_clipboard_new(void);
void remote_clipboard_free(remote_clipboard* clipboard);
void remote_clipboard_attach(remote_clipboard* clipboard, CliprdrClientContext* context);
void remote_clipboard_detach(remote_clipboard* clipboard, CliprdrClientContext* context);
void remote_clipboard_poll(remote_clipboard* clipboard);
void remote_clipboard_set_view_only(remote_clipboard* clipboard, int view_only);
