using System;
using System.Collections.Generic;
using System.Linq;

namespace Legacy.Core.Orders
{
    [Serializable]
    public class Order
    {
        public int Id { get; set; }

        public string Customer { get; set; }

        public List<OrderLine> Lines { get; set; } = new List<OrderLine>();

        public decimal Subtotal()
        {
            return Lines.Sum(l => l.UnitPrice * l.Quantity);
        }
    }

    [Serializable]
    public class OrderLine
    {
        public string Sku { get; set; }

        public int Quantity { get; set; }

        public decimal UnitPrice { get; set; }
    }
}
