namespace bnbtradebot
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            lblBnbPrice = new Label();
            lblConnStatus = new Label();
            lblWalletSummary = new Label();
            gridBalances = new DataGridView();
            gridOpenBuys = new DataGridView();
            gridOpenSells = new DataGridView();
            lblProfit = new Label();
            btnStartStop = new Button();
            txtStepPercent = new TextBox();
            btnApplyQuote = new Button();
            ((System.ComponentModel.ISupportInitialize)gridBalances).BeginInit();
            ((System.ComponentModel.ISupportInitialize)gridOpenBuys).BeginInit();
            ((System.ComponentModel.ISupportInitialize)gridOpenSells).BeginInit();
            SuspendLayout();
            // 
            // lblBnbPrice
            // 
            lblBnbPrice.AutoSize = true;
            lblBnbPrice.Location = new Point(12, 9);
            lblBnbPrice.Name = "lblBnbPrice";
            lblBnbPrice.Size = new Size(38, 15);
            lblBnbPrice.TabIndex = 0;
            lblBnbPrice.Text = "label1";
            // 
            // lblConnStatus
            // 
            lblConnStatus.AutoSize = true;
            lblConnStatus.Location = new Point(12, 24);
            lblConnStatus.Name = "lblConnStatus";
            lblConnStatus.Size = new Size(38, 15);
            lblConnStatus.TabIndex = 1;
            lblConnStatus.Text = "label1";
            // 
            // lblWalletSummary
            // 
            lblWalletSummary.AutoSize = true;
            lblWalletSummary.Location = new Point(12, 39);
            lblWalletSummary.Name = "lblWalletSummary";
            lblWalletSummary.Size = new Size(38, 15);
            lblWalletSummary.TabIndex = 2;
            lblWalletSummary.Text = "label1";
            // 
            // gridBalances
            // 
            gridBalances.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            gridBalances.Location = new Point(12, 108);
            gridBalances.Name = "gridBalances";
            gridBalances.Size = new Size(568, 148);
            gridBalances.TabIndex = 3;
            // 
            // gridOpenBuys
            // 
            gridOpenBuys.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            gridOpenBuys.Location = new Point(11, 297);
            gridOpenBuys.Name = "gridOpenBuys";
            gridOpenBuys.Size = new Size(801, 472);
            gridOpenBuys.TabIndex = 5;
            // 
            // gridOpenSells
            // 
            gridOpenSells.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            gridOpenSells.Location = new Point(831, 297);
            gridOpenSells.Name = "gridOpenSells";
            gridOpenSells.Size = new Size(840, 472);
            gridOpenSells.TabIndex = 6;
            // 
            // lblProfit
            // 
            lblProfit.AutoSize = true;
            lblProfit.Location = new Point(12, 54);
            lblProfit.Name = "lblProfit";
            lblProfit.Size = new Size(38, 15);
            lblProfit.TabIndex = 7;
            lblProfit.Text = "label1";
            // 
            // btnStartStop
            // 
            btnStartStop.Location = new Point(1195, 137);
            btnStartStop.Name = "btnStartStop";
            btnStartStop.Size = new Size(75, 23);
            btnStartStop.TabIndex = 8;
            btnStartStop.Text = "Başlat";
            btnStartStop.UseVisualStyleBackColor = true;
            // 
            // txtStepPercent
            // 
            txtStepPercent.Location = new Point(1182, 108);
            txtStepPercent.Name = "txtStepPercent";
            txtStepPercent.Size = new Size(100, 23);
            txtStepPercent.TabIndex = 9;
            // 
            // btnApplyQuote
            // 
            btnApplyQuote.Location = new Point(1288, 108);
            btnApplyQuote.Name = "btnApplyQuote";
            btnApplyQuote.Size = new Size(75, 23);
            btnApplyQuote.TabIndex = 10;
            btnApplyQuote.Text = "Güncelle";
            btnApplyQuote.UseVisualStyleBackColor = true;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1683, 781);
            Controls.Add(btnApplyQuote);
            Controls.Add(txtStepPercent);
            Controls.Add(btnStartStop);
            Controls.Add(lblProfit);
            Controls.Add(gridOpenSells);
            Controls.Add(gridOpenBuys);
            Controls.Add(gridBalances);
            Controls.Add(lblWalletSummary);
            Controls.Add(lblConnStatus);
            Controls.Add(lblBnbPrice);
            Name = "Form1";
            Text = "Form1";
            Load += Form1_Load;
            ((System.ComponentModel.ISupportInitialize)gridBalances).EndInit();
            ((System.ComponentModel.ISupportInitialize)gridOpenBuys).EndInit();
            ((System.ComponentModel.ISupportInitialize)gridOpenSells).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label lblBnbPrice;
        private Label lblConnStatus;
        private Label lblWalletSummary;
        private DataGridView gridBalances;
        private DataGridView gridOpenBuys;
        private DataGridView gridOpenSells;
        private Label lblProfit;
        private Button btnStartStop;
        private TextBox txtStepPercent;
        private Button btnApplyQuote;
    }
}
