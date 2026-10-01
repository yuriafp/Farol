namespace Legacy.Core.Orders
{
    public interface IOrderRepository
    {
        Order Find(int id);

        void Save(Order order);
    }
}
