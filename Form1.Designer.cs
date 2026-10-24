
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace HSHONAD_Launcher
{
    partial class Form1
    {
        private System.ComponentModel.IContainer? components = null;
        private Microsoft.Web.WebView2.WinForms.WebView2 webView = null!;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
            {
                components.Dispose();
            }

            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            webView = new Microsoft.Web.WebView2.WinForms.WebView2();

            ((System.ComponentModel.ISupportInitialize)webView).BeginInit();
            SuspendLayout();

            webView.AllowExternalDrop = false;
            webView.CreationProperties = null;
            webView.DefaultBackgroundColor =
                System.Drawing.Color.FromArgb(18, 18, 24);
            webView.Dock = System.Windows.Forms.DockStyle.Fill;
            webView.Name = "webView";
            webView.TabIndex = 0;

            AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(1280, 800);
            Controls.Add(webView);
            MinimumSize = new System.Drawing.Size(900, 600);
            Name = "Form1";
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            Text = "HSHONAD Launcher";

            Load += Form1_Load;

            ((System.ComponentModel.ISupportInitialize)webView).EndInit();
            ResumeLayout(false);
        }
    }
}
