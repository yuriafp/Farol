using System.Collections.Generic;

namespace Legacy.Core.Orders
{
    public class InMemoryOrderRepository : IOrderRepository
    {
        private readonly Dictionary<int, Order> _orders = new Dictionary<int, Order>();

        public Order Find(int id)
        {
            Order order;
            return _orders.TryGetValue(id, out order) ? order : null;
        }

        public void Save(Order order)
        {
            _orders[order.Id] = order;
        }
    }
}
