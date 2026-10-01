using System;
using System.Windows.Forms;
using Legacy.Core.Orders;

namespace Legacy.Desktop
{
    public partial class MainForm : Form
    {
        public MainForm()
        {
            InitializeComponent();
        }

        private void calculateButton_Click(object sender, EventArgs e)
        {
            var calculator = new OrderCalculator(new InMemoryOrderRepository());
            totalLabel.Text = calculator.GetTotal(1).ToString("C");
        }
    }
}
