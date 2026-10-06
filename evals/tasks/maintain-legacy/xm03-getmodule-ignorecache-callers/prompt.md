We want to stop bypassing the module cache. Which source files call the overload `GetModule(int moduleId, int tabId, bool ignoreCache)` of `IModuleController`/`ModuleController` (namespace `DotNetNuke.Entities.Modules`), directly or through `ModuleController.Instance`, outside the DotNetNuke.Library project? Calls to the one- or two-argument `GetModule` or to other classes' `GetModule` methods don't count. Don't change any file.

Finish your reply with a code block tagged `callers` that lists the path of each of those files, one per line.
