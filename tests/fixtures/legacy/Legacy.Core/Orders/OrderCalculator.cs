using System;
using System.Configuration;

namespace Legacy.Core.Orders
{
    public class OrderCalculator
    {
        private readonly IOrderRepository _repository;

        public OrderCalculator(IOrderRepository repository)
        {
            _repository = repository;
        }

        public decimal GetTotal(int orderId)
        {
            var order = _repository.Find(orderId);
            if (order == null)
            {
                throw new ArgumentException("Order not found: " + orderId, "orderId");
            }

            return order.Subtotal() * (1 + TaxRate());
        }

        // Legacy on purpose: configuration read through System.Configuration (web.config / app.config).
        private static decimal TaxRate()
        {
            var configured = ConfigurationManager.AppSettings["TaxRate"];
            decimal rate;
            return decimal.TryParse(configured, out rate) ? rate : 0.1m;
        }
    }
}
