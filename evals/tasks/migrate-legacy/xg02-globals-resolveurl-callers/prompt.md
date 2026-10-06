For the move off WebForms we need to replace DNN's `Globals.ResolveUrl(string url)` (namespace `DotNetNuke.Common`). Which source files outside the DotNetNuke.Library project call it? Calls to WebForms' own `Control.ResolveUrl` (such as `this.ResolveUrl(...)` or `Page.ResolveUrl(...)` in pages and controls) and other `ResolveUrl` methods don't count. Don't change any file.

Finish your reply with a code block tagged `callers` that lists the path of each of those files, one per line.
