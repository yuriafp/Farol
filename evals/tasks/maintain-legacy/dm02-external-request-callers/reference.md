Callers of Globals.GetExternalRequest outside Globals.cs:
- PayPalIPN (DNN Platform/Website/admin/Sales/PayPalIPN.aspx.cs)
- Purchase (DNN Platform/Website/admin/Sales/Purchase.ascx.cs)
- SecurityController (Dnn.AdminExperience/Dnn.PersonaBar.Extensions/Services/SecurityController.cs)
- ServerSummaryController (Dnn.AdminExperience/Library/Dnn.PersonaBar.UI/Services/ServerSummaryController.cs)

The `GetExternalRequest` calls in Services/Installer/Util.cs and InstallControllerImpl.cs are to Util's own overloads, not to Globals.
