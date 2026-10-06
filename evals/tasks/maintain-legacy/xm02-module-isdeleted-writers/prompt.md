Something marks modules as deleted behind our back. Which source files assign `ModuleInfo.IsDeleted` (namespace `DotNetNuke.Entities.Modules`), in an assignment or an object initializer? Reading it doesn't count, and neither does setting `IsDeleted` on other types such as `TabInfo` or `UserInfo`. Look at the whole solution, tests included. Don't change any file.

Finish your reply with a code block tagged `writers` that lists the path of each of those files, one per line.
