namespace Legacy.Desktop
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Button calculateButton;
        private System.Windows.Forms.Label totalLabel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.calculateButton = new System.Windows.Forms.Button();
            this.totalLabel = new System.Windows.Forms.Label();
            this.SuspendLayout();
            this.calculateButton.Location = new System.Drawing.Point(12, 12);
            this.calculateButton.Name = "calculateButton";
            this.calculateButton.Size = new System.Drawing.Size(100, 23);
            this.calculateButton.Text = "Calculate";
            this.calculateButton.Click += new System.EventHandler(this.calculateButton_Click);
            this.totalLabel.Location = new System.Drawing.Point(12, 48);
            this.totalLabel.Name = "totalLabel";
            this.totalLabel.Size = new System.Drawing.Size(200, 23);
            this.ClientSize = new System.Drawing.Size(284, 91);
            this.Controls.Add(this.calculateButton);
            this.Controls.Add(this.totalLabel);
            this.Name = "MainForm";
            this.Text = "Legacy Orders";
            this.ResumeLayout(false);
        }
    }
}
