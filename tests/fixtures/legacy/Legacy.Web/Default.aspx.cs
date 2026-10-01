using System;
using System.Web.UI;
using Legacy.Core.Orders;

namespace Legacy.Web
{
    public partial class _Default : Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                lblTotal.Text = string.Empty;
            }
        }

        // Referenced only from Default.aspx (OnClick): invisible to a C#-only reference search.
        protected void btnCalculate_Click(object sender, EventArgs e)
        {
            var calculator = new OrderCalculator(new InMemoryOrderRepository());
            lblTotal.Text = calculator.GetTotal(1).ToString("C");
        }

        // Called only from a <%: %> expression in Default.aspx.
        protected string Greeting()
        {
            return "Legacy Orders";
        }
    }
}
