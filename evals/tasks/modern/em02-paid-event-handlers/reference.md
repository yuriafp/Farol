Three handlers implement `IIntegrationEventHandler<OrderStatusChangedToPaidIntegrationEvent>`:
- Catalog.API: src/Catalog.API/IntegrationEvents/EventHandling/OrderStatusChangedToPaidIntegrationEventHandler.cs (removes the stock)
- WebApp: src/WebApp/Services/OrderStatus/IntegrationEvents/EventHandling/OrderStatusChangedToPaidIntegrationEventHandler.cs (notifies the browser)
- Webhooks.API: src/Webhooks.API/IntegrationEvents/OrderStatusChangedToPaidIntegrationEventHandler.cs (sends webhooks)
