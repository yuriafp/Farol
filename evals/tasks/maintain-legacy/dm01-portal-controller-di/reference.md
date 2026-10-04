Only `PortalController` implements it (DNN Platform/Library/Entities/Portals/PortalController.cs, `ServiceLocator<IPortalController, PortalController>, IPortalController`).

In DNN Platform/DotNetNuke.Web/InternalServices, these receive it through their primary constructor:
- ControlBarController (ControlBarController.cs)
- ItemListServiceController (ItemListServiceController.cs)
- NewUserNotificationServiceController (NewUserNotificationServiceController.cs)
- ProfileServiceController (ProfileServiceController.cs)
