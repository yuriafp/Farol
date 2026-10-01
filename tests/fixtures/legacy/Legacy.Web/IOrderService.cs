using System.ServiceModel;

namespace Legacy.Web
{
    [ServiceContract]
    public interface IOrderService
    {
        [OperationContract]
        decimal GetTotal(int orderId);
    }
}
