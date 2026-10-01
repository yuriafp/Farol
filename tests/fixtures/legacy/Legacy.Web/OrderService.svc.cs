using Legacy.Core.Orders;

namespace Legacy.Web
{
    public class OrderService : IOrderService
    {
        public decimal GetTotal(int orderId)
        {
            return new OrderCalculator(new InMemoryOrderRepository()).GetTotal(orderId);
        }
    }
}
