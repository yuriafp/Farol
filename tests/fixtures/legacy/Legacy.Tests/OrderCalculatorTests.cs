using System;
using Legacy.Core.Orders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Legacy.Tests
{
    [TestClass]
    public class OrderCalculatorTests
    {
        [TestMethod]
        public void GetTotal_adds_the_default_tax()
        {
            var repository = new InMemoryOrderRepository();
            var order = new Order { Id = 1 };
            order.Lines.Add(new OrderLine { Sku = "A-1", Quantity = 2, UnitPrice = 50m });
            repository.Save(order);

            Assert.AreEqual(110m, new OrderCalculator(repository).GetTotal(1));
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void GetTotal_rejects_unknown_orders()
        {
            new OrderCalculator(new InMemoryOrderRepository()).GetTotal(42);
        }
    }

    [TestClass]
    public class InMemoryOrderRepositoryTests
    {
        [TestMethod]
        public void Find_returns_the_saved_order()
        {
            var repository = new InMemoryOrderRepository();
            repository.Save(new Order { Id = 7, Customer = "Contoso" });

            Assert.AreEqual("Contoso", repository.Find(7).Customer);
        }
    }
}
