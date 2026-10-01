using System.Web.Services;
using Legacy.Core.Orders;

namespace Legacy.Web
{
    [WebService(Namespace = "http://legacy.example/orders")]
    [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
    public class LegacyService : WebService
    {
        [WebMethod]
        public decimal GetOrderTotal(int orderId)
        {
            return new OrderCalculator(new InMemoryOrderRepository()).GetTotal(orderId);
        }
    }
}
